using System.Text;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.Application.Testing;

/// <summary>
/// Deterministic stand-ins for the GPU stages.
///
/// These exist for exactly two reasons: the "Fake AI 모드" diagnostic switch, which lets a user verify
/// that scanning, queueing, checkpointing and SRT writing work on their machine before downloading
/// several gigabytes of models, and the integration tests, which must run without a GPU.
///
/// They are never selected unless <c>AppSettings.FakeAiMode</c> is explicitly enabled, and every
/// subtitle they produce is visibly marked so a fake result can never be mistaken for a real one.
/// </summary>
public static class FakeMarkers
{
    /// <summary>Prefix stamped onto every fake translation so the output is unmistakable.</summary>
    public const string TranslationPrefix = "[테스트] ";

    public const string ModelId = "fake";
}

/// <summary>
/// Writes a real, playable 16 kHz mono PCM WAV containing silence.
/// Used when FFmpeg is unavailable; the file is structurally valid so the rest of the chain is
/// exercised for real rather than skipped.
/// </summary>
public sealed class FakeAudioExtractor(IFileSystem fileSystem, double defaultDurationSeconds = 30d) : IAudioExtractor
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly double _defaultDurationSeconds = defaultDurationSeconds;

    public async Task ExtractAsync(
        AudioExtractionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_fileSystem.FileExists(request.VideoPath))
        {
            throw new FileNotFoundException("원본 영상을 찾을 수 없습니다.", request.VideoPath);
        }

        var directory = Path.GetDirectoryName(request.OutputWavPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }

        var sampleCount = (int)(request.SampleRate * _defaultDurationSeconds);
        var dataBytes = sampleCount * 2 * request.Channels;

        var temp = request.OutputWavPath + ".tmp";
        if (_fileSystem.FileExists(temp))
        {
            _fileSystem.Delete(temp);
        }

        await using (var stream = _fileSystem.CreateNew(temp))
        await using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            WriteWavHeader(writer, request.SampleRate, request.Channels, dataBytes);

            var chunk = new byte[request.SampleRate * 2 * request.Channels];
            var written = 0;

            while (written < dataBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var size = Math.Min(chunk.Length, dataBytes - written);
                stream.Write(chunk, 0, size);
                written += size;
                progress?.Report(written * 100d / dataBytes);
            }
        }

        _fileSystem.Move(temp, request.OutputWavPath, overwrite: true);
        progress?.Report(100d);
    }

    private static void WriteWavHeader(BinaryWriter writer, int sampleRate, int channels, int dataBytes)
    {
        const short BitsPerSample = 16;
        var byteRate = sampleRate * channels * BitsPerSample / 8;
        var blockAlign = (short)(channels * BitsPerSample / 8);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(BitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
    }
}

/// <summary>
/// Produces a deterministic transcript whose length follows the real duration of the WAV it is given.
/// Seeded from the file path so re-running a job yields identical output, which is what makes
/// checkpoint-resume assertions meaningful.
/// </summary>
public sealed class FakeTranscriber(IFileSystem fileSystem) : ITranscriber
{
    private readonly IFileSystem _fileSystem = fileSystem;

    private static readonly string[] Sentences =
    [
        "I didn't expect you to come here.",
        "We need to leave before sunrise.",
        "Something is wrong with the engine.",
        "Tell me what you saw that night.",
        "There is no time left to argue.",
        "She never mentioned any of this.",
        "The signal is coming from the north tower.",
        "Keep everyone away from the door."
    ];

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_fileSystem.FileExists(request.AudioPath))
        {
            throw new FileNotFoundException("추출된 음성 파일을 찾을 수 없습니다.", request.AudioPath);
        }

        var duration = request.DurationSeconds is > 0
            ? request.DurationSeconds.Value
            : EstimateWavDuration(request.AudioPath);

        var segmentCount = Math.Max(1, (int)Math.Round(duration / 3.5d));
        var segments = new List<TranscriptionSegment>(segmentCount);
        var seed = Math.Abs(request.AudioPath.GetHashCode(StringComparison.Ordinal));

        for (var i = 0; i < segmentCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = i * (duration / segmentCount);
            var end = Math.Min(duration, (i + 1) * (duration / segmentCount));
            var text = Sentences[(seed + i) % Sentences.Length];

            segments.Add(new TranscriptionSegment
            {
                Id = i + 1,
                Start = Math.Round(start, 3),
                End = Math.Round(end, 3),
                Text = text,
                Words = BuildWords(text, start, end)
            });

            progress?.Report((i + 1) * 100d / segmentCount);

            if (i % 16 == 0)
            {
                await Task.Yield();
            }
        }

        return new TranscriptionResult
        {
            SourceLanguage = request.Language is "auto" or "" ? "en" : request.Language,
            LanguageProbability = 0.99d,
            Segments = segments,
            ModelId = FakeMarkers.ModelId,
            DurationSeconds = duration
        };
    }

    private static IReadOnlyList<WordTimestamp> BuildWords(string text, double start, double end)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var step = (end - start) / Math.Max(1, words.Length);

        return words
            .Select((w, i) => new WordTimestamp(
                i == 0 ? w : " " + w,
                Math.Round(start + (i * step), 3),
                Math.Round(start + ((i + 1) * step), 3),
                0.95d))
            .ToArray();
    }

    /// <summary>Reads the WAV data chunk size so the fake transcript matches the real audio length.</summary>
    private double EstimateWavDuration(string path)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var riff = new string(reader.ReadChars(4));
            if (riff != "RIFF")
            {
                return 30d;
            }

            reader.ReadInt32();
            reader.ReadChars(4);

            var sampleRate = 16_000;
            var channels = 1;
            short bitsPerSample = 16;

            while (stream.Position < stream.Length - 8)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();

                if (chunkId == "fmt ")
                {
                    reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();

                    var remaining = chunkSize - 16;
                    if (remaining > 0)
                    {
                        stream.Seek(remaining, SeekOrigin.Current);
                    }
                }
                else if (chunkId == "data")
                {
                    var bytesPerSecond = sampleRate * channels * bitsPerSample / 8;
                    return bytesPerSecond <= 0 ? 30d : Math.Max(1d, chunkSize / (double)bytesPerSecond);
                }
                else
                {
                    stream.Seek(chunkSize, SeekOrigin.Current);
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return 30d;
    }
}

/// <summary>
/// Returns one Korean-marked line per input id. Honours the id contract exactly, so the
/// <see cref="TranslationValidator"/> path is genuinely exercised by the integration tests.
/// </summary>
public sealed class FakeTranslationEngine : ITranslationEngine
{
    private static readonly Dictionary<string, string> Phrasebook = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I didn't expect you to come here."] = "네가 여기 올 줄은 몰랐어.",
        ["We need to leave before sunrise."] = "해 뜨기 전에 떠나야 해.",
        ["Something is wrong with the engine."] = "엔진에 문제가 생겼어.",
        ["Tell me what you saw that night."] = "그날 밤 무엇을 봤는지 말해 줘.",
        ["There is no time left to argue."] = "말다툼할 시간이 없어.",
        ["She never mentioned any of this."] = "그녀는 이런 이야기를 한 번도 하지 않았어.",
        ["The signal is coming from the north tower."] = "신호는 북쪽 탑에서 오고 있어.",
        ["Keep everyone away from the door."] = "모두 문에서 떨어뜨려 놔."
    };

    public Task<IReadOnlyList<TranslatedSubtitleItem>> TranslateAsync(
        IReadOnlyList<SubtitleItem> items,
        TranslationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<TranslatedSubtitleItem>(items.Count);

        foreach (var item in items)
        {
            var body = Phrasebook.TryGetValue(item.Text.Trim(), out var known)
                ? known
                : $"{item.Text}";

            foreach (var (term, replacement) in context.Glossary)
            {
                body = body.Replace(term, replacement, StringComparison.OrdinalIgnoreCase);
            }

            result.Add(new TranslatedSubtitleItem(item.Id, FakeMarkers.TranslationPrefix + body));
        }

        return Task.FromResult<IReadOnlyList<TranslatedSubtitleItem>>(result);
    }
}

/// <summary>Fixed hardware profile for tests that must exercise the recommendation policy.</summary>
public sealed class FakeHardwareDetector(HardwareProfile? profile = null) : IHardwareDetector
{
    private readonly HardwareProfile _profile = profile ?? Default;

    public static HardwareProfile Default { get; } = new()
    {
        Gpus =
        [
            new GpuInfo
            {
                Name = "NVIDIA GeForce RTX 4070",
                Index = 0,
                TotalVramBytes = 12L * 1024 * 1024 * 1024,
                FreeVramBytes = 11L * 1024 * 1024 * 1024,
                DriverVersion = "560.00",
                ComputeCapability = "8.9"
            }
        ],
        CudaAvailable = true,
        CudaVersion = "12.4",
        CpuName = "Test CPU",
        LogicalCoreCount = 16,
        TotalRamBytes = 32L * 1024 * 1024 * 1024,
        AvailableRamBytes = 20L * 1024 * 1024 * 1024,
        FreeDiskBytes = 500L * 1024 * 1024 * 1024,
        DiskRoot = "C:\\"
    };

    /// <summary>A machine with no usable GPU, for exercising the CPU fallback path.</summary>
    public static HardwareProfile CpuOnly { get; } = new()
    {
        Gpus = [],
        CudaAvailable = false,
        CpuName = "Test CPU",
        LogicalCoreCount = 8,
        TotalRamBytes = 16L * 1024 * 1024 * 1024,
        AvailableRamBytes = 8L * 1024 * 1024 * 1024,
        FreeDiskBytes = 100L * 1024 * 1024 * 1024
    };

    public Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_profile);
}

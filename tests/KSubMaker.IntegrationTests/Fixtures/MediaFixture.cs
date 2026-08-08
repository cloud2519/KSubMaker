using System.Security.Cryptography;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Fixtures;

/// <summary>
/// Generates the test media once with the real ffmpeg on the machine.
///
/// Encoders are probed rather than assumed: a distro ffmpeg without libx264/aac still produces usable
/// files via mpeg4/libmp3lame. When ffmpeg is missing entirely the fixture reports
/// <see cref="Available"/> = false and every test in the collection skips with a clear message.
/// </summary>
public sealed class MediaFixture : IAsyncLifetime
{
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(120);

    private readonly TempWorkspace _workspace = new("ksubmaker-media");

    public const double NominalDurationSeconds = 8d;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public string Root => _workspace.Root;

    /// <summary>8 s of 440 Hz tone over a test pattern; one audio track.</summary>
    public string SampleVideo { get; private set; } = string.Empty;

    /// <summary>Video only — used to prove <c>HasAudioTrack == false</c>.</summary>
    public string NoAudioVideo { get; private set; } = string.Empty;

    /// <summary>Two audio tracks, so the track picker and <c>-map 0:a:n</c> have something to select.</summary>
    public string TwoAudioVideo { get; private set; } = string.Empty;

    /// <summary>A valid video living under a Korean directory name with spaces.</summary>
    public string KoreanPathVideo { get; private set; } = string.Empty;

    /// <summary>Random bytes with an .mp4 extension: ffprobe must fail cleanly on it.</summary>
    public string CorruptVideo { get; private set; } = string.Empty;

    public string VideoEncoder { get; private set; } = "libx264";

    public string AudioEncoder { get; private set; } = "aac";

    public async Task InitializeAsync()
    {
        if (!ExternalTools.FfmpegAvailable)
        {
            SkipReason = ExternalTools.FfmpegSkipReason;
            return;
        }

        VideoEncoder = ExternalTools.PickEncoder("libx264", "mpeg4", "libxvid", "mjpeg");
        AudioEncoder = ExternalTools.PickEncoder("aac", "libmp3lame", "libvorbis", "pcm_s16le");

        SampleVideo = _workspace.Combine("sample.mp4");
        NoAudioVideo = _workspace.Combine("no-audio.mp4");
        TwoAudioVideo = _workspace.Combine("two-audio.mkv");
        CorruptVideo = _workspace.Combine("corrupt.mp4");

        var koreanDirectory = _workspace.CreateSubdirectory("한국어 영상 폴더");
        KoreanPathVideo = Path.Combine(koreanDirectory, "테스트 영상 (최종).mp4");

        await EncodeSampleAsync(SampleVideo).ConfigureAwait(false);
        await EncodeSampleAsync(KoreanPathVideo).ConfigureAwait(false);
        await EncodeSilentVideoAsync(NoAudioVideo).ConfigureAwait(false);
        await EncodeTwoAudioTracksAsync(TwoAudioVideo).ConfigureAwait(false);

        WriteCorruptFile(CorruptVideo);

        Available = true;
    }

    public Task DisposeAsync()
    {
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Copies one of the generated files so a test can mutate or delete it freely.</summary>
    public string CopyTo(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    // -----------------------------------------------------------------------
    // encoding
    // -----------------------------------------------------------------------

    private async Task EncodeSampleAsync(string output)
    {
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={NominalDurationSeconds}",
            "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={NominalDurationSeconds}",
            "-shortest",
            "-c:v", VideoEncoder,
            "-pix_fmt", "yuv420p",
            "-c:a", AudioEncoder,
            output
        ];

        await RunFfmpegAsync(arguments, output).ConfigureAwait(false);
    }

    private async Task EncodeSilentVideoAsync(string output)
    {
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={NominalDurationSeconds}",
            "-an",
            "-c:v", VideoEncoder,
            "-pix_fmt", "yuv420p",
            output
        ];

        await RunFfmpegAsync(arguments, output).ConfigureAwait(false);
    }

    private async Task EncodeTwoAudioTracksAsync(string output)
    {
        string[] arguments =
        [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={NominalDurationSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={NominalDurationSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=880:duration={NominalDurationSeconds}",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
            "-shortest",
            "-c:v", VideoEncoder,
            "-pix_fmt", "yuv420p",
            "-c:a", AudioEncoder,
            "-metadata:s:a:0", "language=eng",
            "-metadata:s:a:1", "language=kor",
            output
        ];

        await RunFfmpegAsync(arguments, output).ConfigureAwait(false);
    }

    private static void WriteCorruptFile(string path)
    {
        var bytes = new byte[64 * 1024];
        RandomNumberGenerator.Fill(bytes);

        // Make sure the first bytes cannot accidentally look like a valid container header.
        bytes[0] = 0x7F;
        bytes[1] = 0x00;
        bytes[2] = 0x13;
        bytes[3] = 0x37;

        File.WriteAllBytes(path, bytes);
    }

    private static async Task RunFfmpegAsync(IReadOnlyList<string> arguments, string output)
    {
        var (exitCode, _, stderr) = await ExternalTools
            .RunAsync(ExternalTools.FfmpegPath, arguments, EncodeTimeout)
            .ConfigureAwait(false);

        if (exitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            throw new InvalidOperationException(
                $"테스트용 영상을 생성하지 못했습니다 ({output}, exit {exitCode}):{Environment.NewLine}{Tail(stderr)}");
        }
    }

    private static string Tail(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(10));
    }
}

/// <summary>
/// Shares one <see cref="MediaFixture"/> across the media-driven test classes: encoding five files per
/// class would cost more than the tests themselves. Tests inside the collection run sequentially,
/// which also keeps the ffmpeg/ffprobe invocations deterministic.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MediaCollection : ICollectionFixture<MediaFixture>
{
    public const string Name = "media";
}

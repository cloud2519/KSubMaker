using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using KSubMaker.Infrastructure.Media;
using KSubMaker.IntegrationTests.Fixtures;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Pipeline;

/// <summary>Real <see cref="FfmpegAudioExtractor"/> producing a real WAV, verified by probing it back.</summary>
[Collection(MediaCollection.Name)]
public sealed class AudioExtractionTests(MediaFixture media) : IDisposable
{
    private readonly TempWorkspace _workspace = new("ksubmaker-audio");

    public void Dispose() => _workspace.Dispose();

    private PipelineHarness NewHarness() => new(_workspace);

    private static async Task<JsonElement> ProbeJsonAsync(string path)
    {
        var (exitCode, stdout, stderr) = await ExternalTools.RunAsync(
            ExternalTools.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            TimeSpan.FromSeconds(60));

        exitCode.Should().Be(0, stderr);

        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    [RequiresFfmpegFact]
    public async Task The_extracted_wav_is_sixteen_kilohertz_mono_pcm_s16le()
    {
        await using var harness = NewHarness();

        var output = Path.Combine(_workspace.CreateSubdirectory("out"), "audio.wav");

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.SampleVideo, OutputWavPath = output },
            null,
            CancellationToken.None);

        File.Exists(output).Should().BeTrue();
        new FileInfo(output).Length.Should().BeGreaterThan(1024);

        var root = await ProbeJsonAsync(output);
        var stream = root.GetProperty("streams").EnumerateArray().Single();

        stream.GetProperty("codec_name").GetString().Should().Be("pcm_s16le");
        stream.GetProperty("channels").GetInt32().Should().Be(1);
        stream.GetProperty("sample_rate").GetString().Should().Be("16000");

        var duration = double.Parse(
            root.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);

        duration.Should().BeApproximately(MediaFixture.NominalDurationSeconds, 1d);
    }

    [RequiresFfmpegFact]
    public async Task The_temporary_file_is_gone_once_extraction_succeeds()
    {
        await using var harness = NewHarness();

        var directory = _workspace.CreateSubdirectory("tmp-check");
        var output = Path.Combine(directory, "audio.wav");

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.SampleVideo, OutputWavPath = output },
            null,
            CancellationToken.None);

        File.Exists(output + ".tmp").Should().BeFalse();
        Directory.GetFiles(directory).Should().ContainSingle().Which.Should().Be(output);
    }

    [RequiresFfmpegFact]
    public async Task Progress_is_reported_and_ends_at_one_hundred()
    {
        await using var harness = NewHarness();

        var output = Path.Combine(_workspace.CreateSubdirectory("progress"), "audio.wav");
        var reports = new List<double>();

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.SampleVideo, OutputWavPath = output },
            new Progress<double>(p =>
            {
                lock (reports)
                {
                    reports.Add(p);
                }
            }),
            CancellationToken.None);

        lock (reports)
        {
            reports.Should().NotBeEmpty();
            reports.Should().OnlyContain(p => p >= 0d && p <= 100d);
            reports[^1].Should().Be(100d);
        }
    }

    [RequiresFfmpegFact]
    public async Task A_specific_audio_track_can_be_selected()
    {
        await using var harness = NewHarness();

        var first = Path.Combine(_workspace.CreateSubdirectory("track0"), "audio.wav");
        var second = Path.Combine(_workspace.CreateSubdirectory("track1"), "audio.wav");

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.TwoAudioVideo, OutputWavPath = first, AudioTrackIndex = 0 },
            null, CancellationToken.None);

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.TwoAudioVideo, OutputWavPath = second, AudioTrackIndex = 1 },
            null, CancellationToken.None);

        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();

        // Different tones (440 Hz vs 880 Hz) must not produce byte-identical PCM.
        File.ReadAllBytes(first).Should().NotEqual(File.ReadAllBytes(second));
    }

    [RequiresFfmpegFact]
    public async Task A_korean_path_with_spaces_is_extracted_correctly()
    {
        await using var harness = NewHarness();

        var directory = _workspace.CreateSubdirectory("한국어 출력 폴더");
        var output = Path.Combine(directory, "추출된 음성.wav");

        await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.KoreanPathVideo, OutputWavPath = output },
            null,
            CancellationToken.None);

        File.Exists(output).Should().BeTrue();

        var root = await ProbeJsonAsync(output);
        root.GetProperty("streams").EnumerateArray().Single()
            .GetProperty("codec_name").GetString().Should().Be("pcm_s16le");
    }

    [RequiresFfmpegFact]
    public async Task A_missing_source_file_throws_VIDEO_NOT_FOUND()
    {
        await using var harness = NewHarness();

        var act = async () => await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest
            {
                VideoPath = Path.Combine(_workspace.Root, "gone.mkv"),
                OutputWavPath = Path.Combine(_workspace.Root, "gone.wav")
            },
            null,
            CancellationToken.None);

        (await act.Should().ThrowAsync<AudioExtractionException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.VideoNotFound);
    }

    [RequiresFfmpegFact]
    public async Task A_video_without_audio_throws_AUDIO_TRACK_NOT_FOUND_and_leaves_no_temp_file()
    {
        await using var harness = NewHarness();

        var directory = _workspace.CreateSubdirectory("no-audio");
        var output = Path.Combine(directory, "audio.wav");

        var act = async () => await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.NoAudioVideo, OutputWavPath = output },
            null,
            CancellationToken.None);

        (await act.Should().ThrowAsync<AudioExtractionException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.AudioTrackNotFound);

        Directory.GetFiles(directory).Should().BeEmpty("a failed extraction must not leave a partial wav");
    }

    [RequiresFfmpegFact]
    public async Task A_corrupt_source_fails_and_leaves_no_temp_file()
    {
        await using var harness = NewHarness();

        var directory = _workspace.CreateSubdirectory("corrupt");
        var output = Path.Combine(directory, "audio.wav");

        var act = async () => await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.CorruptVideo, OutputWavPath = output },
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<AudioExtractionException>();

        Directory.GetFiles(directory).Should().BeEmpty();
    }

    [RequiresFfmpegFact]
    public async Task Cancelling_extraction_removes_the_partial_output()
    {
        await using var harness = NewHarness();

        var directory = _workspace.CreateSubdirectory("cancelled");
        var output = Path.Combine(directory, "audio.wav");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await harness.RealAudioExtractor.ExtractAsync(
            new AudioExtractionRequest { VideoPath = media.SampleVideo, OutputWavPath = output },
            null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        File.Exists(output).Should().BeFalse();
        File.Exists(output + ".tmp").Should().BeFalse();
    }
}

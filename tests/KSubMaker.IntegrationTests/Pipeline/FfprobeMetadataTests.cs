using FluentAssertions;
using KSubMaker.Domain.Media;
using KSubMaker.IntegrationTests.Fixtures;
using KSubMaker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.IntegrationTests.Pipeline;

/// <summary>Real <see cref="KSubMaker.Infrastructure.Media.FfprobeMediaProbe"/> against real files.</summary>
[Collection(MediaCollection.Name)]
public sealed class FfprobeMetadataTests(MediaFixture media) : IDisposable
{
    private readonly TempWorkspace _workspace = new("ksubmaker-ffprobe");

    public void Dispose() => _workspace.Dispose();

    private KSubMaker.Infrastructure.Media.FfprobeMediaProbe NewProbe()
    {
        var paths = new KSubMaker.Infrastructure.Paths.AppPaths(Path.Combine(_workspace.Root, "appdata"));
        var locator = new KSubMaker.Worker.Tools.ToolLocator(paths, NullLogger<KSubMaker.Worker.Tools.ToolLocator>.Instance);
        return new KSubMaker.Infrastructure.Media.FfprobeMediaProbe(
            locator, NullLogger<KSubMaker.Infrastructure.Media.FfprobeMediaProbe>.Instance);
    }

    private static VideoFile Describe(string path)
    {
        var info = new FileInfo(path);

        return new VideoFile
        {
            FullPath = path,
            FileName = info.Name,
            Extension = info.Extension,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc
        };
    }

    [RequiresFfmpegFact]
    public async Task The_duration_is_read_within_tolerance()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.SampleVideo));

        probed.Probed.Should().BeTrue();
        probed.ProbeError.Should().BeNull();
        probed.DurationSeconds.Should().BeApproximately(MediaFixture.NominalDurationSeconds, 0.75d);
    }

    [RequiresFfmpegFact]
    public async Task A_single_audio_track_is_reported()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.SampleVideo));

        probed.HasAudioTrack.Should().BeTrue();
        probed.AudioTracks.Should().ContainSingle();
        probed.AudioTracks[0].Index.Should().Be(0, "the index must be the audio-relative ordinal for -map 0:a:n");
        probed.AudioTracks[0].Codec.Should().NotBeNullOrWhiteSpace();
        probed.AudioTracks[0].Channels.Should().BeGreaterThan(0);
    }

    [RequiresFfmpegFact]
    public async Task Two_audio_tracks_are_reported_with_consecutive_relative_indexes()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.TwoAudioVideo));

        probed.HasAudioTrack.Should().BeTrue();
        probed.AudioTracks.Should().HaveCount(2);
        probed.AudioTracks.Select(t => t.Index).Should().Equal(0, 1);
        probed.AudioTracks.Select(t => t.Language).Should().Equal("eng", "kor");
    }

    [RequiresFfmpegFact]
    public async Task Track_display_names_are_korean_friendly()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.TwoAudioVideo));

        probed.AudioTracks[0].DisplayName.Should().StartWith("#0 eng");
    }

    [RequiresFfmpegFact]
    public async Task A_video_without_audio_reports_no_audio_track()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.NoAudioVideo));

        probed.Probed.Should().BeTrue();
        probed.ProbeError.Should().BeNull();
        probed.HasAudioTrack.Should().BeFalse();
        probed.AudioTracks.Should().BeEmpty();
        probed.DurationSeconds.Should().BeGreaterThan(0d);
    }

    [RequiresFfmpegFact]
    public async Task A_corrupt_file_sets_ProbeError_without_throwing()
    {
        var probe = NewProbe();

        var act = async () => await probe.ProbeAsync(Describe(media.CorruptVideo));

        await act.Should().NotThrowAsync("one bad file in a folder of 500 must not abort the scan");

        var probed = await probe.ProbeAsync(Describe(media.CorruptVideo));

        probed.Probed.Should().BeTrue();
        probed.ProbeError.Should().NotBeNullOrWhiteSpace();
        probed.HasAudioTrack.Should().BeFalse();
        probed.AudioTracks.Should().BeEmpty();
        probed.SubtitleTracks.Should().BeEmpty();
    }

    [RequiresFfmpegFact]
    public async Task A_missing_file_sets_ProbeError_without_throwing()
    {
        var missing = Path.Combine(_workspace.Root, "does-not-exist.mkv");

        var probed = await NewProbe().ProbeAsync(new VideoFile
        {
            FullPath = missing,
            FileName = "does-not-exist.mkv",
            Extension = ".mkv",
            SizeBytes = 0,
            LastWriteTimeUtc = DateTime.UtcNow
        });

        probed.ProbeError.Should().NotBeNullOrWhiteSpace();
        probed.HasAudioTrack.Should().BeFalse();
    }

    [RequiresFfmpegFact]
    public async Task A_korean_path_with_spaces_probes_correctly()
    {
        var probed = await NewProbe().ProbeAsync(Describe(media.KoreanPathVideo));

        probed.ProbeError.Should().BeNull();
        probed.HasAudioTrack.Should().BeTrue();
        probed.DurationSeconds.Should().BeApproximately(MediaFixture.NominalDurationSeconds, 0.75d);
    }

    [RequiresFfmpegFact]
    public async Task Probing_does_not_mutate_the_source_record_identity()
    {
        var original = Describe(media.SampleVideo);

        var probed = await NewProbe().ProbeAsync(original);

        probed.FullPath.Should().Be(original.FullPath);
        probed.FileName.Should().Be(original.FileName);
        probed.SizeBytes.Should().Be(original.SizeBytes);
        original.Probed.Should().BeFalse("the input record must not be mutated");
    }
}

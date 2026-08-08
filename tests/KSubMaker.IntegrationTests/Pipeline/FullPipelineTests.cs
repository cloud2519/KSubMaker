using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Media;
using KSubMaker.IntegrationTests.Fixtures;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Pipeline;

/// <summary>
/// Scan → enqueue → probe → run, with the real ffmpeg audio extractor, the real atomic SRT writer and
/// the real file checkpoint store. Only the two AI stages are the deterministic fakes.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FullPipelineTests(MediaFixture media) : IDisposable
{
    private readonly TempWorkspace _workspace = new("ksubmaker-pipeline");

    public void Dispose() => _workspace.Dispose();

    private string StageVideos(params (string Source, string Name)[] files)
    {
        var folder = _workspace.CreateSubdirectory("영상");

        foreach (var (source, name) in files)
        {
            media.CopyTo(source, Path.Combine(folder, name));
        }

        return folder;
    }

    [RequiresFfmpegFact]
    public async Task The_whole_pipeline_turns_a_scanned_folder_into_korean_srt_files()
    {
        var folder = StageVideos(
            (media.SampleVideo, "first.mp4"),
            (media.SampleVideo, "second.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();

        // ---- scan -----------------------------------------------------------
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        scan.Files.Should().HaveCount(2);

        // ---- probe with the real ffprobe -------------------------------------
        var probed = new List<VideoFile>();
        foreach (var file in scan.Files)
        {
            probed.Add(await harness.MediaProbe.ProbeAsync(file));
        }

        probed.Should().OnlyContain(f => f.Probed && f.ProbeError == null && f.HasAudioTrack);
        probed.Should().OnlyContain(f => f.DurationSeconds > 1d);

        // ---- enqueue ---------------------------------------------------------
        var enqueued = await harness.Queue.EnqueueAsync(probed, settings);
        enqueued.Should().OnlyContain(r => r.Decision == EnqueueDecision.Created);

        foreach (var file in probed)
        {
            await harness.Queue.ApplyProbeAsync(file);
        }

        // ---- run -------------------------------------------------------------
        await harness.RunQueueToCompletionAsync(settings);

        harness.Queue.Jobs.Should().HaveCount(2);
        harness.Queue.Jobs.Should().OnlyContain(j => j.Status == JobStatus.Completed);
        harness.Queue.Jobs.Should().OnlyContain(j => j.ErrorCode == null);
        harness.Queue.Jobs.Should().OnlyContain(j => j.CompletedAtUtc != null);
        harness.Queue.Jobs.Should().OnlyContain(j => j.DetectedLanguage == "en");

        // ---- verify the output -----------------------------------------------
        foreach (var name in new[] { "first", "second" })
        {
            var srt = Path.Combine(folder, name + ".ko.srt");
            SrtAssertions.AssertIsWellFormedKoreanSrt(srt);

            var cues = SrtAssertions.Parse(File.ReadAllText(srt));
            cues.Should().NotBeEmpty();
            cues.SelectMany(c => c.Lines).Should().Contain(l => l.Contains("[테스트]", StringComparison.Ordinal),
                "fake output must be unmistakable");
        }
    }

    [Fact]
    public async Task A_completed_job_ends_on_the_Done_stage_at_one_hundred_percent()
    {
        var folder = StageVideos((media.SampleVideo, "clip.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        await harness.Queue.EnqueueAsync([await harness.MediaProbe.ProbeAsync(scan.Files.Single())], settings);
        await harness.RunQueueToCompletionAsync(settings);

        var job = harness.Queue.Jobs.Single();

        job.Status.Should().Be(JobStatus.Completed);
        job.CurrentStage.Should().Be(JobStage.Done);
        job.OverallProgress.Should().Be(100d);
    }

    [RequiresFfmpegFact]
    public async Task A_korean_path_with_spaces_is_processed_end_to_end()
    {
        var folder = _workspace.CreateSubdirectory("한국어 자료 (2026)");
        media.CopyTo(media.SampleVideo, Path.Combine(folder, "테스트 영상 - 최종본.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });

        var probed = await harness.MediaProbe.ProbeAsync(scan.Files.Single());
        await harness.Queue.EnqueueAsync([probed], settings);

        await harness.RunQueueToCompletionAsync(settings);

        harness.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        SrtAssertions.AssertIsWellFormedKoreanSrt(Path.Combine(folder, "테스트 영상 - 최종본.ko.srt"));
    }

    [RequiresFfmpegFact]
    public async Task The_output_suffix_setting_changes_the_file_name()
    {
        var folder = StageVideos((media.SampleVideo, "clip.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings(s => s.OutputSuffix = "kor");
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });

        await harness.Queue.EnqueueAsync([await harness.MediaProbe.ProbeAsync(scan.Files.Single())], settings);
        await harness.RunQueueToCompletionAsync(settings);

        File.Exists(Path.Combine(folder, "clip.kor.srt")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "clip.ko.srt")).Should().BeFalse();
    }

    [RequiresFfmpegFact]
    public async Task The_checkpoint_directory_holds_the_transcript_and_partial_translation()
    {
        var folder = StageVideos((media.SampleVideo, "clip.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        await harness.Queue.EnqueueAsync([await harness.MediaProbe.ProbeAsync(scan.Files.Single())], settings);
        await harness.RunQueueToCompletionAsync(settings);

        var job = harness.Queue.Jobs.Single();

        var checkpoint = await harness.CheckpointStore.LoadAsync(job.Id);
        checkpoint.Should().NotBeNull();
        checkpoint!.CompletedStage.Should().Be(JobStage.Done);

        var transcription = await harness.CheckpointStore.LoadTranscriptionAsync(job.Id);
        transcription.Should().NotBeNull();
        transcription!.Segments.Should().NotBeEmpty();

        var partial = await harness.CheckpointStore.LoadPartialTranslationAsync(job.Id);
        partial.Should().HaveCount(transcription.Segments.Count);

        Directory.GetFiles(harness.Paths.JobCacheDirectory(job.Id), "*.tmp")
            .Should().BeEmpty("atomic writes must not leave temp files behind");
    }

    [RequiresFfmpegFact]
    public async Task Cue_timings_stay_inside_the_media_duration()
    {
        var folder = StageVideos((media.SampleVideo, "clip.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        var probed = await harness.MediaProbe.ProbeAsync(scan.Files.Single());

        await harness.Queue.EnqueueAsync([probed], settings);
        await harness.Queue.ApplyProbeAsync(probed);
        await harness.RunQueueToCompletionAsync(settings);

        var cues = SrtAssertions.Parse(File.ReadAllText(Path.Combine(folder, "clip.ko.srt")));

        cues[0].Start.Should().BeGreaterThanOrEqualTo(0d);
        cues[^1].End.Should().BeLessThanOrEqualTo(probed.DurationSeconds + 2d,
            "timings come from the audio, never from the language model");
    }

    [RequiresFfmpegFact]
    public async Task A_video_with_no_audio_track_fails_without_writing_a_subtitle()
    {
        var folder = _workspace.CreateSubdirectory("무음");
        media.CopyTo(media.NoAudioVideo, Path.Combine(folder, "silent.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        var probed = await harness.MediaProbe.ProbeAsync(scan.Files.Single());

        probed.HasAudioTrack.Should().BeFalse();

        await harness.Queue.EnqueueAsync([probed], settings);
        await harness.RunQueueToCompletionAsync(settings);

        var job = harness.Queue.Jobs.Single();

        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorCode.Should().NotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(folder, "silent.ko.srt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_video_with_no_audio_track_reports_AUDIO_TRACK_NOT_FOUND()
    {
        var folder = _workspace.CreateSubdirectory("무음-코드");
        media.CopyTo(media.NoAudioVideo, Path.Combine(folder, "silent.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });

        await harness.Queue.EnqueueAsync([await harness.MediaProbe.ProbeAsync(scan.Files.Single())], settings);
        await harness.RunQueueToCompletionAsync(settings);

        harness.Queue.Jobs.Single().ErrorCode
            .Should().Be(KSubMaker.Domain.Errors.ErrorCodes.AudioTrackNotFound);
    }
}

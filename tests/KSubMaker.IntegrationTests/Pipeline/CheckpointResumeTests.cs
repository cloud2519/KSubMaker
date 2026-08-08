using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Application.Testing;
using KSubMaker.Domain.Jobs;
using KSubMaker.IntegrationTests.Fixtures;
using KSubMaker.IntegrationTests.Infrastructure;
using Xunit;

namespace KSubMaker.IntegrationTests.Pipeline;

/// <summary>
/// Simulates a crash in the middle of the most expensive stage and proves the restart resumes from
/// the checkpoint instead of redoing the work.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class CheckpointResumeTests(MediaFixture media) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly TempWorkspace _workspace = new("ksubmaker-resume");

    public void Dispose() => _workspace.Dispose();

    [RequiresFfmpegFact]
    public async Task A_job_cancelled_mid_transcription_keeps_its_checkpoint_and_finishes_on_restart()
    {
        var folder = _workspace.CreateSubdirectory("영상");
        media.CopyTo(media.SampleVideo, Path.Combine(folder, "clip.mp4"));

        var settings = PipelineHarness.DeterministicSettings();

        var gate = new GatedTranscriber(new FakeTranscriber(
            new KSubMaker.Infrastructure.IO.PhysicalFileSystem(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<KSubMaker.Infrastructure.IO.PhysicalFileSystem>.Instance)));

        CountingAudioExtractorDecorator? extractorHolder = null;

        await using var counted = new PipelineHarness(
            _workspace,
            transcriber: gate,
            wrapAudioExtractor: real => extractorHolder = new CountingAudioExtractorDecorator(real));

        var extractor = extractorHolder!;
        await counted.InitializeDatabaseAsync();

        var scan = await counted.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        await counted.Queue.EnqueueAsync([await counted.MediaProbe.ProbeAsync(scan.Files.Single())], settings);

        // ---- run 1: cancel while the transcriber is blocked ------------------
        await counted.Queue.StartAsync(settings);
        await gate.Started.WaitAsync(Timeout);

        await counted.Queue.StopAsync();

        var job = counted.Queue.Jobs.Single();

        job.Status.Should().BeOneOf(JobStatus.Cancelled, JobStatus.Paused);
        extractor.Calls.Should().Be(1, "audio extraction ran before the transcriber blocked");

        var checkpoint = await counted.CheckpointStore.LoadAsync(job.Id);
        checkpoint.Should().NotBeNull("the interrupted run must leave a resumable checkpoint");
        checkpoint!.CompletedStage.Should().Be(JobStage.ExtractingAudio);

        File.Exists(Path.Combine(counted.Paths.JobCacheDirectory(job.Id), "audio.wav"))
            .Should().BeTrue("the extracted audio is what makes the resume cheap");

        // ---- run 2: restart --------------------------------------------------
        gate.BlockFirstCall = false;

        if (job.Status == JobStatus.Cancelled)
        {
            await counted.Queue.RetryAsync([job.Id]);
        }

        await counted.WaitForQueueToSettleAsync(() => counted.Queue.StartAsync(settings), Timeout);

        counted.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        extractor.Calls.Should().Be(1, "the already-extracted audio must be reused after the restart");

        SrtAssertions.AssertIsWellFormedKoreanSrt(Path.Combine(folder, "clip.ko.srt"));
    }

    [RequiresFfmpegFact]
    public async Task A_finished_job_that_is_reprocessed_does_not_re_extract_the_audio()
    {
        var folder = _workspace.CreateSubdirectory("영상");
        media.CopyTo(media.SampleVideo, Path.Combine(folder, "clip.mp4"));

        CountingAudioExtractorDecorator? extractorHolder = null;

        await using var counted = new PipelineHarness(
            _workspace,
            wrapAudioExtractor: real => extractorHolder = new CountingAudioExtractorDecorator(real));

        var extractor = extractorHolder!;
        await counted.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await counted.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        await counted.Queue.EnqueueAsync([await counted.MediaProbe.ProbeAsync(scan.Files.Single())], settings);

        await counted.RunQueueToCompletionAsync(settings, Timeout);
        counted.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        extractor.Calls.Should().Be(1);

        await counted.Queue.RetryAsync([counted.Queue.Jobs.Single().Id]);
        await counted.RunQueueToCompletionAsync(settings, Timeout);

        counted.Queue.Jobs.Single().Status.Should().Be(JobStatus.Completed);
        extractor.Calls.Should().Be(1, "the checkpoint from the first run is still valid");
    }

    [RequiresFfmpegFact]
    public async Task Removing_a_job_deletes_its_checkpoint_directory()
    {
        var folder = _workspace.CreateSubdirectory("영상");
        media.CopyTo(media.SampleVideo, Path.Combine(folder, "clip.mp4"));

        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var settings = PipelineHarness.DeterministicSettings();
        var scan = await harness.ScanService.ScanAsync(new ScanRequest { RootFolder = folder });
        await harness.Queue.EnqueueAsync([await harness.MediaProbe.ProbeAsync(scan.Files.Single())], settings);
        await harness.RunQueueToCompletionAsync(settings, Timeout);

        var job = harness.Queue.Jobs.Single();
        var cacheDirectory = harness.Paths.JobCacheDirectory(job.Id);

        Directory.Exists(cacheDirectory).Should().BeTrue();

        await harness.Queue.RemoveAsync([job.Id]);

        Directory.Exists(cacheDirectory).Should().BeFalse();
        harness.Queue.Jobs.Should().BeEmpty();
    }

    [RequiresFfmpegFact]
    public async Task Orphaned_cache_directories_are_reclaimed()
    {
        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var orphan = harness.Paths.JobCacheDirectory("job-that-no-longer-exists");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "transcription.json"), new string('x', 4096));

        var live = harness.Paths.JobCacheDirectory("live-job");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "job.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(live, "job.json.tmp"), new string('y', 256));

        var reclaimed = await harness.CheckpointStore.CleanupOrphansAsync(["live-job"]);

        reclaimed.Should().BeGreaterThanOrEqualTo(4096 + 256);
        Directory.Exists(orphan).Should().BeFalse();
        Directory.Exists(live).Should().BeTrue();
        File.Exists(Path.Combine(live, "job.json")).Should().BeTrue();
        File.Exists(Path.Combine(live, "job.json.tmp")).Should().BeFalse();
    }

    /// <summary>
    /// The startup sweep the specification calls "처리 중 앱 강제 종료 후 임시 파일 복구": the queue
    /// is the only component that knows which ids are still live, so it is the one that drives it.
    /// </summary>
    [Fact]
    public async Task The_queue_sweeps_orphaned_cache_after_loading_its_jobs()
    {
        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        var survivor = new Job
        {
            Id = "still-queued",
            VideoPath = Path.Combine(_workspace.Root, "clip.mp4"),
            FileName = "clip.mp4"
        };

        await harness.JobRepository.AddAsync(survivor);
        await harness.Queue.LoadAsync();

        var live = harness.Paths.JobCacheDirectory(survivor.Id);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "transcription.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(live, "transcription.json.tmp"), new string('y', 512));

        var orphan = harness.Paths.JobCacheDirectory("removed-long-ago");
        Directory.CreateDirectory(orphan);
        await File.WriteAllBytesAsync(Path.Combine(orphan, "audio.wav"), new byte[8192]);

        var reclaimed = await harness.Queue.CleanupOrphanedCacheAsync();

        reclaimed.Should().BeGreaterThanOrEqualTo(8192 + 512);
        Directory.Exists(orphan).Should().BeFalse("no job owns it any more");
        Directory.Exists(live).Should().BeTrue("its job is still queued and resumable");
        File.Exists(Path.Combine(live, "transcription.json")).Should().BeTrue();
        File.Exists(Path.Combine(live, "transcription.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public async Task The_sweep_keeps_every_checkpoint_when_the_queue_is_full_of_jobs()
    {
        await using var harness = new PipelineHarness(_workspace);
        await harness.InitializeDatabaseAsync();

        foreach (var id in new[] { "job-a", "job-b" })
        {
            await harness.JobRepository.AddAsync(new Job
            {
                Id = id,
                VideoPath = Path.Combine(_workspace.Root, id + ".mp4"),
                FileName = id + ".mp4"
            });

            Directory.CreateDirectory(harness.Paths.JobCacheDirectory(id));
        }

        await harness.Queue.LoadAsync();

        (await harness.Queue.CleanupOrphanedCacheAsync()).Should().Be(0L);

        Directory.Exists(harness.Paths.JobCacheDirectory("job-a")).Should().BeTrue();
        Directory.Exists(harness.Paths.JobCacheDirectory("job-b")).Should().BeTrue();
    }
}

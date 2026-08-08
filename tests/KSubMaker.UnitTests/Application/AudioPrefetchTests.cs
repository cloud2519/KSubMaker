using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// The audio prefetch lane: extracting file N+1's audio while file N is on the GPU.
///
/// <para>Two things are being guarded. The first is that the lane exists at all and stays bounded —
/// throughput converges once the extractor merely keeps ahead of the consumer, so depth buys
/// nothing past that point and an unbounded run over a folder of two-hour files is tens of
/// gigabytes of wav waiting to be read once.</para>
///
/// <para>The second is that it never touches the job the pump is running. Both would drive ffmpeg
/// at the same <c>audio.wav.tmp</c>, and the loser of that race leaves a torn file that Whisper
/// would happily transcribe into nothing.</para>
/// </summary>
public sealed class AudioPrefetchTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    /// <summary>QueueOrder is set explicitly: the pump orders by it, so the test's idea of
    /// "the first job" has to be the pump's too.</summary>
    private static Job NewJob(string id, int order) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        QueueOrder = order,
        Status = JobStatus.Pending
    };

    private static Job[] JobsNamed(int count) =>
        Enumerable.Range(1, count).Select(i => NewJob($"job-{i}", i)).ToArray();

    private static AppSettings Settings(int depth) => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AudioPrefetchDepth = depth,
        AutoRetryOnRecoverableError = false
    };

    private static JobQueueService NewQueue(InMemoryJobRepository repository, IJobProcessor processor) =>
        new(
            repository,
            new SingleProcessorSelector(processor),
            new RecordingCheckpointStore(),
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    private static async Task RunToIdleAsync(JobQueueService queue, AppSettings settings)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnState(object? sender, QueueStateChangedEventArgs args)
        {
            if (args.State is QueueState.Idle or QueueState.Paused)
            {
                idle.TrySetResult();
            }
        }

        queue.StateChanged += OnState;
        try
        {
            await queue.StartAsync(settings);
            await idle.Task.WaitAsync(RunTimeout);
        }
        finally
        {
            queue.StateChanged -= OnState;
        }
    }

    private static async Task<(ScriptedJobProcessor Processor, IReadOnlyList<Job> Jobs)> RunAsync(
        int depth,
        int jobCount)
    {
        var repository = new InMemoryJobRepository(JobsNamed(jobCount));

        var outcomes = Enumerable.Range(0, jobCount)
            .Select(_ => JobExecutionResult.Ok("/videos/out.ko.srt", 1))
            .ToArray();

        var processor = new ScriptedJobProcessor(outcomes);
        var queue = NewQueue(repository, processor);
        await queue.LoadAsync();
        await RunToIdleAsync(queue, Settings(depth));

        return (processor, await repository.GetAllAsync());
    }

    /// <summary>
    /// Parks the first job and waits for the lane to reach <paramref name="expected"/> prefetches.
    ///
    /// Holding the pump is what makes these assertions deterministic. With a processor that returns
    /// instantly the whole run can finish before the lane is even scheduled, and "did it prefetch?"
    /// becomes a coin toss — the exact shape of a test that passes locally and fails in CI.
    /// </summary>
    private async Task<BlockingJobProcessor> PrefetchWhileParkedAsync(
        int depth,
        int jobCount,
        int expected)
    {
        var repository = new InMemoryJobRepository(JobsNamed(jobCount));
        var processor = new BlockingJobProcessor();
        var queue = NewQueue(repository, processor);

        await queue.LoadAsync();
        var run = queue.StartAsync(Settings(depth));

        await processor.Started.WaitAsync(RunTimeout);

        var deadline = DateTime.UtcNow + RunTimeout;
        while (Count(processor) < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        processor.Release();
        await run;
        await queue.StopAsync();

        _lastJobs = await repository.GetAllAsync();
        return processor;
    }

    /// <summary>Jobs as they stood at the end of the last parked run.</summary>
    private IReadOnlyList<Job>? _lastJobs;

    private static int Count(BlockingJobProcessor processor)
    {
        lock (processor.Prefetched)
        {
            return processor.Prefetched.Count;
        }
    }

    [Fact]
    public async Task The_lane_extracts_the_files_the_pump_has_not_reached_yet()
    {
        var processor = await PrefetchWhileParkedAsync(depth: 2, jobCount: 4, expected: 2);

        processor.Prefetched.Should().NotBeEmpty("the whole point is to demux ahead of the GPU");
    }

    [Fact]
    public async Task The_lane_stops_at_the_configured_depth()
    {
        // job-1 is parked, so nothing before it ever finishes and the lane can never advance past
        // depth. Without the bound it would run away through the whole queue — 147 two-hour files
        // is about 34GB of wav for no throughput gain at all.
        var processor = await PrefetchWhileParkedAsync(depth: 2, jobCount: 6, expected: 2);

        processor.Prefetched.Should().HaveCount(2);
        processor.Prefetched.Should().Equal("job-2", "job-3");
    }

    [Fact]
    public async Task The_first_job_is_never_prefetched()
    {
        var processor = await PrefetchWhileParkedAsync(depth: 3, jobCount: 5, expected: 3);

        // The pump starts job-1 immediately and extracts it inside the job. Prefetching it too
        // would put two ffmpeg processes on one audio.wav.tmp.
        processor.Prefetched.Should().NotContain("job-1");
    }

    [Fact]
    public async Task A_depth_of_zero_turns_the_lane_off_completely()
    {
        var (processor, _) = await RunAsync(depth: 0, jobCount: 4);

        processor.Prefetched.Should().BeEmpty();
    }

    [Fact]
    public async Task Turning_the_lane_off_does_not_stop_the_queue_finishing()
    {
        var (processor, jobs) = await RunAsync(depth: 0, jobCount: 3);

        processor.Calls.Should().Be(3);
        jobs.Should().OnlyContain(j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task Every_prefetched_id_is_a_real_job_and_asked_for_once()
    {
        var (processor, jobs) = await RunAsync(depth: 4, jobCount: 5);

        processor.Prefetched.Should().OnlyHaveUniqueItems("a second extraction of the same file is pure waste");
        processor.Prefetched.Should().BeSubsetOf(jobs.Select(j => j.Id));
    }

    [Fact]
    public async Task The_lane_never_delays_the_run_reaching_idle()
    {
        // Regression: the pump awaited the lane inside its finally, and cancelling an already
        // disposed token source threw there — skipping the state change back to 대기 중, so the
        // queue looked busy forever. Every queue test in the suite timed out at once.
        var (processor, jobs) = await RunAsync(depth: 2, jobCount: 3);

        processor.Calls.Should().Be(3);
        jobs.Should().OnlyContain(j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task A_single_file_queue_prefetches_nothing()
    {
        var (processor, _) = await RunAsync(depth: 4, jobCount: 1);

        // Nothing follows the only job, so there is nothing to run ahead of.
        processor.Prefetched.Should().BeEmpty();
    }

    [Fact]
    public async Task A_prefetched_row_shows_the_extraction_stage_without_claiming_to_be_running()
    {
        var processor = await PrefetchWhileParkedAsync(depth: 2, jobCount: 4, expected: 2);
        _ = processor;

        // Asserted through the repository rather than the fake: the point is what the grid binds to.
        // Status must stay 대기 — a row that claims to be running would make 취소 and 재시도 disagree
        // with what the user sees — while the stage and the bar reflect the work genuinely done.
        _lastJobs.Should().NotBeNull();

        var prepared = _lastJobs!.Where(j => j.CurrentStage == JobStage.ExtractingAudio).ToArray();
        prepared.Should().NotBeEmpty("a prefetched file has finished its extraction stage");
        prepared.Should().OnlyContain(j => j.Status == JobStatus.Pending);
        prepared.Should().OnlyContain(j => j.OverallProgress > 0d);
    }

    [Fact]
    public async Task Finishing_a_job_reclaims_its_extracted_audio()
    {
        // A finished job's wav is the only large thing it leaves behind — about 115MB per hour of
        // video — and nothing reads it again. transcription.json stays, so a 재시도 that only
        // changes the translation engine still skips ASR.
        var repository = new InMemoryJobRepository(JobsNamed(2));
        var checkpoints = new RecordingCheckpointStore();

        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Ok("/videos/out.ko.srt", 1),
            JobExecutionResult.Ok("/videos/out.ko.srt", 1));

        var queue = new JobQueueService(
            repository,
            new SingleProcessorSelector(processor),
            checkpoints,
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

        await queue.LoadAsync();
        await RunToIdleAsync(queue, Settings(depth: 0));

        var deadline = DateTime.UtcNow + RunTimeout;
        while (Count(checkpoints) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        lock (checkpoints.AudioDeleted)
        {
            checkpoints.AudioDeleted.Should().BeEquivalentTo(["job-1", "job-2"]);
        }

        // Only the audio: the rest of the checkpoint is what makes a retry cheap.
        checkpoints.Cleared.Should().BeEmpty();
    }

    private static int Count(RecordingCheckpointStore store)
    {
        lock (store.AudioDeleted)
        {
            return store.AudioDeleted.Count;
        }
    }

    [Fact]
    public async Task The_lane_does_not_prefetch_jobs_that_are_already_finished()
    {
        var done = NewJob("job-2", 2);
        done.Status = JobStatus.Completed;

        var repository = new InMemoryJobRepository(NewJob("job-1", 1), done, NewJob("job-3", 3));

        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Ok("/videos/out.ko.srt", 1),
            JobExecutionResult.Ok("/videos/out.ko.srt", 1));

        var queue = NewQueue(repository, processor);
        await queue.LoadAsync();
        await RunToIdleAsync(queue, Settings(depth: 3));

        processor.Prefetched.Should().NotContain("job-2", "its audio would never be read");
    }
}

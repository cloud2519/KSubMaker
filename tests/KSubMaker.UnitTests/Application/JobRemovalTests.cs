using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// 선택 항목 제거: what leaves the queue, what happens to the cache, and what the queue refuses to
/// tear out from under the pump.
/// </summary>
public sealed class JobRemovalTests
{
    /// <summary>Real time, but tiny: enough for a cooperative processor, far too short for a wedged one.</summary>
    private static readonly TimeSpan ShortStopBudget = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job NewJob(string id, JobStatus status = JobStatus.Pending) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = status
    };

    private static AppSettings SequentialSettings() => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AutoRetryOnRecoverableError = false
    };

    private static JobQueueService NewQueue(
        InMemoryJobRepository repository,
        RecordingCheckpointStore store,
        IJobProcessorSelector? selector = null,
        ILogger<JobQueueService>? logger = null) =>
        new(
            repository,
            selector ?? new NeverRunsProcessorSelector(),
            store,
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            logger ?? NullLogger<JobQueueService>.Instance);

    // -----------------------------------------------------------------------
    // the quiet path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Removing_drops_the_job_from_the_queue_and_the_database()
    {
        var repository = new InMemoryJobRepository(NewJob("a"), NewJob("b"));
        var queue = NewQueue(repository, new RecordingCheckpointStore());
        await queue.LoadAsync();

        var result = await queue.RemoveAsync(["a"]);

        result.Removed.Should().Equal("a");
        result.Skipped.Should().BeEmpty();
        queue.Jobs.Select(j => j.Id).Should().Equal("b");
        (await repository.FindAsync("a")).Should().BeNull();
    }

    [Fact]
    public async Task Removing_deletes_the_cache_of_every_job_it_removed()
    {
        var store = new RecordingCheckpointStore();
        var queue = NewQueue(new InMemoryJobRepository(NewJob("a"), NewJob("b"), NewJob("c")), store);
        await queue.LoadAsync();

        await queue.RemoveAsync(["a", "c"]);

        // 체크포인트 and 추출된 오디오 both live under the per-job cache directory the store clears.
        store.Cleared.Should().BeEquivalentTo(["a", "c"]);
    }

    [Fact]
    public async Task An_unknown_id_is_ignored_rather_than_throwing()
    {
        var store = new RecordingCheckpointStore();
        var queue = NewQueue(new InMemoryJobRepository(NewJob("a")), store);
        await queue.LoadAsync();

        var result = await queue.RemoveAsync(["a", "no-such-job"]);

        result.Removed.Should().Equal("a");

        // Clearing the cache of a job that never existed would be a lie about what was deleted.
        store.Cleared.Should().Equal("a");
    }

    [Fact]
    public async Task A_duplicated_id_is_removed_once()
    {
        var store = new RecordingCheckpointStore();
        var queue = NewQueue(new InMemoryJobRepository(NewJob("a")), store);
        await queue.LoadAsync();

        var result = await queue.RemoveAsync(["a", "a"]);

        result.Removed.Should().Equal("a");
        store.Cleared.Should().Equal("a");
    }

    [Fact]
    public async Task Removing_nothing_reports_nothing()
    {
        var queue = NewQueue(new InMemoryJobRepository(NewJob("a")), new RecordingCheckpointStore());
        await queue.LoadAsync();

        var result = await queue.RemoveAsync([]);

        result.RemovedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);
        queue.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_completed_reports_which_ids_it_took()
    {
        var queue = NewQueue(
            new InMemoryJobRepository(
                NewJob("done", JobStatus.Completed),
                NewJob("failed", JobStatus.Failed),
                NewJob("waiting")),
            new RecordingCheckpointStore());

        await queue.LoadAsync();

        var result = await queue.RemoveCompletedAsync();

        result.Removed.Should().Equal("done");
        queue.Jobs.Select(j => j.Id).Should().BeEquivalentTo(["failed", "waiting"]);
    }

    // -----------------------------------------------------------------------
    // a failing cache delete
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_cache_delete_that_throws_does_not_abort_the_batch()
    {
        var logger = new CapturingLogger<JobQueueService>();
        var store = new RecordingCheckpointStore();
        store.FailClearFor.Add("b");

        var repository = new InMemoryJobRepository(NewJob("a"), NewJob("b"), NewJob("c"));
        var queue = NewQueue(repository, store, logger: logger);
        await queue.LoadAsync();

        var result = await queue.RemoveAsync(["a", "b", "c"]);

        // The locked one still leaves the queue: its row must not survive in a half-removed state,
        // and the startup orphan sweep reclaims the directory later.
        result.Removed.Should().BeEquivalentTo(["a", "b", "c"]);
        queue.Jobs.Should().BeEmpty();
        store.Cleared.Should().BeEquivalentTo(["a", "b", "c"], "the failure must not stop the walk");

        (await repository.GetAllAsync()).Should().BeEmpty();
        logger.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Exception is IOException);
    }

    // -----------------------------------------------------------------------
    // a running job
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_running_job_is_cancelled_and_waited_for_before_its_row_goes()
    {
        var processor = new BlockingJobProcessor();
        var store = new RecordingCheckpointStore();
        var repository = new InMemoryJobRepository(NewJob("a"));

        var queue = NewQueue(repository, store, new SingleProcessorSelector(processor));
        await queue.LoadAsync();

        await queue.StartAsync(SequentialSettings());
        await processor.Started.WaitAsync(RunTimeout);

        // Never released: the only thing that can end this job is the cancellation the remover sends.
        var result = await queue.RemoveAsync(["a"], TimeSpan.FromSeconds(5));

        result.Removed.Should().Equal("a");
        result.Skipped.Should().BeEmpty();
        queue.Jobs.Should().BeEmpty();
        store.Cleared.Should().Equal("a");

        // The pump had already let go, so nothing wrote the job back afterwards.
        (await repository.FindAsync("a")).Should().BeNull();

        await queue.DisposeAsync();
    }

    [Fact]
    public async Task A_job_that_will_not_stop_is_skipped_and_the_rest_are_removed()
    {
        var processor = new BlockingJobProcessor { HonoursCancellation = false };
        var store = new RecordingCheckpointStore();
        var repository = new InMemoryJobRepository(NewJob("stuck"), NewJob("idle"));

        var queue = NewQueue(repository, store, new SingleProcessorSelector(processor));
        await queue.LoadAsync();

        try
        {
            await queue.StartAsync(SequentialSettings());
            await processor.Started.WaitAsync(RunTimeout);

            var result = await queue.RemoveAsync(["stuck", "idle"], ShortStopBudget);

            result.Removed.Should().Equal("idle");

            // A row must never be ripped out from under the pump, and the cache of a job that is
            // still running has to stay where it is.
            result.Skipped.Should().Equal("stuck");
            queue.Jobs.Select(j => j.Id).Should().Equal("stuck");
            store.Cleared.Should().Equal("idle");
        }
        finally
        {
            // Let the wedged processor finish so disposal does not hang on the pump.
            processor.Release();
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task Removing_only_a_wedged_job_removes_nothing_and_says_so()
    {
        var processor = new BlockingJobProcessor { HonoursCancellation = false };
        var repository = new InMemoryJobRepository(NewJob("stuck"));
        var queue = NewQueue(repository, new RecordingCheckpointStore(), new SingleProcessorSelector(processor));
        await queue.LoadAsync();

        try
        {
            await queue.StartAsync(SequentialSettings());
            await processor.Started.WaitAsync(RunTimeout);

            var result = await queue.RemoveAsync(["stuck"], ShortStopBudget);

            result.Removed.Should().BeEmpty();
            result.Skipped.Should().Equal("stuck");
            (await repository.FindAsync("stuck")).Should().NotBeNull("nothing was removed");
        }
        finally
        {
            processor.Release();
            await queue.DisposeAsync();
        }
    }

    /// <summary>
    /// 취소 used to flip the status and leave the worker running, so the pump would then write its own
    /// terminal state over the cancellation. Per-job cancellation is what lets 제거 wait at all.
    /// </summary>
    [Fact]
    public async Task Cancelling_one_job_stops_that_job_without_stopping_the_queue()
    {
        var processor = new BlockingJobProcessor();
        var repository = new InMemoryJobRepository(NewJob("a"));
        var queue = NewQueue(repository, new RecordingCheckpointStore(), new SingleProcessorSelector(processor));
        await queue.LoadAsync();

        await queue.StartAsync(SequentialSettings());
        await processor.Started.WaitAsync(RunTimeout);

        await queue.CancelAsync(["a"]);

        // The pump has to actually leave the job; the removal wait is what proves it did.
        var result = await queue.RemoveAsync(["a"], TimeSpan.FromSeconds(5));

        result.Removed.Should().Equal("a");

        await queue.DisposeAsync();
    }

    /// <summary>
    /// The row is gone from the grid the moment the queue stops owning the job, so a late save or a
    /// late JobChanged would resurrect it in the database or on screen.
    /// </summary>
    [Fact]
    public async Task A_removed_job_is_never_announced_again()
    {
        var repository = new InMemoryJobRepository(NewJob("a"));
        var queue = NewQueue(repository, new RecordingCheckpointStore());
        await queue.LoadAsync();

        var announced = new List<string>();
        queue.JobChanged += (_, e) => announced.Add(e.Job.Id);

        await queue.RemoveAsync(["a"]);
        await queue.CancelAsync(["a"]);
        await queue.RetryAsync(["a"]);

        announced.Should().BeEmpty();
    }
}

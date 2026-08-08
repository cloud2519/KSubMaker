using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// The startup sweep. Its whole contract is "reclaim what a crash left behind, and never get in the
/// way": the known-id set must be the loaded queue, and no failure may reach the caller.
/// </summary>
public sealed class OrphanedCacheCleanupTests
{
    private static (JobQueueService Queue, RecordingCheckpointStore Store) NewQueue(
        ILogger<JobQueueService>? logger = null,
        params Job[] jobs)
    {
        var store = new RecordingCheckpointStore();

        var queue = new JobQueueService(
            new InMemoryJobRepository(jobs),
            new NeverRunsProcessorSelector(),
            store,
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            logger ?? NullLogger<JobQueueService>.Instance);

        return (queue, store);
    }

    private static Job NewJob(string id) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv"
    };

    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_keeps_every_job_the_queue_loaded()
    {
        var (queue, store) = NewQueue(null, NewJob("a"), NewJob("b"));
        await queue.LoadAsync();

        await queue.CleanupOrphanedCacheAsync();

        store.CleanupCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task The_number_of_bytes_reclaimed_is_returned_and_logged()
    {
        var logger = new CapturingLogger<JobQueueService>();
        var (queue, store) = NewQueue(logger, NewJob("a"));
        store.Reclaimed = 3L * 1024 * 1024;

        await queue.LoadAsync();

        (await queue.CleanupOrphanedCacheAsync()).Should().Be(3L * 1024 * 1024);
        logger.ContainsMessage("남아 있던 캐시를 정리했습니다").Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_sweep_is_logged_and_swallowed_so_startup_continues()
    {
        var logger = new CapturingLogger<JobQueueService>();
        var (queue, store) = NewQueue(logger, NewJob("a"));
        store.ThrowOnCleanup = new UnauthorizedAccessException("locked by antivirus");

        await queue.LoadAsync();

        var reclaimed = await queue.CleanupOrphanedCacheAsync();

        reclaimed.Should().Be(0L);
        logger.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning && r.Exception is UnauthorizedAccessException);
    }

    [Fact]
    public async Task A_cancelled_sweep_does_not_surface_as_a_failure()
    {
        var (queue, store) = NewQueue(null, NewJob("a"));
        store.ThrowOnCleanup = new OperationCanceledException();

        await queue.LoadAsync();

        var act = async () => await queue.CleanupOrphanedCacheAsync();

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Sweeping before <c>LoadAsync</c> would hand an empty known-id set to the store and delete the
    /// checkpoints of every resumable job. The production caller runs after the load; this test
    /// documents why the ordering is load-bearing.
    /// </summary>
    [Fact]
    public async Task Sweeping_before_the_load_would_report_no_known_jobs()
    {
        var (queue, store) = NewQueue(null, NewJob("a"), NewJob("b"));

        await queue.CleanupOrphanedCacheAsync();
        store.CleanupCalls.Should().ContainSingle().Which.Should().BeEmpty();

        await queue.LoadAsync();
        await queue.CleanupOrphanedCacheAsync();
        store.CleanupCalls[1].Should().BeEquivalentTo(["a", "b"]);
    }
}

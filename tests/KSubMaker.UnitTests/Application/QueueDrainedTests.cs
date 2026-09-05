using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// <see cref="JobQueueService.QueueDrained"/> — the signal a post-run 절전/종료 hangs off. The point
/// of the event is that it fires <b>only</b> when the queue emptied on its own, so a 중단 or a
/// 일시정지 can never lead to the machine sleeping or powering off.
/// </summary>
public sealed class QueueDrainedTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private static Job NewJob(string id) => new()
    {
        Id = id,
        VideoPath = $"/videos/{id}.mkv",
        FileName = $"{id}.mkv",
        Status = JobStatus.Pending
    };

    private static AppSettings Settings() => new()
    {
        ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
        AutoRetryOnRecoverableError = false
    };

    private static JobQueueService NewQueue(InMemoryJobRepository repository, IJobProcessor processor) =>
        new(
            repository,
            new SingleProcessorSelector(processor),
            new RecordingCheckpointStore(),
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

    /// <summary>
    /// Waits for the queue to reach 대기/일시정지. Because <see cref="JobQueueService.QueueDrained"/>
    /// is decided before that state is raised, this is also a safe point to assert that the event
    /// did <b>not</b> fire.
    /// </summary>
    private static async Task WaitForIdleAsync(JobQueueService queue)
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
            if (queue.State is QueueState.Idle or QueueState.Paused)
            {
                return;
            }

            await idle.Task.WaitAsync(RunTimeout);
        }
        finally
        {
            queue.StateChanged -= OnState;
        }
    }

    /// <summary>
    /// Subscribes before the run and returns the outcome the event carried. The event is raised on
    /// the pump thread just after 대기 is announced, so a test that only waited for the state change
    /// would race it.
    /// </summary>
    private static async Task<QueueRunOutcome> RunAndAwaitDrainAsync(JobQueueService queue, AppSettings settings)
    {
        var drained = new TaskCompletionSource<QueueRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.QueueDrained += (_, e) => drained.TrySetResult(e.Outcome);

        await queue.StartAsync(settings);
        return await drained.Task.WaitAsync(RunTimeout);
    }

    [Fact]
    public async Task A_natural_drain_fires_once_and_reports_every_completed_job()
    {
        var repository = new InMemoryJobRepository(NewJob("a"), NewJob("b"));
        await using var queue = NewQueue(repository, new ScriptedJobProcessor());
        await queue.LoadAsync();

        var fires = 0;
        queue.QueueDrained += (_, _) => Interlocked.Increment(ref fires);

        var outcome = await RunAndAwaitDrainAsync(queue, Settings());
        await WaitForIdleAsync(queue);

        fires.Should().Be(1);
        outcome.Completed.Should().Be(2);
        outcome.Failed.Should().Be(0);
        outcome.Cancelled.Should().Be(0);
    }

    [Fact]
    public async Task A_run_with_one_failure_reports_it_but_still_drains()
    {
        var processor = new ScriptedJobProcessor(
            JobExecutionResult.Fail(ErrorCodes.Unknown, "boom", recoverable: false),
            JobExecutionResult.Ok("/videos/b.ko.srt", cueCount: 3));

        var repository = new InMemoryJobRepository(NewJob("a"), NewJob("b"));
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        var outcome = await RunAndAwaitDrainAsync(queue, Settings());

        outcome.Completed.Should().Be(1);
        outcome.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_the_queue_does_not_fire_QueueDrained()
    {
        var processor = new BlockingJobProcessor();
        var repository = new InMemoryJobRepository(NewJob("a"));
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        var drained = 0;
        queue.QueueDrained += (_, _) => Interlocked.Increment(ref drained);

        await queue.StartAsync(Settings());
        await processor.Started.WaitAsync(RunTimeout);
        await queue.StopAsync();
        await WaitForIdleAsync(queue);

        drained.Should().Be(0);
    }

    [Fact]
    public async Task Pausing_the_queue_does_not_fire_QueueDrained()
    {
        var processor = new BlockingJobProcessor();
        var repository = new InMemoryJobRepository(NewJob("a"), NewJob("b"));
        await using var queue = NewQueue(repository, processor);
        await queue.LoadAsync();

        var drained = 0;
        queue.QueueDrained += (_, _) => Interlocked.Increment(ref drained);

        await queue.StartAsync(Settings());
        await processor.Started.WaitAsync(RunTimeout);
        queue.Pause();
        processor.Release();
        await WaitForIdleAsync(queue);

        drained.Should().Be(0);
    }

    [Fact]
    public async Task Starting_with_nothing_runnable_reports_a_run_that_did_nothing()
    {
        // Every job already terminal: the pump has nothing to do and returns straight away. The
        // event still fires (the queue did go idle on its own) but the outcome must show zero work,
        // which is what PostQueueActionPolicy keys on to refuse the action.
        var done = NewJob("done");
        done.Status = JobStatus.Completed;

        var repository = new InMemoryJobRepository(done);
        await using var queue = NewQueue(repository, new ScriptedJobProcessor());
        await queue.LoadAsync();

        var outcome = await RunAndAwaitDrainAsync(queue, Settings());

        outcome.ProcessedNothing.Should().BeTrue();
    }
}

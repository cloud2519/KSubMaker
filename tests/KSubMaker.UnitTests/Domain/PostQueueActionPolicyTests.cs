using FluentAssertions;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The rules that stand between "the queue drained" and "the PC sleeps or powers off". Pure, so
/// every branch is checked here rather than through the WPF shell that cannot run on the test agent.
/// </summary>
public sealed class PostQueueActionPolicyTests
{
    [Fact]
    public void None_configured_is_always_None()
    {
        PostQueueActionPolicy.Resolve(
                PostQueueAction.None,
                onlyWhenAllSucceeded: false,
                new QueueRunOutcome(Completed: 5, Failed: 0, Cancelled: 0))
            .Should().Be(PostQueueAction.None);
    }

    [Fact]
    public void A_run_that_completed_nothing_never_triggers_the_action()
    {
        // 시작 pressed with nothing runnable, or a run where every job failed at probe. Powering off
        // on that would be indefensible.
        PostQueueActionPolicy.Resolve(
                PostQueueAction.Shutdown,
                onlyWhenAllSucceeded: false,
                new QueueRunOutcome(Completed: 0, Failed: 3, Cancelled: 0))
            .Should().Be(PostQueueAction.None);
    }

    [Fact]
    public void A_clean_run_triggers_the_configured_action()
    {
        PostQueueActionPolicy.Resolve(
                PostQueueAction.Sleep,
                onlyWhenAllSucceeded: true,
                new QueueRunOutcome(Completed: 4, Failed: 0, Cancelled: 0))
            .Should().Be(PostQueueAction.Sleep);
    }

    [Theory]
    [InlineData(3, 1, 0)]
    [InlineData(3, 0, 2)]
    [InlineData(0, 0, 1)]
    public void With_the_strict_toggle_any_failure_or_cancellation_calls_it_off(int completed, int failed, int cancelled)
    {
        PostQueueActionPolicy.Resolve(
                PostQueueAction.Hibernate,
                onlyWhenAllSucceeded: true,
                new QueueRunOutcome(completed, failed, cancelled))
            .Should().Be(PostQueueAction.None);
    }

    [Fact]
    public void Without_the_strict_toggle_a_failure_still_lets_the_action_run()
    {
        PostQueueActionPolicy.Resolve(
                PostQueueAction.Shutdown,
                onlyWhenAllSucceeded: false,
                new QueueRunOutcome(Completed: 6, Failed: 2, Cancelled: 1))
            .Should().Be(PostQueueAction.Shutdown);
    }
}

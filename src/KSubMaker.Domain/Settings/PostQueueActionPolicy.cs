namespace KSubMaker.Domain.Settings;

/// <summary>
/// The outcome of one queue run, as counted within that run's scope (a selection, or the whole
/// queue). Only the tallies the post-run decision needs.
/// </summary>
/// <param name="Completed">Jobs that finished successfully.</param>
/// <param name="Failed">Jobs that ended in <c>Failed</c>.</param>
/// <param name="Cancelled">Jobs the user cancelled while the run was going.</param>
public readonly record struct QueueRunOutcome(int Completed, int Failed, int Cancelled)
{
    /// <summary>Nothing at all was processed — an empty run, or one stopped before any job finished.</summary>
    public bool ProcessedNothing => Completed == 0 && Failed == 0 && Cancelled == 0;

    /// <summary>Every job that was processed ended in success.</summary>
    public bool AllSucceeded => Completed > 0 && Failed == 0 && Cancelled == 0;
}

/// <summary>
/// Decides whether the configured <see cref="PostQueueAction"/> actually fires for a given run.
///
/// <para>Pure and side-effect free so the rules are unit tested without a UI. The App project is
/// <c>net10.0-windows</c> and unreachable from the Linux test suite, which is why the decision lives
/// here and only the countdown dialog and the P/Invoke live up there — the same split as
/// <see cref="Models.ModelSelectionValidator"/>.</para>
/// </summary>
public static class PostQueueActionPolicy
{
    /// <summary>
    /// The action to carry out, or <see cref="PostQueueAction.None"/> when this run does not warrant
    /// it.
    /// </summary>
    /// <param name="configured">What the user chose in settings.</param>
    /// <param name="onlyWhenAllSucceeded">
    /// When true, any failed or cancelled job in the run cancels the action — the user is left with
    /// the machine on so they can look at what went wrong.
    /// </param>
    /// <param name="outcome">The run's tallies, within its own scope.</param>
    public static PostQueueAction Resolve(
        PostQueueAction configured,
        bool onlyWhenAllSucceeded,
        QueueRunOutcome outcome)
    {
        if (configured == PostQueueAction.None)
        {
            return PostQueueAction.None;
        }

        // A run that finished nothing is not "done with the work" in any sense the user meant —
        // most often it is a 시작 pressed with nothing runnable. Powering the machine off on that
        // would be indefensible.
        if (outcome.Completed == 0)
        {
            return PostQueueAction.None;
        }

        if (onlyWhenAllSucceeded && (outcome.Failed > 0 || outcome.Cancelled > 0))
        {
            return PostQueueAction.None;
        }

        return configured;
    }
}

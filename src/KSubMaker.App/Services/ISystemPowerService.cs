using KSubMaker.Domain.Settings;

namespace KSubMaker.App.Services;

/// <summary>
/// The machine's power state, as far as KSubMaker touches it: hold sleep off while the queue runs,
/// and carry out the configured <see cref="PostQueueAction"/> once it has drained.
///
/// Every method is best-effort and never throws — a keep-awake that silently fails costs the user a
/// paused sleep timer, not a crash, and a 절전/종료 that cannot start is a status-bar message.
/// </summary>
public interface ISystemPowerService
{
    /// <summary>
    /// Asks Windows not to sleep the system while work is in progress. The display is still allowed
    /// to switch off. Idempotent; call it whenever the queue enters a running state.
    /// </summary>
    void PreventSleep();

    /// <summary>
    /// Releases the hold from <see cref="PreventSleep"/> so the normal idle timers apply again. Call
    /// it whenever the queue goes idle or paused.
    /// </summary>
    void AllowSleep();

    /// <summary>
    /// Carries out <paramref name="action"/> now — suspend, hibernate or shut down. Releases the
    /// keep-awake hold first. <see cref="PostQueueAction.None"/> is a no-op. Returns false when the
    /// action could not be started.
    /// </summary>
    bool Execute(PostQueueAction action);
}

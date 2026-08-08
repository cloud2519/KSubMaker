namespace KSubMaker.Application.Services;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler synchronously on the reporting thread.
///
/// <see cref="System.Progress{T}"/> is the wrong tool inside the queue: it marshals every callback
/// through the captured <see cref="SynchronizationContext"/> (the thread pool, on a background pump),
/// which means reports can be delivered out of order and after the operation has already finished.
/// For a progress bar that is harmless; for state that is also persisted and used to decide what to
/// resume, it is a correctness bug. Callers that need UI-thread affinity marshal it themselves.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}

/// <summary>
/// One-way latch used to stop accepting progress once a job has reached a terminal state.
/// Closing is idempotent and safe from any thread.
/// </summary>
public sealed class ProgressGate
{
    private volatile bool _open = true;

    public bool IsOpen => _open;

    public void Close() => _open = false;
}

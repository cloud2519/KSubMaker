using KSubMaker.WorkerProtocol;

namespace KSubMaker.Application.Abstractions;

/// <summary>
/// Owns the lifetime of the Python worker process and the JSON Lines channel to it.
/// Implementations must guarantee that killing the host also kills the worker and any FFmpeg
/// children it spawned.
/// </summary>
public interface IWorkerClient : IAsyncDisposable
{
    bool IsRunning { get; }

    /// <summary>Raised for every well-formed event the worker emits, on a background thread.</summary>
    event EventHandler<WorkerEvent>? EventReceived;

    /// <summary>Raised when the worker exits unexpectedly.</summary>
    event EventHandler<WorkerExitedEventArgs>? Exited;

    /// <summary>Starts the process and waits for the <c>ready</c> handshake.</summary>
    Task<ReadyEvent> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a command line to the worker's stdin.</summary>
    Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command and awaits the single event that answers it (matched on <c>requestId</c>).
    /// </summary>
    Task<TEvent> RequestAsync<TEvent>(WorkerCommand command, CancellationToken cancellationToken = default)
        where TEvent : WorkerEvent;

    /// <summary>Graceful shutdown, escalating to a kill after <paramref name="timeout"/>.</summary>
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class WorkerExitedEventArgs(int exitCode, bool expected, string? lastStandardError) : EventArgs
{
    public int ExitCode { get; } = exitCode;
    public bool Expected { get; } = expected;
    public string? LastStandardError { get; } = lastStandardError;
}

/// <summary>Resolves the bundled ffmpeg / ffprobe / python worker executables.</summary>
public interface IToolLocator
{
    /// <summary>Absolute path to ffmpeg. Throws <see cref="FileNotFoundException"/> when missing.</summary>
    string FfmpegPath { get; }

    string FfprobePath { get; }

    /// <summary>Command line used to launch the worker: an executable plus its leading arguments.</summary>
    (string Executable, IReadOnlyList<string> Arguments) WorkerCommandLine { get; }

    bool TryValidate(out string? error);
}

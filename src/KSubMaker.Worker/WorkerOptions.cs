namespace KSubMaker.Worker;

/// <summary>
/// Tunables for the worker host. Bound through <c>IOptions&lt;WorkerOptions&gt;</c> so the settings
/// screen (or an appsettings file) can lengthen the timeouts on very slow machines without a rebuild.
/// </summary>
public sealed record WorkerOptions
{
    /// <summary>Configuration section name when bound from configuration.</summary>
    public const string SectionName = "Worker";

    /// <summary>
    /// How long <c>StartAsync</c> waits for the <c>ready</c> handshake. Generous by design: the first
    /// launch of the frozen Python build has to page in torch/ctranslate2, which is slow on HDDs.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Watchdog window. When a job is in flight and nothing at all arrives on stdout for this long the
    /// worker is considered wedged. 15 minutes is longer than any legitimate silent gap: even a
    /// large-v3 CPU transcription reports progress far more often than that.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a graceful <c>shutdown</c> is given before the process tree is killed.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a <c>cancel</c> command is given to produce a <c>cancelled</c> event before the worker
    /// is force-killed. Cancelling mid-inference cannot interrupt a CUDA kernel, so a few seconds of
    /// slack is normal.
    /// </summary>
    public TimeSpan CancellationGraceTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Size of the stderr ring buffer surfaced through <c>WorkerExitedEventArgs</c>.</summary>
    public int StandardErrorBufferLines { get; init; } = 50;

    /// <summary>
    /// Ceiling on the <c>detectHardware</c> round trip. The worker imports ctranslate2 and torch to
    /// answer honestly, which is slow on a cold page cache but nowhere near a minute; past that the
    /// caller is better served by the locally-detected profile than by waiting.
    /// </summary>
    public TimeSpan HardwareProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Kill the worker when the watchdog trips. On by default: a wedged Python process never recovers,
    /// and leaving it alive would make every following job wait out the full idle timeout. Killing it
    /// lets the next job start a fresh worker.
    /// </summary>
    public bool TerminateOnIdleTimeout { get; init; } = true;

    /// <summary>Value of <c>hostVersion</c> in the <c>hello</c> handshake; informational only.</summary>
    public string? HostVersion { get; init; }
}

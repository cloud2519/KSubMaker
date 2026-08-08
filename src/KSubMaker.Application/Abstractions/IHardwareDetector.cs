using KSubMaker.Domain.Hardware;

namespace KSubMaker.Application.Abstractions;

/// <summary>
/// Detects GPU / CPU / RAM / disk. The C# implementation covers everything that can be read without
/// loading a deep-learning stack; whether CUDA is *usable* is answered by the Python worker through
/// <see cref="IWorkerHardwareProbe"/> and merged in by <see cref="Services.HardwareService"/>.
/// </summary>
public interface IHardwareDetector
{
    Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the Python worker the one hardware question the host cannot answer honestly: whether the
/// CUDA runtime that CTranslate2 needs actually opens a device on this machine.
/// </summary>
/// <remarks>
/// Implementations must never start the worker unless explicitly asked to. Spawning CPython (and
/// importing torch) at application launch, before the user has done anything, costs seconds of cold
/// start for information that is only needed once a model is about to be loaded.
/// </remarks>
public interface IWorkerHardwareProbe
{
    /// <summary>True when the worker is already up, i.e. probing costs one round trip and nothing else.</summary>
    bool IsWorkerRunning { get; }

    /// <summary>
    /// Runs <c>detectHardware</c> against the worker. Returns null when the worker is not running
    /// (and <paramref name="startWorkerIfNeeded"/> is false), or when it failed to answer. Never
    /// throws except for cancellation of <paramref name="cancellationToken"/>.
    /// </summary>
    Task<WorkerHardwareReport?> TryDetectAsync(
        bool startWorkerIfNeeded,
        CancellationToken cancellationToken = default);
}

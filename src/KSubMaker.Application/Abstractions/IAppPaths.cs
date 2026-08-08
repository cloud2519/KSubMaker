namespace KSubMaker.Application.Abstractions;

/// <summary>
/// Every writable location the application uses. Nothing in the codebase may build these paths
/// itself — that is the "경로 하드코딩 금지" rule, and it is also what makes the cache/model
/// directories relocatable from the settings screen.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root under %LOCALAPPDATA%\KSubMaker (or the platform equivalent).</summary>
    string Root { get; }

    string DatabaseDirectory { get; }
    string DatabaseFile { get; }

    /// <summary>Per-job checkpoint cache.</summary>
    string CacheDirectory { get; }

    string ModelsDirectory { get; }
    string LogsDirectory { get; }

    /// <summary>Directory containing the bundled ffmpeg/ffprobe executables.</summary>
    string ToolsDirectory { get; }

    /// <summary>Checkpoint directory for a single job; created on demand.</summary>
    string JobCacheDirectory(string jobId);

    /// <summary>Local directory for an installed model.</summary>
    string ModelDirectory(string modelId);

    /// <summary>Applies user overrides from the settings screen.</summary>
    void ApplyOverrides(string? cacheDirectory, string? modelDirectory, string? logDirectory);

    /// <summary>Creates any missing directories. Safe to call repeatedly.</summary>
    void EnsureCreated();
}

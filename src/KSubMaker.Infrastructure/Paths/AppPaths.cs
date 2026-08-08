using KSubMaker.Application.Abstractions;

namespace KSubMaker.Infrastructure.Paths;

/// <summary>
/// The single place that knows where anything is written.
///
/// Every path is derived from one root (<c>%LOCALAPPDATA%\KSubMaker</c> on Windows). The cache,
/// models and logs directories can be relocated from the settings screen, so they are stored as
/// mutable fields behind a lock instead of being computed once in the constructor: settings can be
/// applied while a background scan is already enumerating paths.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private const string ProductFolderName = "KSubMaker";

    private readonly Lock _gate = new();

    private readonly string _root;
    private readonly string _defaultCache;
    private readonly string _defaultModels;
    private readonly string _defaultLogs;

    private string _cache;
    private string _models;
    private string _logs;

    public AppPaths()
        : this(DefaultRoot())
    {
    }

    /// <summary>Test seam: lets the integration tests point the whole tree at a temp folder.</summary>
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.GetFullPath(root);
        DatabaseDirectory = Path.Combine(_root, "database");
        DatabaseFile = Path.Combine(DatabaseDirectory, "ksubmaker.db");

        _defaultCache = Path.Combine(_root, "cache");
        _defaultModels = Path.Combine(_root, "models");
        _defaultLogs = Path.Combine(_root, "logs");

        _cache = _defaultCache;
        _models = _defaultModels;
        _logs = _defaultLogs;
    }

    /// <summary>
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> maps to <c>%LOCALAPPDATA%</c> on
    /// Windows and to <c>~/.local/share</c> elsewhere, so the same expression works on the Linux CI
    /// agent that compiles this assembly.
    /// </summary>
    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        // GetFolderPath returns an empty string on a stripped-down container; fall back to the
        // current user's profile so the application still has somewhere to write.
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify);
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, ProductFolderName);
    }

    public string Root => _root;

    public string DatabaseDirectory { get; }

    public string DatabaseFile { get; }

    public string CacheDirectory
    {
        get
        {
            lock (_gate)
            {
                return _cache;
            }
        }
    }

    public string ModelsDirectory
    {
        get
        {
            lock (_gate)
            {
                return _models;
            }
        }
    }

    public string LogsDirectory
    {
        get
        {
            lock (_gate)
            {
                return _logs;
            }
        }
    }

    /// <summary>
    /// ffmpeg / ffprobe ship next to the executable rather than under %LOCALAPPDATA%, so this one is
    /// relative to the install directory and is never user-overridable.
    /// </summary>
    public string ToolsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "tools");

    public string JobCacheDirectory(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return Path.Combine(CacheDirectory, Sanitize(jobId));
    }

    public string ModelDirectory(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return Path.Combine(ModelsDirectory, Sanitize(modelId));
    }

    public void ApplyOverrides(string? cacheDirectory, string? modelDirectory, string? logDirectory)
    {
        lock (_gate)
        {
            _cache = Normalize(cacheDirectory) ?? _defaultCache;
            _models = Normalize(modelDirectory) ?? _defaultModels;
            _logs = Normalize(logDirectory) ?? _defaultLogs;
        }
    }

    public void EnsureCreated()
    {
        // Snapshot under the lock, create outside it: directory creation can block on a slow network
        // share and must not stall a concurrent path read.
        string cache, models, logs;
        lock (_gate)
        {
            cache = _cache;
            models = _models;
            logs = _logs;
        }

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(logs);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed override must not take the application down at startup; the default wins.
            return null;
        }
    }

    /// <summary>
    /// Job ids and model ids are used verbatim as directory names. They come from our own code, but a
    /// model id such as <c>faster-whisper/large</c> would otherwise silently create a nested folder.
    /// </summary>
    private static string Sanitize(string component)
    {
        Span<char> buffer = component.Length <= 128 ? stackalloc char[component.Length] : new char[component.Length];
        var invalid = Path.GetInvalidFileNameChars();

        for (var i = 0; i < component.Length; i++)
        {
            var c = component[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar
                ? '_'
                : c;
        }

        return new string(buffer);
    }
}

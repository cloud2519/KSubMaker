using System.Collections.Concurrent;
using System.Text;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.UnitTests.Fakes;

/// <summary><see cref="IAppPaths"/> over invented in-memory paths.</summary>
public sealed class FakeAppPaths(string root = "/appdata") : IAppPaths
{
    public string Root { get; } = root;

    public string DatabaseDirectory => Root + "/database";

    public string DatabaseFile => DatabaseDirectory + "/ksubmaker.db";

    public string CacheDirectory { get; private set; } = root + "/cache";

    public string ModelsDirectory { get; private set; } = root + "/models";

    public string LogsDirectory { get; private set; } = root + "/logs";

    public string ToolsDirectory => Root + "/tools";

    public string JobCacheDirectory(string jobId) => CacheDirectory + "/" + jobId;

    public string ModelDirectory(string modelId) => ModelsDirectory + "/" + modelId;

    public void ApplyOverrides(string? cacheDirectory, string? modelDirectory, string? logDirectory)
    {
        CacheDirectory = cacheDirectory ?? Root + "/cache";
        ModelsDirectory = modelDirectory ?? Root + "/models";
        LogsDirectory = logDirectory ?? Root + "/logs";
    }

    public void EnsureCreated()
    {
    }
}

/// <summary>Fully in-memory <see cref="ICheckpointStore"/>, so checkpoint semantics can be unit tested.</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, JobCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TranscriptionResult> _transcriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<int, string>> _partials = new(StringComparer.Ordinal);

    public Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_checkpoints.TryGetValue(jobId, out var value) ? value : null);

    public Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _checkpoints[checkpoint.JobId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task<TranscriptionResult?> LoadTranscriptionAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_transcriptions.TryGetValue(jobId, out var value) ? value : null);

    public Task SaveTranscriptionAsync(string jobId, TranscriptionResult result, CancellationToken cancellationToken = default)
    {
        _transcriptions[jobId] = result;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<int, string>> LoadPartialTranslationAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(
            _partials.TryGetValue(jobId, out var value)
                ? new Dictionary<int, string>(value)
                : new Dictionary<int, string>());

    public Task SavePartialTranslationAsync(
        string jobId,
        IReadOnlyDictionary<int, string> translations,
        CancellationToken cancellationToken = default)
    {
        _partials[jobId] = new Dictionary<int, string>(translations);
        return Task.CompletedTask;
    }

    public Task<long> DeleteAudioAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    public Task ClearAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _checkpoints.TryRemove(jobId, out _);
        _transcriptions.TryRemove(jobId, out _);
        _partials.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task<long> CleanupOrphansAsync(IReadOnlyCollection<string> knownJobIds, CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(knownJobIds, StringComparer.Ordinal);
        var removed = 0L;

        foreach (var id in _checkpoints.Keys.Where(id => !known.Contains(id)).ToArray())
        {
            _checkpoints.TryRemove(id, out _);
            removed++;
        }

        return Task.FromResult(removed);
    }

    // ---- test seams -------------------------------------------------------

    public JobCheckpoint? Peek(string jobId) => _checkpoints.TryGetValue(jobId, out var value) ? value : null;

    public IReadOnlyDictionary<int, string> PeekPartial(string jobId) =>
        _partials.TryGetValue(jobId, out var value) ? value : new Dictionary<int, string>();

    /// <summary>Simulates a partially-written translation checkpoint by keeping only the given ids.</summary>
    public void TruncatePartialTranslation(string jobId, Func<int, bool> keep)
    {
        if (!_partials.TryGetValue(jobId, out var value))
        {
            return;
        }

        _partials[jobId] = value.Where(kvp => keep(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}

/// <summary>Writes a valid, empty WAV into the in-memory file system and counts invocations.</summary>
public sealed class CountingAudioExtractor(InMemoryFileSystem fileSystem) : IAudioExtractor
{
    public int Calls { get; private set; }

    public List<string> Outputs { get; } = [];

    public Task ExtractAsync(AudioExtractionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls++;
        Outputs.Add(request.OutputWavPath);

        var directory = Path.GetDirectoryName(request.OutputWavPath);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }

        fileSystem.AddFile(request.OutputWavPath, size: 44, content: Encoding.ASCII.GetBytes("RIFF____WAVEfmt "));
        progress?.Report(100d);

        return Task.CompletedTask;
    }
}

/// <summary>Returns a fixed transcript and counts how many times ASR actually ran.</summary>
public sealed class CountingTranscriber(IReadOnlyList<TranscriptionSegment> segments, string language = "en") : ITranscriber
{
    public int Calls { get; private set; }

    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Calls++;
        progress?.Report(100d);

        return Task.FromResult(new TranscriptionResult
        {
            SourceLanguage = language,
            LanguageProbability = 0.97d,
            Segments = segments,
            ModelId = "counting-fake",
            DurationSeconds = segments.Count == 0 ? 0d : segments[^1].End
        });
    }
}

/// <summary>Echoes a Korean-marked translation per id and records every id it was asked for.</summary>
public sealed class CountingTranslationEngine : ITranslationEngine
{
    public int Calls { get; private set; }

    /// <summary>Ids requested, in call order, one list per invocation.</summary>
    public List<int[]> RequestedBatches { get; } = [];

    public List<int> AllRequestedIds { get; } = [];

    public List<TranslationContext> Contexts { get; } = [];

    /// <summary>When set, the engine drops these ids from its first response to force a retry.</summary>
    public HashSet<int> DropOnFirstAttempt { get; } = [];

    private bool _dropped;

    public Task<IReadOnlyList<TranslatedSubtitleItem>> TranslateAsync(
        IReadOnlyList<SubtitleItem> items,
        TranslationContext context,
        CancellationToken cancellationToken)
    {
        Calls++;
        RequestedBatches.Add(items.Select(i => i.Id).ToArray());
        AllRequestedIds.AddRange(items.Select(i => i.Id));
        Contexts.Add(context);

        var drop = !_dropped && DropOnFirstAttempt.Count > 0;
        _dropped |= drop;

        var result = items
            .Where(i => !drop || !DropOnFirstAttempt.Contains(i.Id))
            .Select(i => new TranslatedSubtitleItem(i.Id, "[테스트] " + i.Text))
            .ToArray();

        return Task.FromResult<IReadOnlyList<TranslatedSubtitleItem>>(result);
    }
}

/// <summary>
/// A translation engine whose reply is written by the test.
///
/// <see cref="CountingTranslationEngine"/> covers the well-behaved cases; this one exists for the
/// misbehaving ones — an engine that deterministically blanks a line, one that answers with ids
/// nobody asked for, one that duplicates. The callback receives the requested items and the
/// 1-based attempt number so a response can change (or pointedly not change) between retries.
/// </summary>
public sealed class ProgrammableTranslationEngine(
    Func<IReadOnlyList<SubtitleItem>, int, IReadOnlyList<TranslatedSubtitleItem>> respond) : ITranslationEngine
{
    private int _calls;

    /// <summary>How many times the engine was actually invoked.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Ids requested, in call order, one array per invocation.</summary>
    public List<int[]> RequestedBatches { get; } = [];

    public IEnumerable<int> AllRequestedIds => RequestedBatches.SelectMany(ids => ids);

    /// <summary>Convenience: reply "[테스트] " + source for every id, blanking the listed ones.</summary>
    public static ProgrammableTranslationEngine Blanking(params int[] alwaysBlankIds)
    {
        var blank = new HashSet<int>(alwaysBlankIds);

        return new ProgrammableTranslationEngine((items, _) => items
            .Select(i => new TranslatedSubtitleItem(i.Id, blank.Contains(i.Id) ? string.Empty : "[테스트] " + i.Text))
            .ToArray());
    }

    public Task<IReadOnlyList<TranslatedSubtitleItem>> TranslateAsync(
        IReadOnlyList<SubtitleItem> items,
        TranslationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var attempt = Interlocked.Increment(ref _calls);
        RequestedBatches.Add(items.Select(i => i.Id).ToArray());

        return Task.FromResult(respond(items, attempt));
    }
}

/// <summary>Applies the real conflict policy against the in-memory file system and records the cues.</summary>
public sealed class RecordingSubtitleWriter(InMemoryFileSystem fileSystem) : ISubtitleWriter
{
    public int Calls { get; private set; }

    public List<IReadOnlyList<SubtitleCue>> Written { get; } = [];

    public Task<string?> WriteAsync(
        IReadOnlyList<SubtitleCue> cues,
        string desiredPath,
        OutputConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        Calls++;

        var resolution = OutputPathResolver.Resolve(desiredPath, conflictPolicy, fileSystem.FileExists);
        if (!resolution.ShouldWrite)
        {
            return Task.FromResult<string?>(null);
        }

        Written.Add(cues);

        var body = SrtFormatter.ToWindowsLineEndings(SrtFormatter.Write(cues));
        fileSystem.AddFile(resolution.Path, content: Encoding.UTF8.GetBytes(body));

        return Task.FromResult<string?>(resolution.Path);
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when the test says so.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public FixedTimeProvider()
        : this(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

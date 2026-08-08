using System.Text.Json;
using System.Text.Json.Serialization;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Subtitles;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Checkpoints;

/// <summary>
/// Per-job checkpoints as JSON files under <c>cache/{jobId}</c>.
///
/// Files, not database rows, for two reasons: a transcription of a two-hour film is several hundred
/// kilobytes of JSON that nothing ever queries, and a corrupt checkpoint must be individually
/// discardable without touching the queue. Every write goes to <c>X.tmp</c>, is flushed to disk and
/// only then moved over the target, so a power cut leaves either the previous complete file or the
/// new complete file — never a truncated one.
/// </summary>
public sealed class FileCheckpointStore(IAppPaths paths, ILogger<FileCheckpointStore> logger) : ICheckpointStore
{
    private const string JobFileName = "job.json";
    private const string TranscriptionFileName = "transcription.json";
    private const string PartialTranslationFileName = "translation.partial.json";

    /// <summary>Mirrors <c>CheckpointStore.audio_path()</c> in the Python worker.</summary>
    private const string AudioFileName = "audio.wav";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enum names, never ordinals: JobStage.Transcribing must still mean transcribing after a
        // member is inserted into the enum in a later release.
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppPaths _paths = paths;
    private readonly ILogger<FileCheckpointStore> _logger = logger;

    public Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default) =>
        ReadAsync<JobCheckpoint>(jobId, JobFileName, cancellationToken);

    public Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return WriteAsync(checkpoint.JobId, JobFileName, checkpoint, cancellationToken);
    }

    public Task<TranscriptionResult?> LoadTranscriptionAsync(string jobId, CancellationToken cancellationToken = default) =>
        ReadAsync<TranscriptionResult>(jobId, TranscriptionFileName, cancellationToken);

    public Task SaveTranscriptionAsync(string jobId, TranscriptionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return WriteAsync(jobId, TranscriptionFileName, result, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, string>> LoadPartialTranslationAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var map = await ReadAsync<Dictionary<int, string>>(jobId, PartialTranslationFileName, cancellationToken)
            .ConfigureAwait(false);

        // An absent or unreadable partial translation is not an error: it just means nothing has been
        // translated yet, and the pipeline starts from the first batch.
        return map ?? new Dictionary<int, string>();
    }

    public Task SavePartialTranslationAsync(
        string jobId,
        IReadOnlyDictionary<int, string> translations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translations);

        // Materialise into a concrete dictionary: the caller keeps mutating its own instance while
        // this write is in flight, and System.Text.Json would otherwise enumerate it mid-change.
        var snapshot = new Dictionary<int, string>(translations.Count);
        foreach (var (id, text) in translations)
        {
            snapshot[id] = text;
        }

        return WriteAsync(jobId, PartialTranslationFileName, snapshot, cancellationToken);
    }

    public Task<long> DeleteAudioAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        // Must match the worker's CheckpointStore.audio_path(). The two sides write the same
        // directory, and a rename on either side without the other silently stops reclaiming.
        var audio = Path.Combine(_paths.JobCacheDirectory(jobId), AudioFileName);

        try
        {
            var info = new FileInfo(audio);
            if (!info.Exists)
            {
                return Task.FromResult(0L);
            }

            var size = info.Length;
            info.Delete();

            _logger.LogDebug("추출된 음성을 정리했습니다: {JobId} ({Bytes} bytes)", jobId, size);
            return Task.FromResult(size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Antivirus or a media player still holding the file. Housekeeping must never fail the
            // job that just succeeded; the orphan sweep reclaims it once the job is removed.
            _logger.LogDebug(ex, "추출된 음성을 지우지 못했습니다: {Path}", audio);
            return Task.FromResult(0L);
        }
    }

    public Task ClearAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = _paths.JobCacheDirectory(jobId);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _logger.LogDebug("체크포인트를 삭제했습니다: {JobId}", jobId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked audio.wav (antivirus, media player) must not fail the queue operation that
            // triggered the cleanup; the orphan sweep will get it next time.
            _logger.LogWarning(ex, "체크포인트 폴더를 삭제하지 못했습니다: {Directory}", directory);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes cache directories whose job no longer exists, plus <c>*.tmp</c> files abandoned by a
    /// crash mid-write. Sizes are measured before deletion so the caller can tell the user how much
    /// space was reclaimed.
    /// </summary>
    public Task<long> CleanupOrphansAsync(
        IReadOnlyCollection<string> knownJobIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownJobIds);

        var cacheRoot = _paths.CacheDirectory;
        if (!Directory.Exists(cacheRoot))
        {
            return Task.FromResult(0L);
        }

        // Compare on the sanitised directory name, because that is what JobCacheDirectory produced
        // when the folder was created.
        var known = new HashSet<string>(
            knownJobIds.Select(id => Path.GetFileName(_paths.JobCacheDirectory(id))),
            StringComparer.OrdinalIgnoreCase);

        var reclaimed = 0L;

        foreach (var directory in SafeEnumerateDirectories(cacheRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (known.Contains(name))
            {
                // Live job: only sweep the temp files left behind by an interrupted write.
                reclaimed += DeleteStrayTempFiles(directory, cancellationToken);
                continue;
            }

            var size = DirectorySize(directory, cancellationToken);

            try
            {
                Directory.Delete(directory, recursive: true);
                reclaimed += size;
                _logger.LogInformation("사용하지 않는 캐시 폴더를 삭제했습니다: {Directory}", directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "캐시 폴더를 삭제하지 못했습니다: {Directory}", directory);
            }
        }

        // Stray temp files directly under the cache root (older layouts wrote them there).
        reclaimed += DeleteStrayTempFiles(cacheRoot, cancellationToken);

        return Task.FromResult(reclaimed);
    }

    // -----------------------------------------------------------------------
    // Atomic read / write
    // -----------------------------------------------------------------------

    private async Task<T?> ReadAsync<T>(string jobId, string fileName, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var path = Path.Combine(_paths.JobCacheDirectory(jobId), fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            // A truncated checkpoint (pre-atomic-write build, or a disk that lied about flushing)
            // must degrade to "no checkpoint" and let the stage run again, never take the job down.
            _logger.LogWarning(ex, "체크포인트 파일이 손상되어 무시합니다: {Path}", path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "체크포인트 파일을 읽지 못했습니다: {Path}", path);
            return null;
        }
    }

    private async Task WriteAsync<T>(string jobId, string fileName, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var directory = _paths.JobCacheDirectory(jobId);
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, fileName);
        var tempPath = finalPath + TempSuffix;

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken).ConfigureAwait(false);

                // Flush all the way to the device before the move: without this the rename can reach
                // the disk before the data does, which is exactly the corruption this design avoids.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // Housekeeping helpers
    // -----------------------------------------------------------------------

    private long DeleteStrayTempFiles(string directory, CancellationToken cancellationToken)
    {
        var reclaimed = 0L;

        foreach (var file in SafeEnumerateFiles(directory, "*" + TempSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                reclaimed += size;
                _logger.LogDebug("남아 있던 임시 파일을 삭제했습니다: {Path}", file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "임시 파일을 삭제하지 못했습니다: {Path}", file);
            }
        }

        return reclaimed;
    }

    private long DirectorySize(string directory, CancellationToken cancellationToken)
    {
        var total = 0L;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "파일 크기를 확인하지 못했습니다: {Path}", file);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "캐시 폴더 크기를 계산하지 못했습니다: {Directory}", directory);
        }

        return total;
    }

    private string[] SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "캐시 폴더를 열지 못했습니다: {Directory}", root);
            return [];
        }
    }

    private string[] SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "임시 파일 목록을 읽지 못했습니다: {Directory}", directory);
            return [];
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "임시 체크포인트 파일을 삭제하지 못했습니다: {Path}", path);
        }
    }
}

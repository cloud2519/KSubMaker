using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Models;

/// <summary>
/// Downloads model payloads from Hugging Face over HTTPS, resumably and verifiably.
///
/// Design points worth knowing before changing anything here:
/// <list type="bullet">
/// <item>The <b>file list is discovered</b>, not hardcoded. The hub tree call that produces the digests
/// also produces the file names, so <see cref="ModelFileSelector"/> picks the model's files out of it.
/// <c>ModelDescriptor.FallbackFiles</c> is only consulted when that call fails. A resolved set that is
/// empty, or that has no weights in it, is a hard failure — never a silent "downloaded 0 files, done".</item>
/// <item>Every byte goes into <c>&lt;file&gt;.part</c> first. A cancelled or crashed download leaves
/// the partial file in place on purpose — that is what makes the next attempt a resume rather than a
/// restart, which matters when the payload is 3 GB.</item>
/// <item>Digests come from the hub tree API. Only LFS entries publish a real SHA-256; a plain git
/// blob's <c>oid</c> is a SHA-1 of the git object header plus content and is *not* comparable to a
/// file hash, so small files are hashed locally and recorded without a remote comparison. That is a
/// genuine limitation, not an oversight: the large weight files (the ones worth verifying) are all
/// LFS.</item>
/// <item>Verification is offline. <see cref="VerifyAsync"/> only reads the manifest and re-hashes
/// what is on disk, so a user with no network can still check an installation.</item>
/// </list>
/// </summary>
public sealed class HttpModelManager(
    IHttpClientFactory httpClientFactory,
    ModelCatalog catalog,
    IAppPaths paths,
    IModelRepository repository,
    HardwareService hardwareService,
    ILogger<HttpModelManager> logger) : IModelManager
{
    /// <summary>Named client configured in <c>DependencyInjection</c>.</summary>
    public const string HttpClientName = "KSubMaker.ModelDownload";

    public const string ManifestFileName = ".ksubmaker-manifest.json";

    private const string PartSuffix = ".part";
    private const string HuggingFaceHost = "huggingface.co";

    /// <summary>No bytes for this long means the connection is dead, not slow.</summary>
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ModelCatalog _catalog = catalog;
    private readonly IAppPaths _paths = paths;
    private readonly IModelRepository _repository = repository;
    private readonly HardwareService _hardwareService = hardwareService;
    private readonly ILogger<HttpModelManager> _logger = logger;

    /// <summary>Live downloads, so <see cref="GetStatusAsync(CancellationToken)"/> can show progress.</summary>
    private readonly ConcurrentDictionary<string, double> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var installations = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var byId = installations.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var recommendation = await TryGetRecommendationAsync(cancellationToken).ConfigureAwait(false);
        var computeType = recommendation?.ComputeType ?? ComputeType.Int8Float16;

        var result = new List<ModelStatus>(_catalog.All.Count);

        foreach (var descriptor in _catalog.All.OrderBy(d => d.Kind).ThenBy(d => d.ApproxSizeBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            byId.TryGetValue(descriptor.Id, out var record);
            var installation = await SynchroniseAsync(descriptor, record, cancellationToken).ConfigureAwait(false);

            result.Add(new ModelStatus
            {
                Descriptor = descriptor,
                Installation = installation,
                IsDownloading = _inFlight.ContainsKey(descriptor.Id),
                DownloadPercent = _inFlight.TryGetValue(descriptor.Id, out var percent) ? percent : 0d,
                EstimatedVramGb = _catalog.EstimatedVramGb(descriptor.Id, computeType),
                IsRecommended = IsRecommended(descriptor, recommendation)
            });
        }

        return result;
    }

    public async Task<ModelStatus?> GetStatusAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = _catalog.Find(modelId);
        if (descriptor is null)
        {
            return null;
        }

        var record = await _repository.FindAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        var installation = await SynchroniseAsync(descriptor, record, cancellationToken).ConfigureAwait(false);

        var recommendation = await TryGetRecommendationAsync(cancellationToken).ConfigureAwait(false);
        var computeType = recommendation?.ComputeType ?? ComputeType.Int8Float16;

        return new ModelStatus
        {
            Descriptor = descriptor,
            Installation = installation,
            IsDownloading = _inFlight.ContainsKey(descriptor.Id),
            DownloadPercent = _inFlight.TryGetValue(descriptor.Id, out var percent) ? percent : 0d,
            EstimatedVramGb = _catalog.EstimatedVramGb(descriptor.Id, computeType),
            IsRecommended = IsRecommended(descriptor, recommendation)
        };
    }

    public async Task<bool> IsInstalledAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = _catalog.Find(modelId);
        if (descriptor is null)
        {
            return false;
        }

        var manifest = await ReadManifestAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return false;
        }

        var directory = _paths.ModelDirectory(descriptor.Id);

        return manifest.Files.Count > 0 &&
               manifest.Files.All(f => File.Exists(Path.Combine(directory, ToLocalRelativePath(f.RelativePath))));
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    public async Task DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var descriptor = _catalog.Get(modelId);
        var directory = _paths.ModelDirectory(descriptor.Id);
        Directory.CreateDirectory(directory);

        if (!_inFlight.TryAdd(descriptor.Id, 0d))
        {
            _logger.LogInformation("이미 다운로드 중인 모델입니다: {ModelId}", descriptor.Id);
            return;
        }

        try
        {
            using var client = CreateClient();

            var listing = await FetchRemoteFilesAsync(client, descriptor, cancellationToken).ConfigureAwait(false);
            var selected = ResolveFiles(descriptor, listing);
            var plan = BuildPlan(descriptor, selected, listing.Files);
            var totalBytes = plan.Sum(f => f.SizeBytes);

            var reporter = new AggregateProgress(descriptor.Id, totalBytes, progress);
            var entries = new List<ModelFileEntry>(plan.Count);

            foreach (var file in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = await DownloadFileAsync(client, descriptor, file, directory, reporter, cancellationToken)
                    .ConfigureAwait(false);

                entries.Add(entry);
                reporter.CompleteFile(entry.SizeBytes);
                _inFlight[descriptor.Id] = reporter.Percent;
            }

            var manifest = new ModelManifest
            {
                ModelId = descriptor.Id,
                RepositoryId = descriptor.RepositoryId,
                Files = entries,
                CreatedAtUtc = DateTime.UtcNow
            };

            var manifestHash = await WriteManifestAsync(directory, manifest, cancellationToken).ConfigureAwait(false);

            await _repository.UpsertAsync(new ModelInstallation
            {
                Id = descriptor.Id,
                Type = descriptor.Kind,
                Name = descriptor.DisplayName,
                Version = "1",
                // llama.cpp is handed one path and opens the remaining shards itself, so this has to be
                // the *first* shard rather than whichever entry happens to be first in the list.
                LocalPath = descriptor.Layout == ModelPayloadLayout.EntryPointFile
                    ? Path.Combine(directory, ToLocalRelativePath(ModelFileSelector.EntryPointFile(descriptor, selected)))
                    : directory,
                DownloadUrl = $"https://{HuggingFaceHost}/{descriptor.RepositoryId}",
                Sha256 = manifestHash,
                SizeBytes = manifest.TotalBytes,
                Installed = true,
                Verified = true,
                RecommendedVramBytes = (long)(_catalog.EstimatedVramGb(descriptor.Id, ComputeType.Float16) * 1024 * 1024 * 1024),
                InstalledAtUtc = DateTime.UtcNow,
                VerifiedAtUtc = DateTime.UtcNow,
                DownloadedBytes = manifest.TotalBytes
            }, cancellationToken).ConfigureAwait(false);

            reporter.ReportFinished();

            _logger.LogInformation(
                "모델 다운로드를 완료했습니다: {ModelId} ({Files}개 파일, {Size:N0} bytes)",
                descriptor.Id, entries.Count, manifest.TotalBytes);
        }
        catch (OperationCanceledException)
        {
            // The *.part files are left exactly where they are: that is the resume point.
            _logger.LogInformation("모델 다운로드를 취소했습니다. 다음에 이어받을 수 있습니다: {ModelId}", descriptor.Id);
            await RecordPartialProgressAsync(descriptor, directory, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (ModelDownloadException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "모델 다운로드 중 네트워크 오류가 발생했습니다: {ModelId}", descriptor.Id);
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                $"모델을 내려받지 못했습니다. 네트워크 연결을 확인해 주세요. ({ex.Message})",
                ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "모델 저장 중 오류가 발생했습니다: {ModelId}", descriptor.Id);
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                $"모델 파일을 저장하지 못했습니다. 디스크 여유 공간을 확인해 주세요. ({ex.Message})",
                ex);
        }
        finally
        {
            _inFlight.TryRemove(descriptor.Id, out _);
        }
    }

    private async Task<ModelFileEntry> DownloadFileAsync(
        HttpClient client,
        ModelDescriptor descriptor,
        PlannedFile file,
        string directory,
        AggregateProgress reporter,
        CancellationToken cancellationToken)
    {
        var localRelative = ToLocalRelativePath(file.RelativePath);
        var finalPath = ResolveInside(directory, localRelative);

        var parent = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Already installed and the right size: nothing to do. When the remote size is unknown the
        // mere presence of the file has to be enough, since there is nothing to compare against.
        //
        // When a remote digest exists it is recorded as-is rather than re-hashing a 3 GB file on
        // every re-download. If the file on disk has since diverged from that digest, VerifyAsync is
        // the thing that reports it — that is exactly what offline verification is for.
        if (File.Exists(finalPath))
        {
            var existingLength = new FileInfo(finalPath).Length;
            if (file.RemoteSizeKnown ? existingLength == file.SizeBytes : existingLength > 0)
            {
                var existingHash = file.Sha256 ?? await ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false);
                reporter.AdvanceTo(existingLength, file.RelativePath);
                return new ModelFileEntry(file.RelativePath, existingLength, existingHash);
            }

            _logger.LogWarning(
                "크기가 맞지 않아 다시 내려받습니다: {File} (예상 {Expected:N0}, 실제 {Actual:N0})",
                file.RelativePath, file.SizeBytes, existingLength);

            File.Delete(finalPath);
        }

        var partPath = finalPath + PartSuffix;
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        // Resuming is only safe when the finished file can be checked against a remote digest.
        // Without one, an existing *.part whose bytes no longer match the current remote content
        // (the repository's main branch moved, a previous write was interrupted mid-buffer) would be
        // completed to the right *length* and pass every check while being silently corrupt.
        // Everything without an LFS digest on the hub is a small text/JSON blob, so restarting those
        // from zero costs a few hundred kilobytes and removes that failure mode entirely.
        if (resumeFrom > 0 && file.Sha256 is null)
        {
            _logger.LogDebug(
                "원격 해시가 없는 파일이라 이어받지 않고 처음부터 내려받습니다: {File}", file.RelativePath);
            File.Delete(partPath);
            resumeFrom = 0L;
        }

        // A part longer than the known remote size can only come from a corrupted or replaced remote
        // file; restarting is the only correct move.
        if (file.RemoteSizeKnown && resumeFrom > file.SizeBytes)
        {
            _logger.LogWarning("이어받기 파일이 원본보다 커서 처음부터 다시 내려받습니다: {File}", file.RelativePath);
            File.Delete(partPath);
            resumeFrom = 0L;
        }

        if (!file.RemoteSizeKnown || resumeFrom < file.SizeBytes)
        {
            resumeFrom = await FetchAsync(client, file, partPath, resumeFrom, reporter, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (resumeFrom > 0)
        {
            _logger.LogDebug("이미 모두 받은 파일입니다: {File}", file.RelativePath);
        }

        var hash = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);

        if (file.Sha256 is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            // Delete the part: keeping a file whose hash is wrong would make the next resume append
            // to known-bad bytes forever.
            TryDelete(partPath);

            throw new ModelDownloadException(
                ErrorCodes.ModelVerificationFailed,
                $"내려받은 파일의 검증에 실패했습니다: {file.RelativePath}. 다시 시도해 주세요.");
        }

        File.Move(partPath, finalPath, overwrite: true);

        var size = new FileInfo(finalPath).Length;
        _logger.LogDebug("모델 파일 저장 완료: {Model}/{File} ({Size:N0} bytes)", descriptor.Id, file.RelativePath, size);

        return new ModelFileEntry(file.RelativePath, size, hash);
    }

    /// <summary>Streams one file into <c>*.part</c>, resuming from <paramref name="resumeFrom"/>.</summary>
    private async Task<long> FetchAsync(
        HttpClient client,
        PlannedFile file,
        string partPath,
        long resumeFrom,
        AggregateProgress reporter,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);

        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // The hub redirects large files to a CDN. SocketsHttpHandler refuses an HTTPS→HTTP redirect
        // on its own, but the final URI is checked anyway so the rule is enforced, not assumed.
        EnsureHttps(response.RequestMessage?.RequestUri ?? file.Url);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server says our part is already at or past the end of the file: start over.
            _logger.LogWarning("이어받기 범위가 잘못되어 처음부터 다시 내려받습니다: {File}", file.RelativePath);
            TryDelete(partPath);
            return await FetchAsync(client, file, partPath, 0L, reporter, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                $"모델 파일을 내려받지 못했습니다({(int)response.StatusCode}): {file.RelativePath}");
        }

        // A server that ignores the Range header answers 200 with the whole body; appending that to
        // an existing part would silently produce a corrupt file.
        var append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !append)
        {
            _logger.LogWarning("서버가 이어받기를 지원하지 않아 처음부터 내려받습니다: {File}", file.RelativePath);
            resumeFrom = 0L;
        }

        reporter.AdvanceTo(resumeFrom, file.RelativePath);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 20,
            useAsync: true);

        var buffer = new byte[1 << 20];
        var written = resumeFrom;

        // Reset on every chunk: this catches a connection that goes silent without the OS ever
        // reporting a socket error, which is the usual failure mode on flaky Wi-Fi.
        using var stallSource = new CancellationTokenSource(ReadStallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stallSource.Token);

        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ModelDownloadException(
                    ErrorCodes.ModelDownloadFailed,
                    $"다운로드가 {ReadStallTimeout.TotalSeconds:0}초 동안 응답하지 않아 중단했습니다: {file.RelativePath}");
            }

            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            stallSource.CancelAfter(ReadStallTimeout);
            reporter.AdvanceTo(written, file.RelativePath);
            _inFlight[reporter.ModelId] = reporter.Percent;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    // -----------------------------------------------------------------------
    // Verify / delete
    // -----------------------------------------------------------------------

    public async Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var descriptor = _catalog.Find(modelId);
        if (descriptor is null)
        {
            _logger.LogWarning("알 수 없는 모델을 검증하려 했습니다: {ModelId}", modelId);
            return false;
        }

        var manifest = await ReadManifestAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            _logger.LogWarning("검증에 필요한 매니페스트가 없습니다: {ModelId}", descriptor.Id);
            await MarkVerifiedAsync(descriptor.Id, verified: false, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var directory = _paths.ModelDirectory(descriptor.Id);
        var ok = true;

        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(directory, ToLocalRelativePath(entry.RelativePath));

            if (!File.Exists(path))
            {
                _logger.LogWarning("모델 파일이 없습니다: {Path}", path);
                ok = false;
                continue;
            }

            if (new FileInfo(path).Length != entry.SizeBytes)
            {
                _logger.LogWarning("모델 파일 크기가 일치하지 않습니다: {Path}", path);
                ok = false;
                continue;
            }

            var hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("모델 파일의 해시가 일치하지 않습니다: {Path}", path);
                ok = false;
            }
        }

        await MarkVerifiedAsync(descriptor.Id, ok, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("모델 검증 결과 {ModelId}: {Result}", descriptor.Id, ok ? "정상" : "손상");
        return ok;
    }

    public async Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var descriptor = _catalog.Find(modelId);
        var id = descriptor?.Id ?? modelId;
        var directory = _paths.ModelDirectory(id);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _logger.LogInformation("모델을 삭제했습니다: {ModelId}", id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "모델 폴더를 삭제하지 못했습니다: {Directory}", directory);
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                "모델 파일을 삭제하지 못했습니다. 다른 프로그램이 파일을 사용 중일 수 있습니다.",
                ex);
        }

        await _repository.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    public async Task<string?> ResolveModelIdAsync(
        string requested,
        ModelKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            !requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // An explicit choice is honoured even if it is not installed yet; the caller decides
            // whether to trigger a download.
            var explicitDescriptor = _catalog.Find(requested);
            if (explicitDescriptor is not null && explicitDescriptor.Kind == kind)
            {
                return explicitDescriptor.Id;
            }

            _logger.LogWarning(
                "설정에 지정된 모델 '{Requested}'을(를) 카탈로그에서 찾지 못해 자동 선택으로 대체합니다.", requested);
        }

        var recommendation = await TryGetRecommendationAsync(cancellationToken).ConfigureAwait(false);

        var recommended = recommendation is null ? null : kind switch
        {
            ModelKind.Whisper => recommendation.WhisperModelId,
            ModelKind.Translation => recommendation.TranslationModelId,
            ModelKind.Llm => recommendation.LlmModelId,
            _ => null
        };

        if (recommended is not null &&
            await IsInstalledAsync(recommended, cancellationToken).ConfigureAwait(false))
        {
            return recommended;
        }

        // Fall back to the largest installed model of the right kind: a bigger model is slower but
        // more accurate, and "installed" is the only hard constraint at this point.
        foreach (var descriptor in _catalog.OfKind(kind).OrderByDescending(d => d.ApproxSizeBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsInstalledAsync(descriptor.Id, cancellationToken).ConfigureAwait(false))
            {
                if (recommended is not null)
                {
                    _logger.LogInformation(
                        "권장 모델 {Recommended}이(가) 설치되어 있지 않아 {Fallback}을(를) 사용합니다.",
                        recommended, descriptor.Id);
                }

                return descriptor.Id;
            }
        }

        _logger.LogWarning("설치된 {Kind} 모델이 없습니다.", kind);
        return null;
    }

    // -----------------------------------------------------------------------
    // Remote metadata
    // -----------------------------------------------------------------------

    private sealed record RemoteFile(long Size, string? Sha256);

    /// <summary>
    /// The repository listing. <paramref name="Available"/> separates "the hub answered" from "we could
    /// not ask" — a distinction that matters now that the file list comes from here: an unreachable hub
    /// falls back to the catalog, whereas a repository that really is empty (or renamed, so the API
    /// answers about something else) has to fail loudly.
    /// </summary>
    private sealed record RemoteListing(bool Available, IReadOnlyDictionary<string, RemoteFile> Files)
    {
        public static RemoteListing Unavailable { get; } =
            new(false, new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record PlannedFile(string RelativePath, Uri Url, long SizeBytes, bool RemoteSizeKnown, string? Sha256);

    /// <summary>
    /// Asks the hub for the file listing: file names, sizes and (for LFS blobs) SHA-256 digests.
    /// A failure here is not fatal — the download falls back to the catalog's static list and loses
    /// digest comparison — but it is reported, because that is a degraded mode.
    /// </summary>
    private async Task<RemoteListing> FetchRemoteFilesAsync(
        HttpClient client,
        ModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var url = BuildUri($"https://{HuggingFaceHost}/api/models/{descriptor.RepositoryId}/tree/main?recursive=1");
        var map = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var timeout = new CancellationTokenSource(MetadataTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            using var response = await client.GetAsync(url, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "모델 파일 목록을 가져오지 못했습니다({Status}). 카탈로그의 기본 파일 목록으로 진행합니다: {Repo}",
                    (int)response.StatusCode, descriptor.RepositoryId);
                return RemoteListing.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("모델 파일 목록의 형식이 예상과 다릅니다: {Repo}", descriptor.RepositoryId);
                return RemoteListing.Unavailable;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("path", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var path = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                // Directories are listed too; only blobs are downloadable.
                if (element.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    !string.Equals(typeElement.GetString(), "file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : 0L;

                map[path] = new RemoteFile(size, ReadLfsSha256(element));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "모델 파일 목록을 가져오지 못해 카탈로그의 기본 파일 목록으로 진행합니다: {Repo}", descriptor.RepositoryId);
            return RemoteListing.Unavailable;
        }

        return new RemoteListing(true, map);
    }

    /// <summary>
    /// The files this model is actually made of.
    ///
    /// <para>The hub listing wins. The catalog's <c>FallbackFiles</c> only stands in when the listing
    /// could not be fetched, and even then it goes through the same selection rule so both paths agree
    /// on exclusions and ordering.</para>
    /// </summary>
    private IReadOnlyList<string> ResolveFiles(ModelDescriptor descriptor, RemoteListing listing)
    {
        IReadOnlyList<string> selected;

        if (listing.Available)
        {
            selected = ModelFileSelector.Select(descriptor, listing.Files.Keys);

            _logger.LogInformation(
                "저장소 목록에서 {Count}개 파일을 확인했습니다: {Repo} ({Files})",
                selected.Count, descriptor.RepositoryId, string.Join(", ", selected));
        }
        else
        {
            selected = ModelFileSelector.Select(descriptor, descriptor.FallbackFiles);

            _logger.LogWarning(
                "저장소 목록 없이 카탈로그의 기본 파일 목록으로 진행합니다: {Repo} ({Files})",
                descriptor.RepositoryId, string.Join(", ", selected));
        }

        // Downloading nothing and then writing a manifest would leave the user with "설치됨" on an empty
        // folder and a model-load failure on the next job. Refuse instead, and say which repository.
        if (ModelFileSelector.DescribeProblem(descriptor, selected) is { } problem)
        {
            _logger.LogError("모델 파일 목록을 확정하지 못했습니다: {ModelId} / {Repo}", descriptor.Id, descriptor.RepositoryId);
            throw new ModelDownloadException(ErrorCodes.ModelDownloadFailed, problem);
        }

        return selected;
    }

    /// <summary>
    /// Only the <c>lfs</c> block carries a usable digest. The top-level <c>oid</c> of a plain file is
    /// the git blob SHA-1 (<c>sha1("blob &lt;len&gt;\0" + content)</c>) and would never match a
    /// SHA-256 of the file, so it is deliberately ignored rather than "verified" against nothing.
    /// </summary>
    private static string? ReadLfsSha256(JsonElement element)
    {
        if (!element.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string[] candidateNames = ["sha256", "oid"];

        foreach (var name in candidateNames)
        {
            if (!lfs.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // The oid is sometimes prefixed, e.g. "sha256:9f86d0...".
            var separator = raw.IndexOf(':');
            var digest = separator >= 0 ? raw[(separator + 1)..] : raw;

            if (digest.Length == 64 && digest.All(Uri.IsHexDigit))
            {
                return digest;
            }
        }

        return null;
    }

    private static List<PlannedFile> BuildPlan(
        ModelDescriptor descriptor,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, RemoteFile> remote)
    {
        var plan = new List<PlannedFile>(files.Count);

        // Only used when the hub listing was unavailable, purely so the progress bar has a scale.
        var fallbackShare = files.Count == 0
            ? 0L
            : descriptor.ApproxSizeBytes / files.Count;

        foreach (var relative in files)
        {
            var url = BuildFileUri(descriptor.RepositoryId, relative);
            remote.TryGetValue(relative, out var info);

            var sizeKnown = info is { Size: > 0 };

            plan.Add(new PlannedFile(
                relative,
                url,
                sizeKnown ? info!.Size : fallbackShare,
                sizeKnown,
                info?.Sha256));
        }

        return plan;
    }

    // -----------------------------------------------------------------------
    // Manifest
    // -----------------------------------------------------------------------

    private async Task<ModelManifest?> ReadManifestAsync(string modelId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.ModelDirectory(modelId), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 32 * 1024, useAsync: true);

            return await JsonSerializer.DeserializeAsync<ModelManifest>(stream, ManifestJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "모델 매니페스트를 읽지 못했습니다: {Path}", path);
            return null;
        }
    }

    /// <summary>Writes the manifest atomically and returns its SHA-256.</summary>
    private static async Task<string> WriteManifestAsync(
        string directory,
        ModelManifest manifest,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, ManifestJson);
        var bytes = Encoding.UTF8.GetBytes(json);

        var finalPath = Path.Combine(directory, ManifestFileName);
        var tempPath = finalPath + PartSuffix;

        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, finalPath, overwrite: true);

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // -----------------------------------------------------------------------
    // Repository synchronisation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reconciles the database row with what is actually on disk. The files win: a user who deletes
    /// the model folder by hand must not keep seeing "설치됨".
    /// </summary>
    private async Task<ModelInstallation> SynchroniseAsync(
        ModelDescriptor descriptor,
        ModelInstallation? record,
        CancellationToken cancellationToken)
    {
        var directory = _paths.ModelDirectory(descriptor.Id);
        var installed = await IsInstalledAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);

        var installation = record ?? new ModelInstallation
        {
            Id = descriptor.Id,
            Type = descriptor.Kind,
            Name = descriptor.DisplayName,
            Version = "1"
        };

        installation.Type = descriptor.Kind;
        installation.Name = descriptor.DisplayName;
        installation.LocalPath = directory;
        installation.Installed = installed;

        if (!installed)
        {
            installation.Verified = false;
            installation.SizeBytes = 0;
            installation.DownloadedBytes = MeasurePartialBytes(directory);
            return installation;
        }

        var manifest = await ReadManifestAsync(descriptor.Id, cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            installation.SizeBytes = manifest.TotalBytes;
            installation.DownloadedBytes = manifest.TotalBytes;
            installation.InstalledAtUtc ??= manifest.CreatedAtUtc;
        }

        return installation;
    }

    private long MeasurePartialBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0L;
        }

        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "부분 다운로드 크기를 계산하지 못했습니다: {Directory}", directory);
            return 0L;
        }
    }

    private async Task RecordPartialProgressAsync(
        ModelDescriptor descriptor,
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await _repository.FindAsync(descriptor.Id, cancellationToken).ConfigureAwait(false)
                         ?? new ModelInstallation
                         {
                             Id = descriptor.Id,
                             Type = descriptor.Kind,
                             Name = descriptor.DisplayName,
                             Version = "1"
                         };

            record.LocalPath = directory;
            record.Installed = false;
            record.Verified = false;
            record.DownloadedBytes = MeasurePartialBytes(directory);

            await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping only: never let it mask the cancellation the caller is waiting on.
            _logger.LogDebug(ex, "부분 다운로드 상태를 저장하지 못했습니다: {ModelId}", descriptor.Id);
        }
    }

    private async Task MarkVerifiedAsync(string modelId, bool verified, CancellationToken cancellationToken)
    {
        var record = await _repository.FindAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        record.Verified = verified;
        record.VerifiedAtUtc = verified ? DateTime.UtcNow : null;
        await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Infrastructure helpers
    // -----------------------------------------------------------------------

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Multi-gigabyte downloads must not be bounded by a wall-clock HttpClient timeout; stalls are
        // caught by the per-read watchdog in FetchAsync instead.
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static Uri BuildFileUri(string repositoryId, string relativePath)
    {
        var encodedRepo = string.Join('/', repositoryId.Split('/').Select(Uri.EscapeDataString));
        var encodedPath = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

        return BuildUri($"https://{HuggingFaceHost}/{encodedRepo}/resolve/main/{encodedPath}");
    }

    private static Uri BuildUri(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        EnsureHttps(uri);
        return uri;
    }

    /// <summary>
    /// Model weights are executed by the inference stack, so a plaintext download is a code-execution
    /// vector. Anything that is not HTTPS is refused outright rather than warned about.
    /// </summary>
    private static void EnsureHttps(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                $"HTTPS가 아닌 주소로는 모델을 내려받지 않습니다: {uri.Scheme}://{uri.Host}");
        }
    }

    /// <summary>Guards against a catalog entry such as <c>../../evil.dll</c> escaping the model folder.</summary>
    private static string ResolveInside(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootFull = Path.GetFullPath(root);

        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelDownloadException(
                ErrorCodes.ModelDownloadFailed,
                $"모델 파일 경로가 올바르지 않습니다: {relativePath}");
        }

        return full;
    }

    private static string ToLocalRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexStringLower(hash);
    }

    private async Task<HardwareRecommendation?> TryGetRecommendationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _hardwareService.GetRecommendationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Hardware detection is best effort; the model list must still open without it.
            _logger.LogWarning(ex, "하드웨어 권장 설정을 가져오지 못했습니다.");
            return null;
        }
    }

    private static bool IsRecommended(ModelDescriptor descriptor, HardwareRecommendation? recommendation)
    {
        if (recommendation is null)
        {
            return false;
        }

        return descriptor.Kind switch
        {
            ModelKind.Whisper => descriptor.Id.Equals(recommendation.WhisperModelId, StringComparison.OrdinalIgnoreCase),
            ModelKind.Translation => descriptor.Id.Equals(recommendation.TranslationModelId, StringComparison.OrdinalIgnoreCase),
            ModelKind.Llm => descriptor.Id.Equals(recommendation.LlmModelId, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
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
            _logger.LogWarning(ex, "파일을 삭제하지 못했습니다: {Path}", path);
        }
    }

    /// <summary>
    /// Turns per-file byte counts into one model-wide percentage. The UI shows a single bar for a
    /// model, so a per-file progress report would make it jump back to zero four times.
    /// </summary>
    private sealed class AggregateProgress(string modelId, long totalBytes, IProgress<ModelDownloadProgress>? sink)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _completedBytes;
        private long _currentFileBytes;
        private long _lastReportedBytes;
        private TimeSpan _lastReportAt = TimeSpan.Zero;
        private string? _currentFile;

        public string ModelId { get; } = modelId;

        public double Percent => totalBytes <= 0
            ? 0d
            : Math.Clamp((_completedBytes + _currentFileBytes) * 100d / totalBytes, 0d, 100d);

        public void AdvanceTo(long bytesInCurrentFile, string currentFile)
        {
            _currentFileBytes = bytesInCurrentFile;
            _currentFile = currentFile;
            Report(force: false);
        }

        public void CompleteFile(long sizeBytes)
        {
            _completedBytes += sizeBytes;
            _currentFileBytes = 0;
            Report(force: true);
        }

        public void ReportFinished()
        {
            _currentFileBytes = 0;
            Report(force: true);
        }

        private void Report(bool force)
        {
            if (sink is null)
            {
                return;
            }

            var elapsed = _stopwatch.Elapsed;
            var received = _completedBytes + _currentFileBytes;

            // Throttle: a 1 MB buffer on a fast link fires this hundreds of times a second and the UI
            // cannot use any of it.
            if (!force && elapsed - _lastReportAt < TimeSpan.FromMilliseconds(200))
            {
                return;
            }

            var window = (elapsed - _lastReportAt).TotalSeconds;
            var speed = window > 0.05d ? Math.Max(0d, (received - _lastReportedBytes) / window) : 0d;

            _lastReportAt = elapsed;
            _lastReportedBytes = received;

            sink.Report(new ModelDownloadProgress
            {
                ModelId = ModelId,
                ReceivedBytes = received,
                TotalBytes = totalBytes,
                CurrentFile = _currentFile,
                SpeedBytesPerSecond = speed
            });
        }
    }
}

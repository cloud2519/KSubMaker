using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Media;
using KSubMaker.Domain.Subtitles;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Application.Services;

public sealed record ScanRequest
{
    public required string RootFolder { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public bool IncludeHiddenFolders { get; init; }

    /// <summary>Null uses <see cref="VideoExtensions.Default"/>.</summary>
    public IReadOnlySet<string>? Extensions { get; init; }

    /// <summary>Hard stop so a pathological tree cannot spin forever.</summary>
    public int MaxDepth { get; init; } = 64;
}

public sealed record ScanReport
{
    public required IReadOnlyList<VideoFile> Files { get; init; }
    public int DirectoriesVisited { get; init; }
    public int SkippedHidden { get; init; }
    public int SkippedCycles { get; init; }
    public int InaccessibleDirectories { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Walks a folder tree collecting video files.
///
/// Two things this must never do: follow a symlink loop forever, and blow up because one
/// subdirectory denies access. Both are handled explicitly — the walk is iterative (no recursion, so
/// no stack overflow on deep trees) and every directory read is individually guarded.
/// </summary>
public sealed class VideoScanService(IFileSystem fileSystem, ILogger<VideoScanService> logger)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<VideoScanService> _logger = logger;

    public Task<ScanReport> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(request, cancellationToken), cancellationToken);

    public ScanReport Scan(ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = DateTime.UtcNow;
        var extensions = request.Extensions ?? VideoExtensions.Default;
        var results = new List<VideoFile>();

        var visitedRealPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        var directoriesVisited = 0;
        var skippedHidden = 0;
        var skippedCycles = 0;
        var inaccessible = 0;

        if (!_fileSystem.DirectoryExists(request.RootFolder))
        {
            _logger.LogWarning("검색할 폴더가 존재하지 않습니다: {Folder}", request.RootFolder);
            return new ScanReport { Files = [], Elapsed = DateTime.UtcNow - started };
        }

        queue.Enqueue((request.RootFolder, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();

            // Cycle guard. A junction pointing at an ancestor resolves to a path we have already
            // walked, so identity is checked on the *resolved* path, not the literal one.
            string realPath;
            try
            {
                realPath = _fileSystem.GetRealPath(current);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "실제 경로를 확인하지 못해 건너뜁니다: {Path}", current);
                inaccessible++;
                continue;
            }

            if (!visitedRealPaths.Add(realPath))
            {
                skippedCycles++;
                _logger.LogDebug("이미 방문한 경로라 건너뜁니다(순환 가능성): {Path}", current);
                continue;
            }

            directoriesVisited++;

            // ---- files in this directory -----------------------------------
            try
            {
                foreach (var file in _fileSystem.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!VideoExtensions.IsVideo(file, extensions))
                    {
                        continue;
                    }

                    if (!request.IncludeHiddenFolders && _fileSystem.IsHidden(file))
                    {
                        skippedHidden++;
                        continue;
                    }

                    var video = BuildVideoFile(file);
                    if (video is not null)
                    {
                        results.Add(video);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                inaccessible++;
                _logger.LogDebug(ex, "폴더의 파일 목록을 읽지 못했습니다: {Path}", current);
            }

            // ---- subdirectories ---------------------------------------------
            if (!request.IncludeSubfolders || depth >= request.MaxDepth)
            {
                continue;
            }

            try
            {
                foreach (var directory in _fileSystem.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!request.IncludeHiddenFolders && _fileSystem.IsHidden(directory))
                    {
                        skippedHidden++;
                        continue;
                    }

                    // Reparse points are still walked, but only after the cycle guard has had a
                    // chance to reject them, which is why they are enqueued rather than skipped.
                    if (_fileSystem.IsReparsePoint(directory))
                    {
                        _logger.LogDebug("링크된 폴더를 확인합니다: {Path}", directory);
                    }

                    queue.Enqueue((directory, depth + 1));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                inaccessible++;
                _logger.LogDebug(ex, "하위 폴더 목록을 읽지 못했습니다: {Path}", current);
            }
        }

        var ordered = results
            .OrderBy(f => Path.GetDirectoryName(f.FullPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _logger.LogInformation(
            "폴더 검색 완료. 폴더 {Directories}개, 영상 {Files}개, 순환 {Cycles}건, 접근 실패 {Denied}건",
            directoriesVisited, ordered.Length, skippedCycles, inaccessible);

        return new ScanReport
        {
            Files = ordered,
            DirectoriesVisited = directoriesVisited,
            SkippedHidden = skippedHidden,
            SkippedCycles = skippedCycles,
            InaccessibleDirectories = inaccessible,
            Elapsed = DateTime.UtcNow - started
        };
    }

    private VideoFile? BuildVideoFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(path);

            var sidecars = new List<string>();
            var hasKorean = false;

            // A sidecar is any subtitle file whose name starts with the video's base name:
            // "movie.srt", "movie.ko.srt", "movie.en.forced.srt" all count.
            try
            {
                foreach (var candidate in _fileSystem.EnumerateFiles(directory))
                {
                    var candidateName = Path.GetFileName(candidate);
                    if (!VideoExtensions.Subtitle.Contains(Path.GetExtension(candidate)))
                    {
                        continue;
                    }

                    if (!candidateName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    sidecars.Add(candidate);

                    if (OutputPathResolver.LooksKorean(candidate))
                    {
                        hasKorean = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "동일 이름 자막 파일을 확인하지 못했습니다: {Path}", path);
            }

            return new VideoFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                Extension = Path.GetExtension(path),
                SizeBytes = _fileSystem.GetFileSize(path),
                LastWriteTimeUtc = _fileSystem.GetLastWriteTimeUtc(path),
                ExternalSubtitlePaths = sidecars,
                HasKoreanExternalSubtitle = hasKorean
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "파일 정보를 읽지 못해 건너뜁니다: {Path}", path);
            return null;
        }
    }
}

using System.Text;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Subtitles;

/// <summary>
/// Writes the finished Korean SRT next to the source video.
///
/// This is the last step of a job that may have taken an hour, and it writes into the user's own
/// media folder, so it is deliberately paranoid: the conflict policy is applied first, free space is
/// checked before a single byte is written, and the real file only appears via a move from a temp
/// file in the same directory. A failure therefore never destroys an existing subtitle.
/// </summary>
public sealed class AtomicSubtitleWriter(IFileSystem fileSystem, ILogger<AtomicSubtitleWriter> logger) : ISubtitleWriter
{
    /// <summary>
    /// Below this the write is refused up front. An SRT is tiny, but a volume this full will also
    /// fail the temp write halfway through, and a clear Korean message beats a raw disk-full error.
    /// </summary>
    private const long MinimumFreeBytes = 50L * 1024 * 1024;

    /// <summary>
    /// UTF-8 *with* BOM. Many Windows players (and PotPlayer/GOM in particular) fall back to the
    /// system ANSI code page for a BOM-less file, which renders Korean as mojibake.
    /// </summary>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<AtomicSubtitleWriter> _logger = logger;

    public async Task<string?> WriteAsync(
        IReadOnlyList<SubtitleCue> cues,
        string desiredPath,
        OutputConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);

        cancellationToken.ThrowIfCancellationRequested();

        var resolution = OutputPathResolver.Resolve(desiredPath, conflictPolicy, _fileSystem.FileExists);

        if (!resolution.ShouldWrite)
        {
            _logger.LogInformation("자막 파일을 저장하지 않았습니다: {Reason} ({Path})",
                resolution.Reason ?? "정책에 따라 건너뜁니다.", resolution.Path);
            return null;
        }

        if (resolution.WasRenamed)
        {
            _logger.LogInformation("{Reason} 새 경로: {Path}", resolution.Reason, resolution.Path);
        }

        var targetPath = resolution.Path;
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"자막을 저장할 폴더를 확인할 수 없습니다: {targetPath}");
        }

        _fileSystem.CreateDirectory(directory);
        EnsureFreeSpace(directory);

        var content = SrtFormatter.ToWindowsLineEndings(SrtFormatter.Write(cues));

        // The temp file lives in the *same* directory so the final step is a rename within one
        // volume: a cross-volume File.Move degrades to copy+delete and stops being atomic.
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():n}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, Utf8WithBom))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _fileSystem.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        _logger.LogInformation("자막 파일을 저장했습니다: {Path} (자막 {Count}개)", targetPath, cues.Count);
        return targetPath;
    }

    private void EnsureFreeSpace(string directory)
    {
        var free = _fileSystem.GetAvailableFreeSpace(directory);

        // IFileSystem cannot express "unknown", so a non-positive answer means the volume could not
        // be queried (network share, removed drive letter). Refusing to write on that basis would be
        // worse than letting the real write report the real error, so it only warns.
        if (free <= 0)
        {
            _logger.LogWarning("여유 디스크 공간을 확인하지 못했습니다: {Directory}", directory);
            return;
        }

        if (free < MinimumFreeBytes)
        {
            var freeMb = free / 1024d / 1024d;
            throw new IOException(
                $"디스크 여유 공간이 부족하여 자막을 저장할 수 없습니다. " +
                $"현재 {freeMb:0.#}MB, 최소 {MinimumFreeBytes / 1024 / 1024}MB가 필요합니다.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "임시 자막 파일을 삭제하지 못했습니다: {Path}", path);
        }
    }
}

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.Services;

/// <summary>
/// <see cref="Process"/>-based implementation of <see cref="IShellService"/>.
///
/// Paths reaching this class come from the file system scanner and from the queue, so they are not
/// attacker controlled — but they are still validated and quoted before being handed to
/// <c>explorer.exe</c>. A file name containing a quote character would otherwise let the rest of the
/// name be parsed as further Explorer arguments.
/// </summary>
public sealed class ShellService(ILogger<ShellService> logger) : IShellService
{
    private readonly ILogger<ShellService> _logger = logger;

    public bool OpenFolder(string? folderPath)
    {
        if (!TryNormalize(folderPath, out var full))
        {
            return false;
        }

        if (!Directory.Exists(full))
        {
            _logger.LogDebug("열려는 폴더가 존재하지 않습니다: {Path}", full);
            return false;
        }

        return Launch(new ProcessStartInfo
        {
            FileName = full,
            UseShellExecute = true
        });
    }

    public bool RevealFile(string? filePath)
    {
        if (!TryNormalize(filePath, out var full))
        {
            return false;
        }

        if (!File.Exists(full))
        {
            _logger.LogDebug("선택하려는 파일이 존재하지 않습니다: {Path}", full);
            return false;
        }

        // explorer.exe wants exactly "/select,<path>" with no space after the comma.
        return Launch(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{full}\"",
            UseShellExecute = true
        });
    }

    public bool RevealOrOpenParent(string? path)
    {
        if (!TryNormalize(path, out var full))
        {
            return false;
        }

        if (File.Exists(full))
        {
            return RevealFile(full);
        }

        if (Directory.Exists(full))
        {
            return OpenFolder(full);
        }

        var parent = Path.GetDirectoryName(full);
        return !string.IsNullOrEmpty(parent) && OpenFolder(parent);
    }

    /// <summary>
    /// Rejects empty paths, relative paths and anything containing a quote or a newline, then
    /// canonicalises what is left.
    /// </summary>
    private bool TryNormalize(string? path, out string full)
    {
        full = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            _logger.LogWarning("경로에 사용할 수 없는 문자가 있어 열지 않았습니다.");
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(path.Trim());

            if (!Path.IsPathRooted(candidate))
            {
                return false;
            }

            full = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "경로를 해석하지 못했습니다: {Path}", path);
            return false;
        }
    }

    private bool Launch(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "탐색기를 실행하지 못했습니다: {Target}", info.FileName);
            return false;
        }
    }
}

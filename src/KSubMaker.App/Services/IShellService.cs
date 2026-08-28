namespace KSubMaker.App.Services;

/// <summary>
/// Hands a path to Windows Explorer. Every method returns false instead of throwing, because
/// "결과 폴더 열기" failing is a status-bar message, never a crash.
/// </summary>
public interface IShellService
{
    /// <summary>Opens <paramref name="folderPath"/> in a new Explorer window.</summary>
    bool OpenFolder(string? folderPath);

    /// <summary>Opens <paramref name="filePath"/> with the OS default application (plays a video).</summary>
    bool OpenFile(string? filePath);

    /// <summary>Opens the containing folder with <paramref name="filePath"/> selected.</summary>
    bool RevealFile(string? filePath);

    /// <summary>
    /// Opens the folder that contains <paramref name="path"/>, selecting it when the file exists and
    /// falling back to the plain folder when it does not.
    /// </summary>
    bool RevealOrOpenParent(string? path);
}

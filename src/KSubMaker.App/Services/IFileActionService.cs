namespace KSubMaker.App.Services;

/// <summary>
/// File-manager style operations invoked from the grid's right-click menu — rename, delete to the
/// Recycle Bin, and the Windows properties sheet. Every method returns a result instead of throwing;
/// a failed 이름 바꾸기 is a status-bar message, never a crash.
/// </summary>
public interface IFileActionService
{
    /// <summary>
    /// Renames <paramref name="path"/> to <paramref name="newFileName"/> within the same folder.
    /// Returns the new full path on success, or null (and logs) on any failure — a bad name, a
    /// target that already exists, a locked file.
    /// </summary>
    string? Rename(string path, string newFileName);

    /// <summary>
    /// Sends the given files to the Recycle Bin in one shell operation (so it is a single undo).
    /// Missing files are skipped. Returns true when the operation completed without an error.
    /// </summary>
    bool RecycleFiles(IReadOnlyList<string> paths);

    /// <summary>Opens the Windows file properties dialog for <paramref name="path"/>.</summary>
    bool ShowProperties(string path);
}

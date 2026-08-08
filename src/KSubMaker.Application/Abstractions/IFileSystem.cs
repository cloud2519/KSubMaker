namespace KSubMaker.Application.Abstractions;

/// <summary>Minimal file-system surface used by the scanner, so recursion can be unit tested.</summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);

    /// <summary>Immediate subdirectories. Implementations must not throw on access denial.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Immediate files.</summary>
    IEnumerable<string> EnumerateFiles(string path);

    bool IsHidden(string path);

    /// <summary>True when the entry is a symbolic link / junction / mount point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Canonical identity used for cycle detection. On Windows this is the resolved full path with
    /// links followed; a stable string is all the scanner needs.
    /// </summary>
    string GetRealPath(string path);

    long GetFileSize(string path);
    DateTime GetLastWriteTimeUtc(string path);

    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void Move(string source, string destination, bool overwrite);
    void Delete(string path);
    void CreateDirectory(string path);

    /// <summary>Free bytes on the volume that contains <paramref name="path"/>.</summary>
    long GetAvailableFreeSpace(string path);
}

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>
/// A unique temporary directory that deletes itself on dispose. Every test that touches the real file
/// system owns one of these, so nothing leaks between tests and nothing depends on ordering.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string label = "ksubmaker")
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            $"{label}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():n}");

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string CreateSubdirectory(string name)
    {
        var path = Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteText(string relativePath, string content)
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file handle may still be closing; retry a couple of times and then give up
                // quietly — a leftover temp directory must never fail a test.
                Thread.Yield();
            }
        }
    }
}

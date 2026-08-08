using System.Text.Json;

namespace KSubMaker.UnitTests.Fakes;

/// <summary>One entry of a Hugging Face <c>tree/main</c> response.</summary>
public sealed record HubEntry(string Path, long Size);

/// <summary>
/// Reads the verbatim hub listings checked in under <c>tests/fixtures/huggingface/</c>.
///
/// These are what make the file-selection rule testable with no network at all: the captures are the
/// real API responses, so a test against them exercises the same shape production parses. The online
/// counterpart (<c>ModelCatalogHubTests</c>) exists to catch drift between the captures and reality.
/// </summary>
public static class HuggingFaceListings
{
    /// <summary>The .csproj copies the captures next to the test assembly, so cwd never matters.</summary>
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "HuggingFaceListings");

    public static bool Exists(string repositoryId) => File.Exists(PathFor(repositoryId));

    public static IReadOnlyList<HubEntry> Entries(string repositoryId)
    {
        var path = PathFor(repositoryId);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{repositoryId}' 저장소의 캡처가 없습니다. tests/fixtures/huggingface/의 README를 참고해 추가하세요.",
                path);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var entries = new List<HubEntry>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "file";
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entryPath = element.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                continue;
            }

            var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                ? parsed
                : 0L;

            entries.Add(new HubEntry(entryPath, size));
        }

        return entries;
    }

    public static IReadOnlyList<string> Paths(string repositoryId) =>
        Entries(repositoryId).Select(entry => entry.Path).ToList();

    public static long TotalBytes(string repositoryId, IEnumerable<string> files)
    {
        var sizes = Entries(repositoryId).ToDictionary(entry => entry.Path, entry => entry.Size, StringComparer.Ordinal);
        return files.Sum(file => sizes.TryGetValue(file, out var size) ? size : 0L);
    }

    /// <summary>"owner/name" is stored as "owner__name.json"; "/" is not a legal file name character.</summary>
    private static string PathFor(string repositoryId) =>
        Path.Combine(Root, repositoryId.Replace("/", "__", StringComparison.Ordinal) + ".json");
}

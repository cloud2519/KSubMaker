using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace KSubMaker.IntegrationTests.Models;

/// <summary>One file in a repository, as the hub reports it.</summary>
public sealed record HubFile(string Path, long Size);

/// <summary>A <c>tree/main</c> response, or the status code that came back instead.</summary>
public sealed record HubListing(HttpStatusCode StatusCode, IReadOnlyList<HubFile> Files)
{
    public bool Ok => StatusCode == HttpStatusCode.OK;

    public IReadOnlyList<string> Paths => Files.Select(file => file.Path).ToList();

    public long TotalBytes(IEnumerable<string> selected)
    {
        var sizes = Files.ToDictionary(file => file.Path, file => file.Size, StringComparer.Ordinal);
        return selected.Sum(file => sizes.TryGetValue(file, out var size) ? size : 0L);
    }
}

/// <summary>
/// Fetches repository listings once per repository and keeps them for the lifetime of the test class.
///
/// <para>xunit creates a class fixture once, so nine theory cases cost nine HTTP calls, not
/// nine-times-the-number-of-assertions. Hammering the hub from a test suite is how a CI job earns a
/// rate limit.</para>
/// </summary>
public sealed class HuggingFaceTreeCache : IDisposable
{
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly ConcurrentDictionary<string, Task<HubListing>> _listings =
        new(StringComparer.OrdinalIgnoreCase);

    public HuggingFaceTreeCache()
    {
        // Some Hugging Face edge nodes reject requests without a User-Agent, exactly as the real
        // downloader's named client works around.
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("KSubMaker-Tests/0.1 (+https://github.com/ksubmaker)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Task<HubListing> GetAsync(string repositoryId) =>
        _listings.GetOrAdd(repositoryId, FetchAsync);

    private async Task<HubListing> FetchAsync(string repositoryId)
    {
        var url = $"https://huggingface.co/api/models/{repositoryId}/tree/main?recursive=1";

        using var response = await _client.GetAsync(url).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new HubListing(response.StatusCode, []);
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        var files = new List<HubFile>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "file";
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = element.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                ? parsed
                : 0L;

            files.Add(new HubFile(path, size));
        }

        return new HubListing(response.StatusCode, files);
    }

    public void Dispose() => _client.Dispose();
}

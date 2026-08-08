using System.Text.RegularExpressions;

namespace KSubMaker.Domain.Models;

/// <summary>
/// Turns a Hugging Face repository listing into the exact set of files one model is made of.
///
/// <para><b>Why this exists.</b> The catalog used to hardcode the file list per model and the app only
/// found out it was wrong when a user pressed "다운로드" and got a 404 halfway through
/// (<c>vocabulary.txt</c> on <c>faster-whisper-large-v3</c>, which publishes <c>vocabulary.json</c>).
/// The downloader already fetches the repository tree for SHA-256 digests, so the real file list is
/// one <c>.Keys</c> away — asking the hub is strictly better than a list a human typed once.</para>
///
/// <para>The rule is a pair of regexes rather than "download everything" because GGUF repositories
/// publish a dozen quantisations of the same model and we want exactly one of them — including all of
/// its shards when llama.cpp split it in two.</para>
/// </summary>
public static class ModelFileSelector
{
    /// <summary>Include pattern for a repository that contains exactly one model (the CTranslate2 conversions).</summary>
    public const string AnyFile = "^.+$";

    /// <summary>A CTranslate2 model is unusable without its weights.</summary>
    public const string Ct2EssentialFile = @"^model\.bin$";

    /// <summary>A llama.cpp model is unusable without at least one GGUF shard.</summary>
    public const string GgufEssentialFile = @"\.gguf$";

    /// <summary>
    /// Repository furniture that is never part of a model: dotfiles (<c>.gitattributes</c>,
    /// <c>.gitignore</c>), any licence file, and any markdown file (which covers <c>README.md</c> and
    /// <c>LICENSE.model.md</c>). Downloading these would waste bandwidth and, worse, would make the
    /// "model directory" contain files the inference stack has to ignore.
    /// </summary>
    public const string DefaultExcludePattern = @"^(?:.*/)?(?:\.[^/]+|licen[sc]e[^/]*|[^/]*\.md)$";

    /// <summary>The patterns are ours, but an unbounded match is never worth the risk.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// The repository-relative paths that make up <paramref name="descriptor"/>, in ordinal order.
    ///
    /// <para>Ordinal order is not cosmetic: it is what puts <c>…-00001-of-00002.gguf</c> before
    /// <c>…-00002-of-00002.gguf</c>, and <see cref="EntryPointFile"/> depends on that.</para>
    /// </summary>
    public static IReadOnlyList<string> Select(ModelDescriptor descriptor, IEnumerable<string> repositoryPaths)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        return repositoryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => Matches(descriptor.IncludePattern, path) && !Matches(descriptor.ExcludePattern, path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A Korean explanation of why <paramref name="selected"/> cannot be downloaded, or <c>null</c> when
    /// it can. Downloading nothing and reporting success is the one outcome that must be impossible:
    /// the user would get "설치됨" for an empty folder and a model-load failure on the next job.
    /// </summary>
    public static string? DescribeProblem(ModelDescriptor descriptor, IReadOnlyList<string> selected)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(selected);

        if (selected.Count == 0)
        {
            return $"저장소에서 내려받을 파일을 찾지 못했습니다: {descriptor.RepositoryId}. " +
                   "저장소 주소가 바뀌었거나 파일 선택 규칙이 더 이상 맞지 않습니다.";
        }

        if (!selected.Any(file => Matches(descriptor.EssentialFilePattern, file)))
        {
            return $"저장소에 모델 본체 파일이 없습니다: {descriptor.RepositoryId}. " +
                   $"필수 파일 규칙 '{descriptor.EssentialFilePattern}'과(와) 일치하는 파일이 하나도 없습니다.";
        }

        return null;
    }

    /// <summary>
    /// The single path handed to the inference engine for a <see cref="ModelPayloadLayout.EntryPointFile"/>
    /// model.
    ///
    /// <para>llama.cpp is given the <b>first</b> shard and opens the remaining ones itself, so handing it
    /// <c>…-00002-of-00002.gguf</c> fails with a confusing "invalid magic" error. Picking the first
    /// ordinal match of the essential pattern is what makes that impossible.</para>
    /// </summary>
    public static string EntryPointFile(ModelDescriptor descriptor, IReadOnlyList<string> selected)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(selected);

        foreach (var file in selected.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Matches(descriptor.EssentialFilePattern, file))
            {
                return file;
            }
        }

        throw new InvalidOperationException(
            $"모델 본체 파일을 찾지 못했습니다: {descriptor.RepositoryId}");
    }

    private static bool Matches(string pattern, string value) =>
        Regex.IsMatch(value, pattern, Options, MatchTimeout);
}

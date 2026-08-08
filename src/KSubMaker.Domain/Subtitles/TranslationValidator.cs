namespace KSubMaker.Domain.Subtitles;

public sealed record TranslationValidationResult
{
    public required bool IsValid { get; init; }

    /// <summary>Ids that were requested but not returned.</summary>
    public IReadOnlyList<int> MissingIds { get; init; } = [];

    /// <summary>Ids returned more than once.</summary>
    public IReadOnlyList<int> DuplicateIds { get; init; } = [];

    /// <summary>Ids returned that were never requested.</summary>
    public IReadOnlyList<int> UnexpectedIds { get; init; } = [];

    /// <summary>Ids whose translation was blank.</summary>
    public IReadOnlyList<int> EmptyIds { get; init; } = [];

    /// <summary>
    /// True when the response broke the id contract itself rather than merely failing to translate
    /// something: an id nobody asked for, the same id twice, or (on the Python side, where ids
    /// arrive as JSON) an id that could not be parsed at all.
    ///
    /// <para>This is the distinction that decides whether a batch degrades or fails. A blank
    /// translation is a quirky line; a response whose ids do not line up is an engine that is not
    /// answering the question, and shipping its output would put the wrong Korean under the wrong
    /// timecode.</para>
    /// </summary>
    public bool IsCorrupt => UnexpectedIds.Count > 0 || DuplicateIds.Count > 0;

    public string Describe()
    {
        if (IsValid)
        {
            return "정상";
        }

        var parts = new List<string>();

        if (MissingIds.Count > 0)
        {
            parts.Add($"누락 {MissingIds.Count}건({string.Join(",", MissingIds.Take(5))}…)");
        }

        if (DuplicateIds.Count > 0)
        {
            parts.Add($"중복 {DuplicateIds.Count}건");
        }

        if (UnexpectedIds.Count > 0)
        {
            parts.Add($"알 수 없는 id {UnexpectedIds.Count}건");
        }

        if (EmptyIds.Count > 0)
        {
            parts.Add($"빈 번역 {EmptyIds.Count}건");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Guards the boundary where a language model's output re-enters the pipeline.
///
/// A translation engine is allowed to reorder items, but it may not invent, drop, duplicate or blank
/// them. Anything else and the batch is retried for the ids that came back unusable — silently
/// accepting a short response would drop subtitles from the finished file, which is the worst
/// possible failure here because it is invisible.
///
/// <para>What happens when the retries run out is a judgement call, and this class holds the rule:
/// a handful of stubbornly blank lines is a quirk to degrade around (keep the source text, finish
/// the job), while a batch that came back mostly blank is a broken engine and must fail loudly. See
/// <see cref="IsMostlyUntranslated"/>.</para>
/// </summary>
public static class TranslationValidator
{
    /// <summary>
    /// Fraction of a batch that has to come back unusable before the response is treated as a broken
    /// engine rather than a few untranslatable lines.
    ///
    /// <para><b>Half, and why.</b> After <see cref="TranslatableText"/> has removed the cues that
    /// contain no words at all, what is left is real dialogue, and a working engine translates
    /// essentially all of it — the field report that prompted this was 1 blank cue in 30, twice, in
    /// two whole films. There is no plausible content-shaped reason for half a batch of ordinary
    /// dialogue to come back empty; that pattern means the wrong source-language code, a model that
    /// never finished loading, or an LLM that has stopped following the output format. Degrading
    /// there would ship a subtitle file that is half untranslated source text, which is worse for
    /// the user than an error they can act on. Below the threshold the opposite is true: failing the
    /// job discards every cue that <i>did</i> translate.</para>
    /// </summary>
    public const double MostlyUntranslatedRatio = 0.5d;

    /// <summary>
    /// Floor on the absolute number of unusable cues, so the ratio cannot fire on a tiny batch.
    ///
    /// <para>The CUDA-OOM ladder halves batches repeatedly and can hand the engine a single segment;
    /// one blank cue out of one is 100% and would otherwise look like total failure when it is
    /// exactly the ordinary case this whole change exists to survive.</para>
    /// </summary>
    public const int MostlyUntranslatedMinimumCues = 4;

    /// <summary>
    /// True when <paramref name="unusableCount"/> of <paramref name="requestedCount"/> cues is
    /// enough to call the response broken rather than merely incomplete. Both
    /// <see cref="MostlyUntranslatedRatio"/> and <see cref="MostlyUntranslatedMinimumCues"/> must be
    /// met.
    /// </summary>
    public static bool IsMostlyUntranslated(int unusableCount, int requestedCount)
    {
        if (requestedCount <= 0 || unusableCount < MostlyUntranslatedMinimumCues)
        {
            return false;
        }

        return unusableCount >= requestedCount * MostlyUntranslatedRatio;
    }

    public static TranslationValidationResult Validate(
        IReadOnlyList<SubtitleItem> requested,
        IReadOnlyList<TranslatedSubtitleItem> returned)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(returned);

        var requestedIds = requested.Select(i => i.Id).ToHashSet();
        var seen = new HashSet<int>();
        var duplicates = new List<int>();
        var unexpected = new List<int>();
        var empty = new List<int>();

        foreach (var item in returned)
        {
            if (!seen.Add(item.Id))
            {
                duplicates.Add(item.Id);
                continue;
            }

            if (!requestedIds.Contains(item.Id))
            {
                unexpected.Add(item.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Translation))
            {
                empty.Add(item.Id);
            }
        }

        var missing = requestedIds.Where(id => !seen.Contains(id)).OrderBy(id => id).ToArray();

        return new TranslationValidationResult
        {
            IsValid = missing.Length == 0 && duplicates.Count == 0 && unexpected.Count == 0 && empty.Count == 0,
            MissingIds = missing,
            DuplicateIds = duplicates,
            UnexpectedIds = unexpected,
            EmptyIds = empty
        };
    }

    /// <summary>
    /// Rejoins a validated response to the requested order, keyed by id.
    /// Ids missing from <paramref name="returned"/> simply do not appear in the result.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ToMap(IReadOnlyList<TranslatedSubtitleItem> returned)
    {
        var map = new Dictionary<int, string>();

        foreach (var item in returned)
        {
            if (!string.IsNullOrWhiteSpace(item.Translation))
            {
                map[item.Id] = item.Translation.Trim();
            }
        }

        return map;
    }
}

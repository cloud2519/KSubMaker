namespace KSubMaker.Domain.Settings;

/// <summary>Why a 고유명사 사전 entry was rejected. <see cref="Ok"/> means it may be added.</summary>
public enum GlossaryValidation
{
    Ok,

    /// <summary>Blank source term. It would match every string in the transcript.</summary>
    SourceRequired,

    /// <summary>Blank Korean rendering. It would delete the term from the output.</summary>
    TargetRequired,

    /// <summary>The source term is already in the table (compared case-insensitively).</summary>
    DuplicateSource
}

/// <summary>
/// The rules the 고유명사 사전 editor enforces, kept out of the view model so they can be tested
/// without WPF.
///
/// Case-insensitive throughout, because <see cref="AppSettings.Glossary"/> itself is built with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>: two entries differing only in case would collapse
/// into one on save and silently discard whichever the user typed second.
/// </summary>
public static class GlossaryRules
{
    public static GlossaryValidation Validate(
        string? source,
        string? target,
        IEnumerable<string> existingSources)
    {
        ArgumentNullException.ThrowIfNull(existingSources);

        var trimmedSource = source?.Trim() ?? string.Empty;
        if (trimmedSource.Length == 0)
        {
            return GlossaryValidation.SourceRequired;
        }

        if ((target?.Trim() ?? string.Empty).Length == 0)
        {
            return GlossaryValidation.TargetRequired;
        }

        foreach (var existing in existingSources)
        {
            if ((existing?.Trim() ?? string.Empty).Equals(trimmedSource, StringComparison.OrdinalIgnoreCase))
            {
                return GlossaryValidation.DuplicateSource;
            }
        }

        return GlossaryValidation.Ok;
    }

    /// <summary>
    /// Projects edited rows onto the persisted dictionary.
    ///
    /// Rows that were edited to blank in the grid are dropped rather than saved, and a duplicate that
    /// only appeared through in-place editing keeps the first occurrence — the same rule the 추가
    /// button applies up front, so the grid and the saved file never disagree.
    /// </summary>
    public static Dictionary<string, string> Build(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (source, target) in entries)
        {
            var trimmedSource = source?.Trim() ?? string.Empty;
            var trimmedTarget = target?.Trim() ?? string.Empty;

            if (trimmedSource.Length == 0 || trimmedTarget.Length == 0)
            {
                continue;
            }

            glossary.TryAdd(trimmedSource, trimmedTarget);
        }

        return glossary;
    }
}

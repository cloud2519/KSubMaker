using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Subtitles;

public sealed record SubtitleFormattingOptions
{
    public int MaxLinesPerCue { get; init; } = 2;
    public int MaxCharsPerLine { get; init; } = 22;
    public double MinCueDurationSeconds { get; init; } = 1.0;
    public double MaxCueDurationSeconds { get; init; } = 7.0;
    public int MinCueGapMilliseconds { get; init; } = 50;
    public bool MergeShortCues { get; init; } = true;

    public double MinGapSeconds => MinCueGapMilliseconds / 1000d;
    public int MaxCharsPerCue => MaxLinesPerCue * MaxCharsPerLine;

    public static SubtitleFormattingOptions From(AppSettings settings) => new()
    {
        MaxLinesPerCue = Math.Max(1, settings.MaxLinesPerCue),
        MaxCharsPerLine = Math.Max(8, settings.MaxCharsPerLine),
        MinCueDurationSeconds = Math.Max(0.1, settings.MinCueDurationSeconds),
        MaxCueDurationSeconds = Math.Max(settings.MinCueDurationSeconds, settings.MaxCueDurationSeconds),
        MinCueGapMilliseconds = Math.Max(0, settings.MinCueGapMilliseconds),
        MergeShortCues = settings.MergeShortCues
    };
}

/// <summary>
/// Turns translated text plus the original timings into display-ready cues.
///
/// Invariant that drives the whole design: <b>translation never moves a timecode</b>. Timings come
/// only from the ASR segments; this class may merge, split or nudge them for readability, but the
/// numbers always originate from the audio, never from a language model.
/// </summary>
public static class SubtitlePostProcessor
{
    /// <summary>
    /// Joins ASR segments to their translations by id, then applies merging, line breaking and
    /// timing hygiene. Segments with no translation are dropped rather than emitted untranslated.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Build(
        IReadOnlyList<TranscriptionSegment> segments,
        IReadOnlyDictionary<int, string> translations,
        SubtitleFormattingOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(options);

        var draft = new List<DraftCue>();

        foreach (var segment in segments.OrderBy(s => s.Start).ThenBy(s => s.Id))
        {
            if (!translations.TryGetValue(segment.Id, out var text))
            {
                continue;
            }

            var normalized = KoreanLineBreaker.Normalize(text);
            if (normalized.Length == 0)
            {
                continue;
            }

            var start = Math.Max(0d, segment.Start);
            var end = Math.Max(start + 0.001d, segment.End);
            draft.Add(new DraftCue(start, end, normalized));
        }

        if (draft.Count == 0)
        {
            return [];
        }

        if (options.MergeShortCues)
        {
            draft = MergeShort(draft, options);
        }

        draft = SplitLong(draft, options);
        draft = FixTimings(draft, options);

        var cues = new List<SubtitleCue>(draft.Count);
        var index = 1;

        foreach (var item in draft)
        {
            var lines = KoreanLineBreaker.Break(item.Text, options.MaxLinesPerCue, options.MaxCharsPerLine);
            if (lines.Count == 0)
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                Index = index++,
                Start = item.Start,
                End = item.End,
                Lines = lines
            });
        }

        return cues;
    }

    /// <summary>
    /// Merges a cue into its successor when it is too short to read and the two are adjacent.
    /// Never merges across a pause longer than one second — that is almost always a scene change.
    /// </summary>
    private static List<DraftCue> MergeShort(List<DraftCue> input, SubtitleFormattingOptions options)
    {
        const double MaxMergeGapSeconds = 1.0;
        var result = new List<DraftCue>(input.Count);
        var i = 0;

        while (i < input.Count)
        {
            var current = input[i];

            while (i + 1 < input.Count)
            {
                var next = input[i + 1];
                var gap = next.Start - current.End;
                var mergedDuration = next.End - current.Start;
                var mergedLength = current.Text.Length + 1 + next.Text.Length;

                var currentTooShort = current.Duration < options.MinCueDurationSeconds || current.Text.Length <= 4;

                if (!currentTooShort ||
                    gap > MaxMergeGapSeconds ||
                    gap < 0 ||
                    mergedDuration > options.MaxCueDurationSeconds ||
                    mergedLength > options.MaxCharsPerCue)
                {
                    break;
                }

                current = new DraftCue(current.Start, next.End, $"{current.Text} {next.Text}");
                i++;
            }

            result.Add(current);
            i++;
        }

        return result;
    }

    /// <summary>
    /// Splits a cue whose text cannot fit in the allowed number of lines, distributing its duration
    /// proportionally to the character count of each part. Splitting happens on sentence boundaries
    /// where possible, otherwise on the best word boundary.
    /// </summary>
    private static List<DraftCue> SplitLong(List<DraftCue> input, SubtitleFormattingOptions options)
    {
        var result = new List<DraftCue>(input.Count);

        foreach (var cue in input)
        {
            if (cue.Text.Length <= options.MaxCharsPerCue && cue.Duration <= options.MaxCueDurationSeconds)
            {
                result.Add(cue);
                continue;
            }

            var parts = SplitText(cue.Text, options.MaxCharsPerCue);
            if (parts.Count <= 1)
            {
                result.Add(cue);
                continue;
            }

            var totalChars = parts.Sum(p => p.Length);
            var cursor = cue.Start;

            for (var i = 0; i < parts.Count; i++)
            {
                var share = totalChars == 0 ? 1d / parts.Count : parts[i].Length / (double)totalChars;
                var duration = cue.Duration * share;
                var end = i == parts.Count - 1 ? cue.End : Math.Min(cue.End, cursor + duration);

                if (end <= cursor)
                {
                    end = Math.Min(cue.End, cursor + 0.001d);
                }

                result.Add(new DraftCue(cursor, end, parts[i]));
                cursor = end;
            }
        }

        return result;
    }

    private static List<string> SplitText(string text, int maxChars)
    {
        var parts = new List<string>();
        var remaining = text;

        while (remaining.Length > maxChars)
        {
            var cut = FindSentenceCut(remaining, maxChars);
            if (cut <= 0 || cut >= remaining.Length)
            {
                cut = Math.Min(maxChars, remaining.Length - 1);
            }

            parts.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            parts.Add(remaining.Trim());
        }

        return parts;
    }

    private static int FindSentenceCut(string text, int maxChars)
    {
        var limit = Math.Min(maxChars, text.Length - 1);

        for (var i = limit; i > maxChars / 2; i--)
        {
            if (text[i - 1] is '.' or '?' or '!' or '…')
            {
                return i;
            }
        }

        for (var i = limit; i > maxChars / 3; i--)
        {
            if (text[i - 1] is ',' or ';' or ':')
            {
                return i;
            }
        }

        var space = text.LastIndexOf(' ', limit);
        return space > 0 ? space + 1 : limit;
    }

    /// <summary>
    /// Enforces min/max duration, the minimum inter-cue gap and strict monotonicity.
    /// A cue is only stretched into space that is actually free.
    /// </summary>
    private static List<DraftCue> FixTimings(List<DraftCue> input, SubtitleFormattingOptions options)
    {
        var ordered = input.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        var gap = options.MinGapSeconds;

        for (var i = 0; i < ordered.Count; i++)
        {
            var cue = ordered[i];
            var start = Math.Max(0d, cue.Start);
            var end = cue.End;

            // Never start before the previous cue has finished (+ the required gap).
            if (i > 0)
            {
                var previousEnd = ordered[i - 1].End;
                if (start < previousEnd + gap)
                {
                    start = previousEnd + gap;
                }
            }

            if (end < start + 0.001d)
            {
                end = start + 0.001d;
            }

            // Grow a too-short cue, but only up to the next cue's start.
            if (end - start < options.MinCueDurationSeconds)
            {
                var desired = start + options.MinCueDurationSeconds;
                var ceiling = i + 1 < ordered.Count ? ordered[i + 1].Start - gap : double.MaxValue;
                end = Math.Max(start + 0.001d, Math.Min(desired, ceiling));
            }

            if (end - start > options.MaxCueDurationSeconds)
            {
                end = start + options.MaxCueDurationSeconds;
            }

            ordered[i] = cue with { Start = start, End = end };
        }

        return ordered;
    }

    private readonly record struct DraftCue(double Start, double End, string Text)
    {
        public double Duration => End - Start;
    }
}

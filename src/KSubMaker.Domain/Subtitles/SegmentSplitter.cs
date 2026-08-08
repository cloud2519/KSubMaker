namespace KSubMaker.Domain.Subtitles;

/// <summary>
/// Splits over-long ASR segments *before* translation, using Whisper's word timestamps so the
/// resulting pieces keep real audio-derived timings.
///
/// Doing this before translation is what makes word timestamps genuinely useful: once the text is
/// Korean there is no word-level alignment left to exploit, so any later split has to interpolate.
/// </summary>
public static class SegmentSplitter
{
    /// <summary>
    /// Splits segments that exceed <paramref name="maxChars"/> or <paramref name="maxDurationSeconds"/>.
    /// Ids are reassigned contiguously from 1 so downstream batching and validation stay simple.
    /// </summary>
    public static IReadOnlyList<TranscriptionSegment> Split(
        IReadOnlyList<TranscriptionSegment> segments,
        int maxChars = 90,
        double maxDurationSeconds = 7.0)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var output = new List<TranscriptionSegment>(segments.Count);

        foreach (var segment in segments)
        {
            var text = KoreanLineBreaker.Normalize(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var normalized = segment with { Text = text };

            if (text.Length <= maxChars && normalized.Duration <= maxDurationSeconds)
            {
                output.Add(normalized);
                continue;
            }

            if (normalized.Words.Count > 1)
            {
                output.AddRange(SplitByWords(normalized, maxChars, maxDurationSeconds));
            }
            else
            {
                output.AddRange(SplitProportionally(normalized, maxChars));
            }
        }

        return Renumber(output);
    }

    private static IEnumerable<TranscriptionSegment> SplitByWords(
        TranscriptionSegment segment,
        int maxChars,
        double maxDurationSeconds)
    {
        var chunk = new List<WordTimestamp>();
        var length = 0;

        foreach (var word in segment.Words)
        {
            var wordText = word.Word;
            var projectedLength = length + wordText.Length;
            var projectedDuration = chunk.Count == 0 ? 0d : word.End - chunk[0].Start;

            var mustFlush = chunk.Count > 0 &&
                            (projectedLength > maxChars || projectedDuration > maxDurationSeconds);

            // Prefer flushing right after sentence-ending punctuation when we are already past half
            // the budget: it produces far more natural cues than a purely length-driven cut.
            var wantsFlush = chunk.Count > 0 &&
                             length > maxChars / 2 &&
                             EndsSentence(chunk[^1].Word);

            if (mustFlush || wantsFlush)
            {
                yield return FromWords(segment, chunk);
                chunk = [];
                length = 0;
            }

            chunk.Add(word);
            length += wordText.Length;
        }

        if (chunk.Count > 0)
        {
            yield return FromWords(segment, chunk);
        }
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '.' or '?' or '!' or '…';
    }

    private static TranscriptionSegment FromWords(TranscriptionSegment source, List<WordTimestamp> words)
    {
        var text = KoreanLineBreaker.Normalize(string.Concat(words.Select(w => w.Word)));

        return new TranscriptionSegment
        {
            Id = source.Id,
            Start = words[0].Start,
            End = Math.Max(words[^1].End, words[0].Start + 0.001d),
            Text = text,
            Words = words.ToArray()
        };
    }

    /// <summary>Fallback when no word timestamps are available: cut on punctuation, interpolate time.</summary>
    private static IEnumerable<TranscriptionSegment> SplitProportionally(TranscriptionSegment segment, int maxChars)
    {
        var pieces = new List<string>();
        var remaining = segment.Text;

        while (remaining.Length > maxChars)
        {
            var cut = -1;

            for (var i = Math.Min(maxChars, remaining.Length - 1); i > maxChars / 3; i--)
            {
                if (remaining[i - 1] is '.' or '?' or '!' or '…' or ',' or ';')
                {
                    cut = i;
                    break;
                }
            }

            if (cut <= 0)
            {
                var space = remaining.LastIndexOf(' ', Math.Min(maxChars, remaining.Length - 1));
                cut = space > 0 ? space + 1 : Math.Min(maxChars, remaining.Length - 1);
            }

            pieces.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            pieces.Add(remaining.Trim());
        }

        var totalChars = pieces.Sum(p => p.Length);
        var cursor = segment.Start;

        for (var i = 0; i < pieces.Count; i++)
        {
            var share = totalChars == 0 ? 1d / pieces.Count : pieces[i].Length / (double)totalChars;
            var end = i == pieces.Count - 1 ? segment.End : Math.Min(segment.End, cursor + (segment.Duration * share));

            if (end <= cursor)
            {
                end = Math.Min(segment.End, cursor + 0.001d);
            }

            yield return new TranscriptionSegment
            {
                Id = segment.Id,
                Start = cursor,
                End = end,
                Text = pieces[i],
                Words = []
            };

            cursor = end;
        }
    }

    private static IReadOnlyList<TranscriptionSegment> Renumber(List<TranscriptionSegment> segments)
    {
        var result = new List<TranscriptionSegment>(segments.Count);
        var id = 1;

        foreach (var segment in segments)
        {
            result.Add(segment with { Id = id++ });
        }

        return result;
    }
}

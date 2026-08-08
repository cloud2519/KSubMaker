namespace KSubMaker.Domain.Subtitles;

public sealed record TranslationBatchOptions
{
    public int MaxItems { get; init; } = 30;
    public int MaxChars { get; init; } = 2500;
    public double MaxSeconds { get; init; } = 180;

    /// <summary>How many preceding items are supplied as read-only context.</summary>
    public int ContextItems { get; init; } = 3;
}

/// <summary>A batch of segments plus the already-translated context that precedes it.</summary>
public sealed record TranslationBatch
{
    public required int Index { get; init; }
    public required IReadOnlyList<TranscriptionSegment> Segments { get; init; }

    /// <summary>Preceding source items supplied for context only. Never part of the expected output.</summary>
    public IReadOnlyList<TranscriptionSegment> Context { get; init; } = [];

    public IReadOnlyList<SubtitleItem> Items =>
        Segments.Select(s => new SubtitleItem(s.Id, s.Text)).ToArray();

    public IReadOnlyList<SubtitleItem> ContextItems =>
        Context.Select(s => new SubtitleItem(s.Id, s.Text)).ToArray();
}

/// <summary>
/// Splits a transcript into translation batches. A batch closes at whichever limit is hit first:
/// item count, character count, or covered media duration.
/// </summary>
public static class TranslationBatcher
{
    public static IReadOnlyList<TranslationBatch> Split(
        IReadOnlyList<TranscriptionSegment> segments,
        TranslationBatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        options ??= new TranslationBatchOptions();

        var maxItems = Math.Max(1, options.MaxItems);
        var maxChars = Math.Max(50, options.MaxChars);
        var maxSeconds = Math.Max(5d, options.MaxSeconds);

        var batches = new List<TranslationBatch>();
        var current = new List<TranscriptionSegment>();
        var chars = 0;
        var batchIndex = 0;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            var context = batches.Count == 0 || options.ContextItems <= 0
                ? Array.Empty<TranscriptionSegment>()
                : batches[^1].Segments.TakeLast(options.ContextItems).ToArray();

            batches.Add(new TranslationBatch
            {
                Index = batchIndex++,
                Segments = current.ToArray(),
                Context = context
            });

            current = [];
            chars = 0;
        }

        foreach (var segment in segments)
        {
            if (current.Count > 0)
            {
                var wouldExceedItems = current.Count + 1 > maxItems;
                var wouldExceedChars = chars + segment.Text.Length > maxChars;
                var wouldExceedSpan = segment.End - current[0].Start > maxSeconds;

                if (wouldExceedItems || wouldExceedChars || wouldExceedSpan)
                {
                    Flush();
                }
            }

            current.Add(segment);
            chars += segment.Text.Length;
        }

        Flush();
        return batches;
    }
}

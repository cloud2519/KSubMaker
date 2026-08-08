namespace KSubMaker.Domain.Subtitles;

/// <summary>A word with its own timestamps, as produced by faster-whisper's word_timestamps option.</summary>
public sealed record WordTimestamp(string Word, double Start, double End, double? Probability = null);

/// <summary>One recognised span of speech before translation.</summary>
public sealed record TranscriptionSegment
{
    public required int Id { get; init; }
    public required double Start { get; init; }
    public required double End { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<WordTimestamp> Words { get; init; } = [];

    public double Duration => End - Start;
}

/// <summary>The complete ASR result that is checkpointed as <c>transcription.json</c>.</summary>
public sealed record TranscriptionResult
{
    public required string SourceLanguage { get; init; }
    public double LanguageProbability { get; init; }
    public required IReadOnlyList<TranscriptionSegment> Segments { get; init; }
    public string? ModelId { get; init; }
    public double? DurationSeconds { get; init; }
}

/// <summary>Text handed to a translation engine. Timecodes are deliberately absent.</summary>
public sealed record SubtitleItem(int Id, string Text);

/// <summary>Text returned by a translation engine, rejoined to timings by <see cref="SubtitleItem.Id"/>.</summary>
public sealed record TranslatedSubtitleItem(int Id, string Translation);

/// <summary>A finished subtitle cue ready to be serialised to SRT.</summary>
public sealed record SubtitleCue
{
    public required int Index { get; init; }
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Already line-broken; at most <c>MaxLinesPerCue</c> entries.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    public double Duration => End - Start;
    public string Text => string.Join("\n", Lines);
}

/// <summary>Style and glossary information passed to an <c>ITranslationEngine</c>.</summary>
public sealed record TranslationContext
{
    public required string SourceLanguage { get; init; }
    public string TargetLanguage { get; init; } = "ko";

    /// <summary>Already-translated tail of the previous batch. Read-only context; never re-emitted.</summary>
    public IReadOnlyList<SubtitleItem> PrecedingContext { get; init; } = [];

    public Settings.TranslationStyle Style { get; init; } = Settings.TranslationStyle.Natural;
    public IReadOnlyDictionary<string, string> Glossary { get; init; } = new Dictionary<string, string>();
}

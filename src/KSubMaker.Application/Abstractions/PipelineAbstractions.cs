using KSubMaker.Domain.Media;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.Application.Abstractions;

/// <summary>Reads container metadata with FFprobe.</summary>
public interface IMediaProbe
{
    /// <summary>Fills duration, audio tracks and subtitle tracks. Never throws for a bad file:
    /// the failure is reported through <see cref="VideoFile.ProbeError"/>.</summary>
    Task<VideoFile> ProbeAsync(VideoFile file, CancellationToken cancellationToken = default);
}

public sealed record AudioExtractionRequest
{
    public required string VideoPath { get; init; }
    public required string OutputWavPath { get; init; }

    /// <summary>Null selects FFmpeg's default audio stream.</summary>
    public int? AudioTrackIndex { get; init; }

    public int SampleRate { get; init; } = 16_000;
    public int Channels { get; init; } = 1;
}

/// <summary>Extracts 16 kHz mono PCM audio via FFmpeg.</summary>
public interface IAudioExtractor
{
    Task ExtractAsync(
        AudioExtractionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed record TranscriptionRequest
{
    public required string AudioPath { get; init; }
    public string Language { get; init; } = "auto";
    public string ModelId { get; init; } = "auto";
    public ComputeType? ComputeType { get; init; }
    public int BeamSize { get; init; } = 5;
    public bool VadFilter { get; init; } = true;
    public bool WordTimestamps { get; init; } = true;
    public bool ConditionOnPreviousText { get; init; }

    /// <summary>Null keeps the transcriber's built-in per-language hint.</summary>
    public string? InitialPrompt { get; init; }

    public double? DurationSeconds { get; init; }
}

/// <summary>Speech recognition. Implemented by the worker-backed and fake transcribers.</summary>
public interface ITranscriber
{
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Translation. Exactly the interface named in the specification.
/// Implementations must return one item per input id: no additions, no omissions, no reordering
/// requirements (the caller rejoins by id).
/// </summary>
public interface ITranslationEngine
{
    Task<IReadOnlyList<TranslatedSubtitleItem>> TranslateAsync(
        IReadOnlyList<SubtitleItem> items,
        TranslationContext context,
        CancellationToken cancellationToken);
}

/// <summary>Writes the final SRT atomically (temp file + move).</summary>
public interface ISubtitleWriter
{
    /// <summary>Returns the path actually written, or null when the conflict policy said skip.</summary>
    Task<string?> WriteAsync(
        IReadOnlyList<SubtitleCue> cues,
        string desiredPath,
        OutputConflictPolicy conflictPolicy,
        CancellationToken cancellationToken);
}

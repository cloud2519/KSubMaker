using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;

namespace KSubMaker.Application.Abstractions;

/// <summary>Progress push from a processor back to the queue.</summary>
public sealed record JobProgress
{
    public required string JobId { get; init; }
    public required JobStage Stage { get; init; }
    public double StageProgress { get; init; }
    public double OverallProgress { get; init; }
    public double? Speed { get; init; }
    public string? Message { get; init; }
    public string? DetectedLanguage { get; init; }
    public double? LanguageProbability { get; init; }
}

public sealed record JobExecutionResult
{
    public required bool Success { get; init; }
    public string? OutputPath { get; init; }
    public int CueCount { get; init; }
    public string? SourceLanguage { get; init; }
    public string? WhisperModel { get; init; }
    public string? TranslationModel { get; init; }
    public TranslationEngineKind? TranslationEngine { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>The worker reported the target already existed and the policy said skip.</summary>
    public bool Skipped { get; init; }

    /// <summary>Host may retry once automatically.</summary>
    public bool Recoverable { get; init; }

    public static JobExecutionResult Ok(string outputPath, int cueCount) =>
        new() { Success = true, OutputPath = outputPath, CueCount = cueCount };

    public static JobExecutionResult Fail(string code, string message, bool recoverable = false) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message, Recoverable = recoverable };
}

/// <summary>
/// Which part of the pipeline to run. Splitting the pipeline is what makes processing strategy B
/// ("transcribe everything, unload Whisper, then translate everything") possible without the queue
/// having to know anything about model residency.
/// </summary>
public enum JobPhase
{
    /// <summary>Extract → transcribe → translate → write. Strategy A.</summary>
    Full,

    /// <summary>Extract → transcribe, then stop and checkpoint. Strategy B pass 1, strategy C lane 1.</summary>
    TranscribeOnly,

    /// <summary>Resume from the transcription checkpoint, translate and write. Strategy B pass 2.</summary>
    TranslateAndWrite
}

/// <summary>
/// Executes one job end to end. Two implementations exist: the real one that drives the Python
/// worker, and an in-process one used by "Fake AI 모드" and the integration tests.
/// </summary>
public interface IJobProcessor
{
    /// <summary>Human-readable name shown in logs and in the settings screen.</summary>
    string Name { get; }

    Task<JobExecutionResult> ProcessAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extract <paramref name="job"/>'s audio now, before the queue reaches it.
    ///
    /// <para>Pulling audio out of a container is ffmpeg work — CPU and disk, no VRAM — so it can run
    /// while the GPU is busy with the file in front. There is no matching "use the prefetched audio"
    /// call: this writes the same wav and checkpoint stanza the job would have written itself, so
    /// the job just finds the stage already done.</para>
    ///
    /// <para>Best effort by contract. Implementations return false rather than throwing when they
    /// cannot prefetch — an old worker that does not know the v1.3 command, a file that vanished,
    /// a busy lane. The only cost of a failed prefetch is the time it would have saved.</para>
    /// </summary>
    Task<AudioPrefetchOutcome> PrefetchAudioAsync(
        Job job,
        AppSettings settings,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a prefetch actually did.
///
/// <para>The three cases are kept apart because reporting them as one boolean made the logs lie:
/// a job whose wav was left behind by an earlier run answered in two milliseconds and was written
/// down as "음성을 미리 추출했습니다", which is indistinguishable from a real 70-second extraction
/// when you are trying to work out whether the feature runs at all.</para>
/// </summary>
public enum AudioPrefetchOutcome
{
    /// <summary>Nothing was done and no audio is ready: cancelled, refused, or not applicable.</summary>
    NotAttempted,

    /// <summary>The audio was already on disk and still matches. Ready, but nothing was extracted.</summary>
    AlreadyPresent,

    /// <summary>ffmpeg ran and produced the wav.</summary>
    Extracted
}

/// <summary>Chooses the processor for the current settings (real worker vs. fake pipeline).</summary>
public interface IJobProcessorSelector
{
    IJobProcessor Select(AppSettings settings);
}

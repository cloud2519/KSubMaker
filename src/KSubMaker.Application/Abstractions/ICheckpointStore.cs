using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.Application.Abstractions;

/// <summary>What a previous run of a job already finished.</summary>
public sealed record JobCheckpoint
{
    public required string JobId { get; init; }
    public required string VideoPath { get; init; }

    /// <summary>Last stage that completed successfully.</summary>
    public JobStage CompletedStage { get; init; } = JobStage.None;

    public string? AudioPath { get; init; }
    public string? DetectedLanguage { get; init; }
    public string? WhisperModel { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Invalidates the checkpoint when the source file changed underneath it.</summary>
    public long SourceFileSize { get; init; }
    public DateTime SourceLastWriteUtc { get; init; }
}

/// <summary>
/// Durable per-job state under <c>cache/{jobId}</c>. Everything here is written atomically so a
/// power cut leaves either the old file or the new one, never a half-written one.
/// </summary>
public interface ICheckpointStore
{
    Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default);

    Task<TranscriptionResult?> LoadTranscriptionAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveTranscriptionAsync(string jobId, TranscriptionResult result, CancellationToken cancellationToken = default);

    /// <summary>Translations completed so far, keyed by segment id.</summary>
    Task<IReadOnlyDictionary<int, string>> LoadPartialTranslationAsync(string jobId, CancellationToken cancellationToken = default);

    Task SavePartialTranslationAsync(string jobId, IReadOnlyDictionary<int, string> translations, CancellationToken cancellationToken = default);

    /// <summary>Removes the whole checkpoint directory for a job.</summary>
    Task ClearAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes just the extracted <c>audio.wav</c>, keeping the rest of the checkpoint.
    ///
    /// <para>Called once the subtitle has been written. The wav is the only large thing in a job's
    /// cache — roughly 115MB per hour of video, so a folder of two-hour films leaves tens of
    /// gigabytes of audio nobody will read again — while everything beside it is JSON measured in
    /// kilobytes.</para>
    ///
    /// <para>Keeping that JSON is the point of deleting only the wav: <c>transcription.json</c> is
    /// what lets 재시도 with a different translation engine skip the expensive ASR stage entirely.
    /// Clearing the whole directory would reclaim a fraction more and cost an hour of GPU time the
    /// next time the user compares two engines.</para>
    /// </summary>
    /// <returns>Bytes reclaimed; zero when there was no audio to delete.</returns>
    Task<long> DeleteAudioAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes cache directories that belong to no known job, plus stray <c>*.tmp</c> files left by a
    /// crash. Returns the number of bytes reclaimed.
    /// </summary>
    Task<long> CleanupOrphansAsync(IReadOnlyCollection<string> knownJobIds, CancellationToken cancellationToken = default);
}

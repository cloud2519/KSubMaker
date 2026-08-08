namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Lifecycle state of a single video-to-Korean-subtitle job.
/// Persisted by name (not by ordinal) so that reordering never corrupts an existing database.
/// </summary>
public enum JobStatus
{
    Pending,
    Probing,
    ExtractingAudio,
    Transcribing,
    Translating,
    WritingSubtitle,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// The processing stage a job is currently executing. Distinct from <see cref="JobStatus"/> because a
/// job can be <see cref="JobStatus.Paused"/> or <see cref="JobStatus.Failed"/> while still remembering
/// which stage it stopped in, which is what checkpoint resume keys off.
/// </summary>
public enum JobStage
{
    None,
    Probing,
    ExtractingAudio,
    Transcribing,
    Translating,
    WritingSubtitle,
    Done
}

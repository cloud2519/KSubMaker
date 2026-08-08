namespace KSubMaker.Domain.Jobs;

/// <summary>
/// Converts (stage, stage-progress) into a single monotonically increasing overall percentage.
/// Weights are rough wall-clock shares measured on a GPU run; they only need to be stable and
/// monotonic, not exact.
/// </summary>
public static class ProgressCalculator
{
    /// <summary>Fraction of total work attributed to each stage. Must sum to 1.0.</summary>
    public static readonly IReadOnlyDictionary<JobStage, double> Weights = new Dictionary<JobStage, double>
    {
        [JobStage.Probing] = 0.02,
        [JobStage.ExtractingAudio] = 0.08,
        [JobStage.Transcribing] = 0.55,
        [JobStage.Translating] = 0.32,
        [JobStage.WritingSubtitle] = 0.03
    };

    private static readonly JobStage[] Order =
    [
        JobStage.Probing,
        JobStage.ExtractingAudio,
        JobStage.Transcribing,
        JobStage.Translating,
        JobStage.WritingSubtitle
    ];

    /// <summary>Overall percentage (0-100) for the given stage at the given stage percentage (0-100).</summary>
    public static double Overall(JobStage stage, double stageProgress)
    {
        if (stage == JobStage.Done)
        {
            return 100d;
        }

        if (stage == JobStage.None)
        {
            return 0d;
        }

        var clamped = Math.Clamp(stageProgress, 0d, 100d) / 100d;
        var completed = 0d;

        foreach (var s in Order)
        {
            if (s == stage)
            {
                break;
            }

            completed += Weights[s];
        }

        var value = (completed + (Weights[stage] * clamped)) * 100d;
        return Math.Round(Math.Clamp(value, 0d, 100d), 2);
    }

    /// <summary>
    /// Estimates remaining wall-clock time from overall progress and elapsed time.
    /// Returns null while progress is too small to extrapolate from.
    /// </summary>
    public static TimeSpan? EstimateRemaining(double overallProgress, TimeSpan elapsed)
    {
        if (overallProgress <= 0.5d || overallProgress >= 100d || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var totalSeconds = elapsed.TotalSeconds / (overallProgress / 100d);
        var remaining = totalSeconds - elapsed.TotalSeconds;
        return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining);
    }
}

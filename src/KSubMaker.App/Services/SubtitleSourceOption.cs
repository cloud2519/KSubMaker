using KSubMaker.Domain.Jobs;

namespace KSubMaker.App.Services;

/// <summary>
/// One entry in the 자막 원본 picker: what the user sees, and the per-job override it produces.
///
/// A flat option list rather than a mode radio plus a track combo, because the two are not
/// independent — "내장 자막" without a track index is not a choice the pipeline can act on.
/// </summary>
/// <param name="Display">Korean label, built from <c>AudioTrackInfo.DisplayName</c> /
/// <c>EmbeddedSubtitleTrackInfo.DisplayName</c> so it names the codec and language the container
/// actually reports.</param>
/// <param name="Mode">Override recorded on the job. <see cref="JobSourceOverride.None"/> restores
/// the application-wide policy.</param>
/// <param name="TrackIndex">Stream index for the chosen audio or subtitle track; null lets FFmpeg
/// pick the default.</param>
/// <param name="Language">Language tag of a chosen subtitle track, when the container reported a
/// usable one.</param>
public sealed record SubtitleSourceOption(
    string Display,
    JobSourceOverride Mode,
    int? TrackIndex = null,
    string? Language = null)
{
    /// <summary>Secondary line shown under <see cref="Display"/>; null hides it.</summary>
    public string? Hint { get; init; }
}

namespace KSubMaker.Domain.Settings;

/// <summary>
/// Translates the pre-split subtitle settings into the pair that replaced them.
///
/// <para>Until this split there was one <c>ExistingSubtitlePolicy</c> dropdown plus a separate
/// <c>SkipIfKoreanSubtitleExists</c> checkbox that was evaluated <em>before</em> it — so the
/// checkbox could silently override the dropdown, which is half the reason the two were pulled
/// apart. Both values have to be read together to work out what a user actually had.</para>
///
/// <para>Pure and in Domain so the mapping is testable: getting it wrong would not throw, it would
/// quietly change what happens to every file in someone's library on their next run.</para>
/// </summary>
public static class LegacySubtitleSettings
{
    /// <summary>Wire names of the retired enum. Strings, because the type itself is gone.</summary>
    public const string AlwaysTranscribe = "AlwaysTranscribe";
    public const string SkipIfExternalSubtitleExists = "SkipIfExternalSubtitleExists";
    public const string UseEmbeddedTrack = "UseEmbeddedTrack";
    public const string UseExternalSubtitle = "UseExternalSubtitle";
    public const string CompleteIfKoreanExists = "CompleteIfKoreanExists";
    public const string AskPerFile = "AskPerFile";

    /// <summary>
    /// Maps a stored (policy, checkbox) pair onto the new settings.
    /// </summary>
    /// <param name="policy">The stored <c>ExistingSubtitlePolicy</c> name; null or unknown reads as the old default.</param>
    /// <param name="skipIfKoreanSubtitleExists">The stored standalone checkbox, which defaulted to true.</param>
    public static (SubtitleSourcePreference Source, ExistingSubtitleRule Rule) Migrate(
        string? policy, bool skipIfKoreanSubtitleExists)
    {
        var source = policy switch
        {
            UseEmbeddedTrack => SubtitleSourcePreference.PreferEmbeddedTrack,
            UseExternalSubtitle => SubtitleSourcePreference.PreferExternalFile,
            AskPerFile => SubtitleSourcePreference.AskPerFile,

            // AlwaysTranscribe, CompleteIfKoreanExists and SkipIfExternalSubtitleExists all
            // transcribed; they differed only in which files they let through.
            _ => SubtitleSourcePreference.AudioOnly
        };

        var rule = policy switch
        {
            SkipIfExternalSubtitleExists => ExistingSubtitleRule.SkipIfAnySubtitleExists,
            CompleteIfKoreanExists => ExistingSubtitleRule.CompleteIfKoreanExists,

            // Everything else deferred to the checkbox, which is exactly the confusion this split
            // removes: with it off, a policy of AlwaysTranscribe really did mean "redo every file".
            _ => skipIfKoreanSubtitleExists
                ? ExistingSubtitleRule.CompleteIfKoreanExists
                : ExistingSubtitleRule.ProcessAnyway
        };

        return (source, rule);
    }
}

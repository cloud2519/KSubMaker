using FluentAssertions;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The old (policy, checkbox) pair mapped onto the two settings that replaced it.
///
/// Every case here is somebody's saved configuration. Getting one wrong would not throw — it would
/// quietly change what happens to every file in their library the next time they press 시작.
/// </summary>
public sealed class LegacySubtitleSettingsTests
{
    [Theory]
    // The three policies that transcribed differ only in which files they let through.
    [InlineData(LegacySubtitleSettings.AlwaysTranscribe, SubtitleSourcePreference.AudioOnly)]
    [InlineData(LegacySubtitleSettings.CompleteIfKoreanExists, SubtitleSourcePreference.AudioOnly)]
    [InlineData(LegacySubtitleSettings.SkipIfExternalSubtitleExists, SubtitleSourcePreference.AudioOnly)]
    [InlineData(LegacySubtitleSettings.UseEmbeddedTrack, SubtitleSourcePreference.PreferEmbeddedTrack)]
    [InlineData(LegacySubtitleSettings.UseExternalSubtitle, SubtitleSourcePreference.PreferExternalFile)]
    [InlineData(LegacySubtitleSettings.AskPerFile, SubtitleSourcePreference.AskPerFile)]
    public void Each_old_policy_maps_onto_the_source_it_actually_used(string policy, SubtitleSourcePreference expected)
    {
        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: true)
            .Source.Should().Be(expected);
    }

    [Theory]
    [InlineData(LegacySubtitleSettings.SkipIfExternalSubtitleExists, ExistingSubtitleRule.SkipIfAnySubtitleExists)]
    [InlineData(LegacySubtitleSettings.CompleteIfKoreanExists, ExistingSubtitleRule.CompleteIfKoreanExists)]
    public void A_policy_that_filtered_files_keeps_filtering_them(string policy, ExistingSubtitleRule expected)
    {
        // These two carried their own filter, so the checkbox was irrelevant to them either way.
        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: true).Rule.Should().Be(expected);
        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: false).Rule.Should().Be(expected);
    }

    [Theory]
    [InlineData(LegacySubtitleSettings.AlwaysTranscribe)]
    [InlineData(LegacySubtitleSettings.UseEmbeddedTrack)]
    [InlineData(LegacySubtitleSettings.UseExternalSubtitle)]
    [InlineData(LegacySubtitleSettings.AskPerFile)]
    public void Everything_else_took_its_filter_from_the_checkbox(string policy)
    {
        // This is the confusion the split removes: the checkbox was evaluated *before* the dropdown,
        // so it could silently override it. Preserving that is still the honest migration.
        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: true)
            .Rule.Should().Be(ExistingSubtitleRule.CompleteIfKoreanExists);

        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: false)
            .Rule.Should().Be(ExistingSubtitleRule.ProcessAnyway);
    }

    [Fact]
    public void The_old_defaults_migrate_to_the_new_defaults()
    {
        // AlwaysTranscribe + checkbox on is what a database written by the shipped app holds unless
        // the user changed something, so this is the case that runs on most machines.
        var (source, rule) = LegacySubtitleSettings.Migrate(
            LegacySubtitleSettings.AlwaysTranscribe, skipIfKoreanSubtitleExists: true);

        var fresh = new AppSettings();

        source.Should().Be(fresh.SubtitleSource);
        rule.Should().Be(fresh.ExistingSubtitleRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingFromAFutureBuild")]
    public void An_unreadable_policy_reads_as_the_old_default(string? policy)
    {
        LegacySubtitleSettings.Migrate(policy, skipIfKoreanSubtitleExists: true)
            .Should().Be((SubtitleSourcePreference.AudioOnly, ExistingSubtitleRule.CompleteIfKoreanExists));
    }
}

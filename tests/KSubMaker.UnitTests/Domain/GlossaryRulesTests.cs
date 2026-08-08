using FluentAssertions;
using KSubMaker.Domain.Settings;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The 고유명사 사전 editor's rules. They live in the domain so they can be tested without WPF; the
/// view model only turns the verdict into a Korean sentence from the resource table.
/// </summary>
public sealed class GlossaryRulesTests
{
    private static readonly string[] Existing = ["Sherlock", "Baker Street"];

    // -----------------------------------------------------------------------
    // Validate
    // -----------------------------------------------------------------------

    [Fact]
    public void A_new_pair_is_accepted()
    {
        GlossaryRules.Validate("Watson", "왓슨", Existing).Should().Be(GlossaryValidation.Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_blank_source_term_is_rejected(string? source)
    {
        GlossaryRules.Validate(source, "왓슨", Existing).Should().Be(GlossaryValidation.SourceRequired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_korean_rendering_is_rejected(string? target)
    {
        GlossaryRules.Validate("Watson", target, Existing).Should().Be(GlossaryValidation.TargetRequired);
    }

    [Fact]
    public void A_blank_source_is_reported_before_a_blank_target()
    {
        // One message at a time: the user fixes the first field and presses 추가 again.
        GlossaryRules.Validate(" ", " ", Existing).Should().Be(GlossaryValidation.SourceRequired);
    }

    [Theory]
    [InlineData("Sherlock")]
    [InlineData("sherlock")]
    [InlineData("SHERLOCK")]
    [InlineData("  Sherlock  ")]
    [InlineData("baker street")]
    public void A_duplicate_source_term_is_rejected_regardless_of_case_or_padding(string source)
    {
        GlossaryRules.Validate(source, "다른 번역", Existing).Should().Be(GlossaryValidation.DuplicateSource);
    }

    [Fact]
    public void A_duplicate_is_only_reported_after_both_fields_are_filled_in()
    {
        GlossaryRules.Validate("Sherlock", "", Existing).Should().Be(GlossaryValidation.TargetRequired);
    }

    [Fact]
    public void An_empty_table_accepts_anything_non_blank()
    {
        GlossaryRules.Validate("Watson", "왓슨", []).Should().Be(GlossaryValidation.Ok);
    }

    // -----------------------------------------------------------------------
    // Build
    // -----------------------------------------------------------------------

    private static KeyValuePair<string, string> Pair(string source, string target) => new(source, target);

    [Fact]
    public void Build_trims_both_sides()
    {
        var glossary = GlossaryRules.Build([Pair("  Sherlock ", "  셜록 ")]);

        glossary.Should().ContainKey("Sherlock").WhoseValue.Should().Be("셜록");
    }

    [Fact]
    public void Build_drops_rows_that_were_edited_to_blank()
    {
        var glossary = GlossaryRules.Build(
        [
            Pair("Sherlock", "셜록"),
            Pair("   ", "이름 없음"),
            Pair("Watson", "  ")
        ]);

        glossary.Should().ContainSingle().Which.Key.Should().Be("Sherlock");
    }

    [Fact]
    public void Build_keeps_the_first_of_two_rows_that_differ_only_in_case()
    {
        var glossary = GlossaryRules.Build([Pair("Sherlock", "셜록"), Pair("sherlock", "셜럭")]);

        glossary.Should().ContainSingle();
        glossary["SHERLOCK"].Should().Be("셜록");
    }

    [Fact]
    public void The_built_dictionary_is_case_insensitive_like_the_persisted_one()
    {
        var glossary = GlossaryRules.Build([Pair("Baker Street", "베이커가")]);

        glossary.ContainsKey("baker street").Should().BeTrue();
    }

    [Fact]
    public void Build_round_trips_through_AppSettings()
    {
        var settings = new AppSettings
        {
            Glossary = GlossaryRules.Build([Pair("Sherlock", "셜록"), Pair("Baker Street", "베이커가")])
        };

        var clone = settings.Clone();

        clone.Glossary.Should().HaveCount(2);
        clone.Glossary["sherlock"].Should().Be("셜록");
    }

    [Fact]
    public void Build_of_nothing_is_an_empty_dictionary_not_null()
    {
        GlossaryRules.Build([]).Should().BeEmpty();
    }
}

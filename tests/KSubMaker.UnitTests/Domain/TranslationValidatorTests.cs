using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "번역 응답 검증": the guard that stops a short LLM answer silently dropping subtitles.</summary>
public sealed class TranslationValidatorTests
{
    private static IReadOnlyList<SubtitleItem> Requested(params int[] ids) =>
        ids.Select(id => new SubtitleItem(id, $"source {id}")).ToArray();

    private static IReadOnlyList<TranslatedSubtitleItem> Returned(params (int Id, string Text)[] items) =>
        items.Select(i => new TranslatedSubtitleItem(i.Id, i.Text)).ToArray();

    [Fact]
    public void A_complete_response_is_valid()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2, 3),
            Returned((1, "하나"), (2, "둘"), (3, "셋")));

        result.IsValid.Should().BeTrue();
        result.MissingIds.Should().BeEmpty();
        result.DuplicateIds.Should().BeEmpty();
        result.UnexpectedIds.Should().BeEmpty();
        result.EmptyIds.Should().BeEmpty();
        result.Describe().Should().Be("정상");
    }

    [Fact]
    public void A_reordered_response_is_still_valid_because_the_caller_rejoins_by_id()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2, 3),
            Returned((3, "셋"), (1, "하나"), (2, "둘")));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_missing_id_is_reported_and_sorted()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2, 3, 4),
            Returned((4, "넷"), (1, "하나")));

        result.IsValid.Should().BeFalse();
        result.MissingIds.Should().Equal(2, 3);
        result.Describe().Should().Contain("누락 2건");
    }

    [Fact]
    public void A_duplicated_id_is_reported()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2),
            Returned((1, "하나"), (2, "둘"), (1, "하나 또")));

        result.IsValid.Should().BeFalse();
        result.DuplicateIds.Should().Equal(1);
        result.Describe().Should().Contain("중복 1건");
    }

    [Fact]
    public void An_invented_id_is_reported_as_unexpected()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2),
            Returned((1, "하나"), (2, "둘"), (99, "어디서 온 거지")));

        result.IsValid.Should().BeFalse();
        result.UnexpectedIds.Should().Equal(99);
        result.MissingIds.Should().BeEmpty();
        result.Describe().Should().Contain("알 수 없는 id 1건");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_blank_translation_is_reported_as_empty(string blank)
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2),
            Returned((1, "하나"), (2, blank)));

        result.IsValid.Should().BeFalse();
        result.EmptyIds.Should().Equal(2);
        result.Describe().Should().Contain("빈 번역 1건");
    }

    [Fact]
    public void Several_problems_are_reported_together()
    {
        var result = TranslationValidator.Validate(
            Requested(1, 2, 3, 4),
            Returned((1, "하나"), (1, "또 하나"), (2, "  "), (77, "몰라")));

        result.IsValid.Should().BeFalse();
        result.MissingIds.Should().Equal(3, 4);
        result.DuplicateIds.Should().Equal(1);
        result.UnexpectedIds.Should().Equal(77);
        result.EmptyIds.Should().Equal(2);

        var description = result.Describe();
        description.Should().Contain("누락").And.Contain("중복").And.Contain("알 수 없는 id").And.Contain("빈 번역");
    }

    [Fact]
    public void An_empty_request_answered_with_nothing_is_valid()
    {
        var result = TranslationValidator.Validate([], []);

        result.IsValid.Should().BeTrue();
        result.MissingIds.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_request_answered_with_content_is_all_unexpected()
    {
        var result = TranslationValidator.Validate([], Returned((1, "하나")));

        result.IsValid.Should().BeFalse();
        result.UnexpectedIds.Should().Equal(1);
    }

    [Fact]
    public void An_empty_response_to_a_real_request_reports_every_id_as_missing()
    {
        var result = TranslationValidator.Validate(Requested(1, 2, 3), []);

        result.IsValid.Should().BeFalse();
        result.MissingIds.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Validate_rejects_null_arguments()
    {
        var requestedNull = () => TranslationValidator.Validate(null!, []);
        var returnedNull = () => TranslationValidator.Validate([], null!);

        requestedNull.Should().Throw<ArgumentNullException>();
        returnedNull.Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // corrupt vs merely incomplete — the distinction that decides degrade or fail
    // -----------------------------------------------------------------------

    [Fact]
    public void A_blank_translation_is_incomplete_but_not_corrupt()
    {
        // The whole point: a line the engine will not translate is a quirk to work around, not a
        // reason to throw away every other line in the batch.
        var result = TranslationValidator.Validate(Requested(1, 2), Returned((1, "하나"), (2, "")));

        result.IsValid.Should().BeFalse();
        result.IsCorrupt.Should().BeFalse();
    }

    [Fact]
    public void A_dropped_line_is_incomplete_but_not_corrupt()
    {
        var result = TranslationValidator.Validate(Requested(1, 2), Returned((1, "하나")));

        result.IsCorrupt.Should().BeFalse();
    }

    [Fact]
    public void An_unexpected_id_is_corrupt()
    {
        var result = TranslationValidator.Validate(Requested(1), Returned((1, "하나"), (99, "몰라")));

        result.IsCorrupt.Should().BeTrue("an engine answering questions nobody asked is not following the contract");
    }

    [Fact]
    public void A_duplicated_id_is_corrupt()
    {
        var result = TranslationValidator.Validate(Requested(1, 2), Returned((1, "하나"), (1, "또"), (2, "둘")));

        result.IsCorrupt.Should().BeTrue();
    }

    [Fact]
    public void A_clean_response_is_not_corrupt()
    {
        TranslationValidator.Validate(Requested(1, 2), Returned((1, "하나"), (2, "둘")))
            .IsCorrupt.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // ToMap
    // -----------------------------------------------------------------------

    [Fact]
    public void ToMap_keys_by_id_and_trims_the_text()
    {
        var map = TranslationValidator.ToMap(Returned((1, "  하나  "), (2, "\t둘\n")));

        map.Should().HaveCount(2);
        map[1].Should().Be("하나");
        map[2].Should().Be("둘");
    }

    [Fact]
    public void ToMap_drops_blank_translations_instead_of_storing_an_empty_subtitle()
    {
        var map = TranslationValidator.ToMap(Returned((1, "하나"), (2, "   "), (3, "")));

        map.Keys.Should().Equal(1);
    }

    [Fact]
    public void ToMap_keeps_the_last_value_for_a_duplicated_id()
    {
        var map = TranslationValidator.ToMap(Returned((1, "먼저"), (1, "나중")));

        map[1].Should().Be("나중");
    }

    [Fact]
    public void ToMap_of_an_empty_response_is_empty()
    {
        TranslationValidator.ToMap([]).Should().BeEmpty();
    }
}

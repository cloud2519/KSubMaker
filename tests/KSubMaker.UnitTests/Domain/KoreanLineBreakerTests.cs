using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// Covers the hard rule of Korean line breaking: a line must never start with a 조사 (particle) or a
/// dependent noun, because "학교에서\n는 만났다" reads as broken Korean even though it fits.
/// </summary>
public sealed class KoreanLineBreakerTests
{
    /// <summary>
    /// The particles and dependent nouns named in the specification. Kept here independently of the
    /// production array so the test still means something if that array is edited.
    /// </summary>
    private static readonly string[] ForbiddenLineStarts =
    [
        "은", "는", "이", "가", "을", "를", "에", "에서", "에게", "께", "께서", "의", "도", "만",
        "까지", "부터", "으로", "로", "와", "과", "랑", "이랑", "보다", "처럼", "같이", "마다",
        "밖에", "조차", "마저", "이나", "나", "든지", "라도", "이라도", "요", "죠", "네요",
        "것", "거", "수", "때", "중", "등", "및", "뿐", "지", "채", "만큼", "대로", "듯", "겸"
    ];

    /// <summary>Sentences deliberately written with free-standing particles at plausible break points.</summary>
    public static TheoryData<string> ParticleTrapSentences =>
    [
        "오늘 아침에 학교 에서 친구 를 만났고 우리 는 함께 도서관 으로 갔다",
        "그는 어제 우리가 만난 그 사람 은 사실 아주 유명한 배우였다고 말했다",
        "이번 계획 은 처음부터 끝 까지 완전히 다시 검토해야 한다고 생각합니다",
        "네가 말한 그 이야기 를 나 는 오늘 처음 들었고 정말 많이 놀랐어",
        "회의 에서 결정된 내용 은 내일 아침 까지 모두 에게 공유되어야 합니다",
        "신호는 북쪽 탑에서 오고 있어 지금 당장 모두 대피시켜야 해",
        "말다툼할 시간이 없어 우리는 해 뜨기 전에 반드시 떠나야만 해"
    ];

    [Theory]
    [MemberData(nameof(ParticleTrapSentences))]
    public void A_continuation_line_never_begins_with_a_particle(string sentence)
    {
        var lines = KoreanLineBreaker.Break(sentence, maxLines: 2, maxCharsPerLine: 22);

        foreach (var line in lines.Skip(1))
        {
            var firstToken = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            ForbiddenLineStarts.Should().NotContain(firstToken,
                $"'{firstToken}' must not start a line (원문: {sentence})");
        }
    }

    [Theory]
    [MemberData(nameof(ParticleTrapSentences))]
    public void Breaking_preserves_the_text_exactly(string sentence)
    {
        var lines = KoreanLineBreaker.Break(sentence, maxLines: 2, maxCharsPerLine: 22);

        string.Join(" ", lines).Should().Be(KoreanLineBreaker.Normalize(sentence));
    }

    [Theory]
    [MemberData(nameof(ParticleTrapSentences))]
    public void Breaking_never_produces_more_than_the_requested_number_of_lines(string sentence)
    {
        KoreanLineBreaker.Break(sentence, maxLines: 2, maxCharsPerLine: 22).Should().HaveCountLessThanOrEqualTo(2);
        KoreanLineBreaker.Break(sentence, maxLines: 3, maxCharsPerLine: 12).Should().HaveCountLessThanOrEqualTo(3);
    }

    [Theory]
    [MemberData(nameof(ParticleTrapSentences))]
    public void No_produced_line_is_blank(string sentence)
    {
        KoreanLineBreaker.Break(sentence, maxLines: 2, maxCharsPerLine: 22)
            .Should().OnlyContain(l => l.Trim().Length > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public void Blank_text_produces_no_lines(string text)
    {
        KoreanLineBreaker.Break(text).Should().BeEmpty();
    }

    [Fact]
    public void Text_that_already_fits_is_returned_as_a_single_line()
    {
        KoreanLineBreaker.Break("짧은 문장입니다", maxLines: 2, maxCharsPerLine: 22)
            .Should().ContainSingle().Which.Should().Be("짧은 문장입니다");
    }

    [Fact]
    public void A_single_line_budget_never_splits()
    {
        var text = "아주 긴 문장을 한 줄로만 표시해야 하는 경우에는 절대 나누지 않습니다";

        KoreanLineBreaker.Break(text, maxLines: 1, maxCharsPerLine: 10)
            .Should().ContainSingle().Which.Should().Be(text);
    }

    [Fact]
    public void A_break_after_sentence_punctuation_is_preferred()
    {
        var lines = KoreanLineBreaker.Break(
            "먼저 이렇게 말했습니다. 그리고 나서 완전히 다른 이야기를 꺼냈습니다",
            maxLines: 2,
            maxCharsPerLine: 22);

        lines.Should().HaveCount(2);
        lines[0].Should().EndWith(".");
    }

    [Fact]
    public void Dense_text_without_spaces_still_gets_split_at_the_hard_limit()
    {
        var text = new string('가', 60);

        var lines = KoreanLineBreaker.Break(text, maxLines: 2, maxCharsPerLine: 20);

        lines.Should().HaveCount(2);
        lines[0].Should().HaveLength(20);
        string.Concat(lines).Should().Be(text);
    }

    // -----------------------------------------------------------------------
    // Normalize
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("  앞뒤  공백  ", "앞뒤 공백")]
    [InlineData("여러\n줄\r\n텍스트", "여러 줄 텍스트")]
    [InlineData("탭\t으로\t구분", "탭 으로 구분")]
    [InlineData("연속    공백", "연속 공백")]
    public void Normalize_collapses_whitespace(string input, string expected)
    {
        KoreanLineBreaker.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_strips_control_characters_that_would_corrupt_a_cue()
    {
        KoreanLineBreaker.Normalize("정상\u0001텍스트").Should().Be("정상텍스트");
    }

    [Fact]
    public void Normalize_of_blank_input_is_empty()
    {
        KoreanLineBreaker.Normalize("   \t \n ").Should().BeEmpty();
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = KoreanLineBreaker.Normalize("  여러   가지\n공백\t문자  ");

        KoreanLineBreaker.Normalize(once).Should().Be(once);
    }
}

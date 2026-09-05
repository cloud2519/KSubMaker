using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// The sidecar ranking: 설정된 원본 언어 → ja → en → 기타 → 표기 없음.
///
/// Pinned hard because the rule is invisible at run time — the user drops a folder and gets a
/// subtitle, with no screen that says which of the four .srt files beside the video was read.
/// </summary>
public sealed class ExternalSubtitleSelectorTests
{
    private const string Video = @"D:\videos\movie.mp4";

    private static string? Pick(string[] candidates, string? sourceLanguage = null) =>
        ExternalSubtitleSelector.Choose(Video, candidates, sourceLanguage)?.Path;

    // -----------------------------------------------------------------------
    // the ranking itself
    // -----------------------------------------------------------------------

    [Fact]
    public void Japanese_beats_english_which_beats_other_languages_which_beat_an_untagged_file()
    {
        string[] all =
        [
            @"D:\videos\movie.srt",
            @"D:\videos\movie.fr.srt",
            @"D:\videos\movie.en.srt",
            @"D:\videos\movie.ja.srt"
        ];

        Pick(all).Should().Be(@"D:\videos\movie.ja.srt");
        Pick(all[..3]).Should().Be(@"D:\videos\movie.en.srt");
        Pick(all[..2]).Should().Be(@"D:\videos\movie.fr.srt");
        Pick(all[..1]).Should().Be(@"D:\videos\movie.srt");
    }

    [Fact]
    public void The_configured_source_language_outranks_even_japanese()
    {
        string[] candidates = [@"D:\videos\movie.ja.srt", @"D:\videos\movie.fr.srt"];

        Pick(candidates, "fr").Should().Be(@"D:\videos\movie.fr.srt");
        Pick(candidates, "auto").Should().Be(@"D:\videos\movie.ja.srt", "auto falls back to the fixed order");
        Pick(candidates, null).Should().Be(@"D:\videos\movie.ja.srt");
    }

    [Fact]
    public void An_untagged_file_is_used_when_it_is_the_only_one()
    {
        var choice = ExternalSubtitleSelector.Choose(Video, [@"D:\videos\movie.srt"]);

        choice.Should().NotBeNull();
        choice!.Language.Should().BeNull("no tag is a real answer, not a rejection");
    }

    // -----------------------------------------------------------------------
    // what must never be chosen
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\videos\movie.ko.srt")]
    [InlineData(@"D:\videos\movie.kor.srt")]
    [InlineData(@"D:\videos\movie.korean.srt")]
    [InlineData(@"D:\videos\movie.ko-KR.srt")]
    [InlineData(@"D:\videos\movie.ko.forced.srt")]
    public void A_korean_sidecar_is_never_the_source(string korean)
    {
        // It is this pipeline's own output. Translating it would round-trip Korean through Korean
        // and then overwrite the good subtitle with the result.
        Pick([korean]).Should().BeNull();
        Pick([korean, @"D:\videos\movie.en.srt"]).Should().Be(@"D:\videos\movie.en.srt");
    }

    [Theory]
    [InlineData(@"D:\videos\movie.sub")]
    [InlineData(@"D:\videos\movie.idx")]
    [InlineData(@"D:\videos\movie.smi")]
    [InlineData(@"D:\videos\movie.txt")]
    public void Formats_we_cannot_turn_into_cues_are_skipped(string unreadable)
    {
        // .sub/.idx are VobSub bitmaps and would need OCR; they still count as "a subtitle exists"
        // elsewhere, which is why the readable set is narrower than VideoExtensions.Subtitle.
        Pick([unreadable]).Should().BeNull();
    }

    [Fact]
    public void No_candidates_at_all_yields_null_rather_than_throwing()
    {
        ExternalSubtitleSelector.Choose(Video, []).Should().BeNull();
        ExternalSubtitleSelector.Choose(Video, [string.Empty]).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // reading the language tag
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\videos\movie.ja.srt", "ja")]
    [InlineData(@"D:\videos\movie.jpn.srt", "ja")]
    [InlineData(@"D:\videos\movie.japanese.srt", "ja")]
    [InlineData(@"D:\videos\movie.ja-JP.srt", "ja")]
    [InlineData(@"D:\videos\movie.ja.forced.srt", "ja")]
    [InlineData(@"D:\videos\movie.forced.ja.srt", "ja")]
    [InlineData(@"D:\videos\movie.eng.sdh.srt", "en")]
    [InlineData(@"D:\videos\movie.srt", null)]
    public void The_language_tag_is_read_from_the_file_name(string path, string? expected)
    {
        ExternalSubtitleSelector.Choose(Video, [path])!.Language.Should().Be(expected);
    }

    [Fact]
    public void A_tag_that_is_not_a_language_does_not_become_one()
    {
        // "forced" and "sdh" are not two-letter codes and are not in the alias table, so the file
        // reads as untagged rather than as a language called "fo".
        ExternalSubtitleSelector.Choose(Video, [@"D:\videos\movie.forced.srt"])!
            .Language.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // tie-breaking
    // -----------------------------------------------------------------------

    [Fact]
    public void Within_one_tier_srt_wins_over_a_format_that_needs_converting()
    {
        Pick([@"D:\videos\movie.ja.ass", @"D:\videos\movie.ja.srt"])
            .Should().Be(@"D:\videos\movie.ja.srt");
    }

    [Fact]
    public void Within_one_tier_the_plainer_name_wins()
    {
        Pick([@"D:\videos\movie.ja.forced.sdh.srt", @"D:\videos\movie.ja.srt"])
            .Should().Be(@"D:\videos\movie.ja.srt");
    }

    [Fact]
    public void The_order_the_candidates_arrive_in_does_not_change_the_answer()
    {
        // The directory listing order is not something we control, and a subtitle that changes
        // between two runs of the same queue would be near-impossible to explain.
        string[] forwards = [@"D:\videos\movie.ja.srt", @"D:\videos\movie.en.srt", @"D:\videos\movie.srt"];
        string[] backwards = [.. forwards.Reverse()];

        Pick(forwards).Should().Be(Pick(backwards));
    }

    // -----------------------------------------------------------------------
    // never translate a file onto itself
    // -----------------------------------------------------------------------

    [Fact]
    public void The_file_this_job_is_about_to_write_is_never_its_own_source()
    {
        // With a blank output suffix the result is movie.srt — which is also a perfectly ordinary
        // *source* name. Reading and writing the same path would replace the original subtitle with
        // a round trip of its own text. 건너뛰기 (the default conflict policy) would stop it, but
        // 덮어쓰기 is one dropdown away and the loss cannot be undone.
        string[] candidates = [@"D:\videos\movie.srt"];

        ExternalSubtitleSelector.Choose(Video, candidates, null, @"D:\videos\movie.srt")
            .Should().BeNull();

        ExternalSubtitleSelector.Choose(Video, candidates, null, @"D:\videos\movie.ko.srt")
            .Should().NotBeNull("a different output path leaves the sidecar usable");
    }

    [Fact]
    public void The_output_path_is_matched_regardless_of_casing_or_separators()
    {
        ExternalSubtitleSelector.Choose(
                Video, [@"D:\videos\movie.srt"], null, @"D:\videos\sub\..\MOVIE.SRT")
            .Should().BeNull();
    }

    [Fact]
    public void A_second_candidate_survives_when_only_one_collides_with_the_output()
    {
        var choice = ExternalSubtitleSelector.Choose(
            Video,
            [@"D:\videos\movie.srt", @"D:\videos\movie.ja.srt"],
            null,
            @"D:\videos\movie.srt");

        choice!.Path.Should().Be(@"D:\videos\movie.ja.srt");
    }
}

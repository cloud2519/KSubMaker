using FluentAssertions;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>
/// Covers "자막 후처리": merging, splitting, timing hygiene and line breaking.
///
/// The invariant that runs through every test here is that translation never moves a timecode: the
/// numbers may be nudged for readability, but they always originate from the ASR segments.
/// </summary>
public sealed class SubtitlePostProcessorTests
{
    private static readonly SubtitleFormattingOptions Defaults = new();

    private static TranscriptionSegment Segment(int id, double start, double end, string text) => new()
    {
        Id = id,
        Start = start,
        End = end,
        Text = text
    };

    private static IReadOnlyDictionary<int, string> Translations(params (int Id, string Text)[] items) =>
        items.ToDictionary(i => i.Id, i => i.Text);

    // -----------------------------------------------------------------------
    // joining
    // -----------------------------------------------------------------------

    [Fact]
    public void Segments_without_a_translation_are_dropped_rather_than_emitted_untranslated()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 3d, "first"),
                Segment(2, 4d, 7d, "second"),
                Segment(3, 8d, 11d, "third")
            ],
            Translations((1, "첫 번째 문장입니다"), (3, "세 번째 문장입니다")),
            Defaults with { MergeShortCues = false });

        cues.Should().HaveCount(2);
        cues.Select(c => c.Text).Should().NotContain(t => t.Contains("second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_translation_that_normalises_to_nothing_is_dropped()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 3d, "x")],
            Translations((1, "   \t  ")),
            Defaults);

        cues.Should().BeEmpty();
    }

    [Fact]
    public void No_translations_at_all_produces_no_cues()
    {
        SubtitlePostProcessor.Build([Segment(1, 0d, 3d, "x")], Translations(), Defaults).Should().BeEmpty();
    }

    [Fact]
    public void Build_rejects_null_arguments()
    {
        var nullSegments = () => SubtitlePostProcessor.Build(null!, Translations(), Defaults);
        var nullTranslations = () => SubtitlePostProcessor.Build([], null!, Defaults);
        var nullOptions = () => SubtitlePostProcessor.Build([], Translations(), null!);

        nullSegments.Should().Throw<ArgumentNullException>();
        nullTranslations.Should().Throw<ArgumentNullException>();
        nullOptions.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Cues_are_numbered_from_one_contiguously()
    {
        var segments = Enumerable.Range(1, 12)
            .Select(i => Segment(i, i * 4d, (i * 4d) + 3d, $"line {i}"))
            .ToArray();

        var translations = segments.ToDictionary(s => s.Id, s => $"{s.Id}번째 한국어 자막 문장입니다");

        var cues = SubtitlePostProcessor.Build(segments, translations, Defaults);

        cues.Select(c => c.Index).Should().Equal(Enumerable.Range(1, cues.Count));
    }

    // -----------------------------------------------------------------------
    // merging
    // -----------------------------------------------------------------------

    [Fact]
    public void A_very_short_cue_is_merged_into_its_neighbour()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 0.4d, "a"),
                Segment(2, 0.6d, 3.5d, "b")
            ],
            Translations((1, "응"), (2, "그래서 어떻게 됐는데")),
            Defaults);

        cues.Should().ContainSingle();
        cues[0].Text.Replace("\n", " ", StringComparison.Ordinal).Should().Be("응 그래서 어떻게 됐는데");
        cues[0].Start.Should().Be(0d);
        cues[0].End.Should().Be(3.5d);
    }

    [Fact]
    public void Merging_never_crosses_a_pause_longer_than_a_second()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 0.4d, "a"),
                Segment(2, 5d, 8d, "b")     // 4.6 s of silence: a scene change
            ],
            Translations((1, "응"), (2, "그래서 어떻게 됐는데")),
            Defaults);

        cues.Should().HaveCount(2);
    }

    [Fact]
    public void Merging_stops_before_exceeding_the_maximum_cue_duration()
    {
        var options = Defaults with { MaxCueDurationSeconds = 3d };

        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 0.4d, "a"),
                Segment(2, 0.6d, 6d, "b")
            ],
            Translations((1, "응"), (2, "그래서 어떻게 됐는데")),
            options);

        cues.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Merging_can_be_switched_off()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 0.4d, "a"),
                Segment(2, 0.6d, 3.5d, "b")
            ],
            Translations((1, "응"), (2, "그래서 어떻게 됐는데")),
            Defaults with { MergeShortCues = false });

        cues.Should().HaveCount(2);
    }

    // -----------------------------------------------------------------------
    // splitting
    // -----------------------------------------------------------------------

    [Fact]
    public void A_cue_whose_text_cannot_fit_in_two_lines_is_split_into_several_cues()
    {
        var longText =
            "첫 번째 문장입니다. 두 번째 문장도 이어집니다. 세 번째 문장까지 계속됩니다. " +
            "네 번째 문장으로 마무리합니다.";

        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 6d, "x")],
            Translations((1, longText)),
            Defaults with { MergeShortCues = false });

        cues.Should().HaveCountGreaterThan(1);
        cues.Should().OnlyContain(c => c.Text.Replace("\n", " ", StringComparison.Ordinal).Length <= Defaults.MaxCharsPerCue + 2);
    }

    [Fact]
    public void A_split_distributes_the_original_duration_and_never_invents_time()
    {
        var longText = string.Join(" ", Enumerable.Range(1, 24).Select(i => $"단어{i}"));

        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 10d, 20d, "x")],
            Translations((1, longText)),
            Defaults with { MergeShortCues = false });

        cues.Should().HaveCountGreaterThan(1);
        cues[0].Start.Should().BeGreaterThanOrEqualTo(10d);

        // The only legitimate overshoot is the documented minimum-duration nudge on the last piece.
        cues[^1].End.Should().BeLessThanOrEqualTo(20d + Defaults.MinCueDurationSeconds + 1e-9);
    }

    [Fact]
    public void Splitting_prefers_a_sentence_boundary()
    {
        var text = "여기서 첫 번째 문장이 확실하게 끝납니다. 그리고 여기서부터 완전히 새로운 문장이 시작됩니다.";

        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 8d, "x")],
            Translations((1, text)),
            Defaults with { MergeShortCues = false });

        cues.Should().HaveCountGreaterThan(1);
        cues[0].Text.Replace("\n", " ", StringComparison.Ordinal).Should().EndWith("끝납니다.");
    }

    // -----------------------------------------------------------------------
    // timing hygiene
    // -----------------------------------------------------------------------

    [Fact]
    public void Overlapping_source_segments_are_separated_by_at_least_the_minimum_gap()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 5d, "a"),
                Segment(2, 3d, 8d, "b"),     // starts before the previous one ends
                Segment(3, 7d, 12d, "c")
            ],
            Translations(
                (1, "첫 번째 자막 문장입니다"),
                (2, "두 번째 자막 문장입니다"),
                (3, "세 번째 자막 문장입니다")),
            Defaults with { MergeShortCues = false });

        AssertNoOverlap(cues, Defaults);
    }

    [Fact]
    public void A_reversed_segment_is_repaired_instead_of_producing_a_negative_duration()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 5d, 4d, "a")],
            Translations((1, "시간이 뒤집힌 자막")),
            Defaults);

        cues.Should().ContainSingle();
        cues[0].End.Should().BeGreaterThan(cues[0].Start);
    }

    [Fact]
    public void A_short_cue_is_stretched_to_the_minimum_duration_when_there_is_room()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 0.2d, "a")],
            Translations((1, "짧지만 읽을 수 있는 자막")),
            Defaults with { MergeShortCues = false });

        cues.Should().ContainSingle();
        cues[0].Duration.Should().BeApproximately(Defaults.MinCueDurationSeconds, 1e-6);
    }

    [Fact]
    public void A_long_cue_is_capped_at_the_maximum_duration()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 60d, "a")],
            Translations((1, "아주 오래 표시되는 자막")),
            Defaults with { MergeShortCues = false });

        cues.Should().OnlyContain(c => c.Duration <= Defaults.MaxCueDurationSeconds + 1e-9);
    }

    [Fact]
    public void A_negative_start_time_is_clamped_to_zero()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, -3d, 2d, "a")],
            Translations((1, "음수 시작 시간을 가진 자막")),
            Defaults);

        cues[0].Start.Should().BeGreaterThanOrEqualTo(0d);
    }

    [Fact]
    public void Out_of_order_segments_are_sorted_before_anything_else_happens()
    {
        var cues = SubtitlePostProcessor.Build(
            [
                Segment(3, 20d, 24d, "c"),
                Segment(1, 0d, 4d, "a"),
                Segment(2, 10d, 14d, "b")
            ],
            Translations(
                (1, "가장 먼저 나오는 자막"),
                (2, "그 다음에 나오는 자막"),
                (3, "마지막으로 나오는 자막")),
            Defaults);

        cues.Select(c => c.Start).Should().BeInAscendingOrder();
        cues[0].Text.Should().Contain("가장 먼저");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(250)]
    public void The_configured_minimum_gap_is_honoured(int gapMilliseconds)
    {
        var options = Defaults with { MinCueGapMilliseconds = gapMilliseconds, MergeShortCues = false };

        var cues = SubtitlePostProcessor.Build(
            [
                Segment(1, 0d, 4d, "a"),
                Segment(2, 4d, 8d, "b"),
                Segment(3, 8d, 12d, "c")
            ],
            Translations(
                (1, "첫 번째 자막 문장입니다"),
                (2, "두 번째 자막 문장입니다"),
                (3, "세 번째 자막 문장입니다")),
            options);

        AssertNoOverlap(cues, options);
    }

    [Fact]
    public void A_realistic_transcript_comes_out_with_every_timing_invariant_intact()
    {
        var random = new Random(20260802);

        var segments = new List<TranscriptionSegment>();
        var translations = new Dictionary<int, string>();
        var cursor = 0d;

        for (var i = 1; i <= 120; i++)
        {
            var duration = 0.2d + (random.NextDouble() * 6d);
            var start = cursor - (random.NextDouble() < 0.15d ? 0.4d : 0d); // deliberate overlaps
            var end = start + duration;

            segments.Add(Segment(i, start, end, $"source {i}"));
            translations[i] = string.Join(" ", Enumerable.Range(0, 1 + random.Next(14)).Select(w => $"낱말{w}"));

            cursor = end + (random.NextDouble() * 1.5d);
        }

        var cues = SubtitlePostProcessor.Build(segments, translations, Defaults);

        cues.Should().NotBeEmpty();
        AssertNoOverlap(cues, Defaults);

        cues.Should().OnlyContain(c => c.End > c.Start, "no cue may be reversed or zero-length");
        cues.Should().OnlyContain(c => c.Start >= 0d);
        cues.Should().OnlyContain(c => c.Duration <= Defaults.MaxCueDurationSeconds + 1e-9);
        cues.Should().OnlyContain(c => c.Lines.Count <= Defaults.MaxLinesPerCue);
        cues.Should().OnlyContain(c => c.Lines.All(l => l.Trim().Length > 0));
        cues.Select(c => c.Index).Should().Equal(Enumerable.Range(1, cues.Count));
    }

    // -----------------------------------------------------------------------
    // line breaking
    // -----------------------------------------------------------------------

    [Fact]
    public void No_cue_ever_exceeds_the_configured_number_of_lines()
    {
        var options = Defaults with { MaxLinesPerCue = 2, MaxCharsPerLine = 22 };

        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 6d, "x")],
            Translations((1, "아주 긴 한국어 문장을 두 줄로 나누어 표시해야 하는 상황입니다")),
            options);

        cues.Should().OnlyContain(c => c.Lines.Count <= 2);
    }

    [Fact]
    public void A_single_line_cue_stays_on_one_line()
    {
        var cues = SubtitlePostProcessor.Build(
            [Segment(1, 0d, 3d, "x")],
            Translations((1, "짧은 문장")),
            Defaults);

        cues[0].Lines.Should().ContainSingle();
    }

    // -----------------------------------------------------------------------
    // options
    // -----------------------------------------------------------------------

    [Fact]
    public void Options_are_derived_from_AppSettings_with_sane_floors()
    {
        var options = SubtitleFormattingOptions.From(new AppSettings
        {
            MaxLinesPerCue = 0,
            MaxCharsPerLine = 1,
            MinCueDurationSeconds = 0d,
            MaxCueDurationSeconds = -5d,
            MinCueGapMilliseconds = -1,
            MergeShortCues = false
        });

        options.MaxLinesPerCue.Should().Be(1);
        options.MaxCharsPerLine.Should().Be(8);
        options.MinCueDurationSeconds.Should().Be(0.1d);
        options.MaxCueDurationSeconds.Should().BeGreaterThanOrEqualTo(0d);
        options.MinCueGapMilliseconds.Should().Be(0);
        options.MergeShortCues.Should().BeFalse();
    }

    [Fact]
    public void MinGapSeconds_and_MaxCharsPerCue_are_derived_consistently()
    {
        var options = new SubtitleFormattingOptions { MinCueGapMilliseconds = 250, MaxLinesPerCue = 3, MaxCharsPerLine = 20 };

        options.MinGapSeconds.Should().Be(0.25d);
        options.MaxCharsPerCue.Should().Be(60);
    }

    private static void AssertNoOverlap(IReadOnlyList<SubtitleCue> cues, SubtitleFormattingOptions options)
    {
        for (var i = 1; i < cues.Count; i++)
        {
            cues[i].Start.Should().BeGreaterThanOrEqualTo(
                cues[i - 1].End + options.MinGapSeconds - 1e-9,
                $"cue {i + 1} must start at least {options.MinCueGapMilliseconds} ms after cue {i} ends");
        }
    }
}

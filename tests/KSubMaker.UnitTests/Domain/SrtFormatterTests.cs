using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "SRT 시간 형식" and "SRT 인덱스 생성".</summary>
public sealed class SrtFormatterTests
{
    private static SubtitleCue Cue(int index, double start, double end, params string[] lines) => new()
    {
        Index = index,
        Start = start,
        End = end,
        Lines = lines
    };

    // -----------------------------------------------------------------------
    // FormatTimestamp
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0d, "00:00:00,000")]
    [InlineData(0.001d, "00:00:00,001")]
    [InlineData(0.5d, "00:00:00,500")]
    [InlineData(0.999d, "00:00:00,999")]
    [InlineData(1d, "00:00:01,000")]
    [InlineData(59.999d, "00:00:59,999")]
    [InlineData(60d, "00:01:00,000")]
    [InlineData(3599.999d, "00:59:59,999")]
    [InlineData(3600d, "01:00:00,000")]
    [InlineData(3723.456d, "01:02:03,456")]
    public void FormatTimestamp_renders_hours_minutes_seconds_and_milliseconds(double seconds, string expected)
    {
        SrtFormatter.FormatTimestamp(seconds).Should().Be(expected);
    }

    [Theory]
    [InlineData(86_400d, "24:00:00,000")]
    [InlineData(90_061.5d, "25:01:01,500")]
    [InlineData(360_000d, "100:00:00,000")]
    public void FormatTimestamp_keeps_counting_past_twenty_four_hours(double seconds, string expected)
    {
        // SRT hours are not a clock: a 30-hour recording must render as 30:xx, never wrap to 06:xx.
        SrtFormatter.FormatTimestamp(seconds).Should().Be(expected);
    }

    [Theory]
    [InlineData(-0.001d)]
    [InlineData(-1d)]
    [InlineData(-100_000d)]
    public void FormatTimestamp_clamps_negative_input_to_zero(double seconds)
    {
        SrtFormatter.FormatTimestamp(seconds).Should().Be("00:00:00,000");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FormatTimestamp_treats_non_finite_input_as_zero(double seconds)
    {
        SrtFormatter.FormatTimestamp(seconds).Should().Be("00:00:00,000");
    }

    [Theory]
    [InlineData(0.9994d, "00:00:00,999")]
    [InlineData(0.99949d, "00:00:00,999")]
    [InlineData(0.9995d, "00:00:01,000")]
    [InlineData(3.0004d, "00:00:03,000")]
    [InlineData(3.0005d, "00:00:03,001")]
    public void FormatTimestamp_rounds_half_away_from_zero_at_the_millisecond_boundary(double seconds, string expected)
    {
        SrtFormatter.FormatTimestamp(seconds).Should().Be(expected);
    }

    [Fact]
    public void FormatTimestamp_is_always_exactly_twelve_characters_below_ten_hours()
    {
        for (var seconds = 0d; seconds < 36_000d; seconds += 977.3d)
        {
            SrtFormatter.FormatTimestamp(seconds).Should().MatchRegex(@"^\d{2}:\d{2}:\d{2},\d{3}$");
        }
    }

    // -----------------------------------------------------------------------
    // TryParseTimestamp
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("00:00:00,000", 0d)]
    [InlineData("00:00:00,500", 0.5d)]
    [InlineData("01:00:00,000", 3600d)]
    [InlineData("01:02:03,456", 3723.456d)]
    [InlineData("25:01:01,500", 90_061.5d)]
    [InlineData("00:00:03", 3d)]
    [InlineData("  01:02:03,456  ", 3723.456d)]
    public void TryParseTimestamp_reads_the_comma_form(string value, double expected)
    {
        SrtFormatter.TryParseTimestamp(value, out var seconds).Should().BeTrue();
        seconds.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("01:02:03.456", 3723.456d)]
    [InlineData("00:00:00.001", 0.001d)]
    public void TryParseTimestamp_also_accepts_a_dot_separator(string value, double expected)
    {
        SrtFormatter.TryParseTimestamp(value, out var seconds).Should().BeTrue();
        seconds.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a timestamp")]
    [InlineData("01:02")]
    [InlineData("01:02:03:04")]
    [InlineData("aa:bb:cc,ddd")]
    [InlineData("01:02:xx,456")]
    [InlineData("01:02:03,xyz")]
    public void TryParseTimestamp_rejects_malformed_input_without_throwing(string value)
    {
        SrtFormatter.TryParseTimestamp(value, out var seconds).Should().BeFalse();
        seconds.Should().Be(0d);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.001d)]
    [InlineData(12.345d)]
    [InlineData(3723.456d)]
    [InlineData(90_061.5d)]
    public void FormatTimestamp_and_TryParseTimestamp_round_trip(double seconds)
    {
        var text = SrtFormatter.FormatTimestamp(seconds);

        SrtFormatter.TryParseTimestamp(text, out var parsed).Should().BeTrue();
        parsed.Should().BeApproximately(seconds, 0.0005d);
    }

    // -----------------------------------------------------------------------
    // Write
    // -----------------------------------------------------------------------

    [Fact]
    public void Write_renumbers_from_one_even_when_the_input_indexes_are_wrong()
    {
        var srt = SrtFormatter.Write(
        [
            Cue(97, 0d, 1d, "첫 번째"),
            Cue(97, 1.5d, 2.5d, "두 번째"),
            Cue(0, 3d, 4d, "세 번째"),
            Cue(-12, 4.5d, 5.5d, "네 번째")
        ]);

        IndexesOf(srt).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Write_keeps_the_supplied_order_and_ignores_the_index_field_entirely()
    {
        var srt = SrtFormatter.Write(
        [
            Cue(5, 10d, 11d, "나중"),
            Cue(1, 0d, 1d, "먼저")
        ]);

        var blocks = Blocks(srt);
        blocks[0].Should().Contain("나중").And.Contain("00:00:10,000");
        blocks[1].Should().Contain("먼저").And.Contain("00:00:00,000");
    }

    [Fact]
    public void Write_skips_cues_whose_lines_are_all_blank_without_burning_an_index()
    {
        var srt = SrtFormatter.Write(
        [
            Cue(1, 0d, 1d, "보이는 자막"),
            Cue(2, 1d, 2d, "   ", "\t"),
            Cue(3, 2d, 3d),
            Cue(4, 3d, 4d, "두 번째로 보이는 자막")
        ]);

        IndexesOf(srt).Should().Equal(1, 2);
        srt.Should().NotContain("00:00:01,000 -->");
        srt.Should().Contain("보이는 자막").And.Contain("두 번째로 보이는 자막");
    }

    [Fact]
    public void Write_drops_blank_lines_but_keeps_the_remaining_ones_in_a_cue()
    {
        var srt = SrtFormatter.Write([Cue(1, 0d, 1d, "첫 줄", "   ", "둘째 줄")]);

        srt.Should().Be("1\n00:00:00,000 --> 00:00:01,000\n첫 줄\n둘째 줄\n\n");
    }

    [Fact]
    public void Write_emits_the_arrow_separator_and_a_blank_line_between_cues()
    {
        var srt = SrtFormatter.Write(
        [
            Cue(1, 0d, 1.25d, "하나"),
            Cue(2, 2d, 3.5d, "둘")
        ]);

        srt.Should().Be(
            "1\n00:00:00,000 --> 00:00:01,250\n하나\n\n" +
            "2\n00:00:02,000 --> 00:00:03,500\n둘\n\n");
    }

    [Fact]
    public void Write_trims_trailing_whitespace_on_every_line()
    {
        var srt = SrtFormatter.Write([Cue(1, 0d, 1d, "끝에 공백   ")]);

        srt.Should().Contain("끝에 공백\n");
        srt.Should().NotContain("끝에 공백   ");
    }

    [Fact]
    public void Write_returns_an_empty_string_for_no_cues()
    {
        SrtFormatter.Write([]).Should().BeEmpty();
    }

    [Fact]
    public void Write_produces_contiguous_indexes_for_a_large_cue_list()
    {
        var cues = Enumerable.Range(0, 500)
            .Select(i => Cue(999 - i, i * 2d, (i * 2d) + 1.5d, $"자막 {i}"))
            .ToArray();

        IndexesOf(SrtFormatter.Write(cues)).Should().Equal(Enumerable.Range(1, 500));
    }

    [Fact]
    public void ToWindowsLineEndings_converts_every_newline_exactly_once()
    {
        var srt = SrtFormatter.Write([Cue(1, 0d, 1d, "하나"), Cue(2, 1.5d, 2d, "둘")]);

        var windows = SrtFormatter.ToWindowsLineEndings(srt);

        windows.Should().NotContain("\r\r\n");
        windows.Replace("\r\n", "\n").Should().Be(srt);
        windows.Count(c => c == '\r').Should().Be(srt.Count(c => c == '\n'));
    }

    [Fact]
    public void ToWindowsLineEndings_is_idempotent()
    {
        var once = SrtFormatter.ToWindowsLineEndings("a\nb\r\nc");

        SrtFormatter.ToWindowsLineEndings(once).Should().Be(once);
    }

    private static int[] IndexesOf(string srt) =>
        Blocks(srt)
            .Select(b => int.Parse(b.Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    private static string[] Blocks(string srt) =>
        srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
}

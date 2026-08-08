using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>Structural checks on a real SRT file produced by the pipeline.</summary>
public static partial class SrtAssertions
{
    [GeneratedRegex(@"^(?<start>\d{2,}:\d{2}:\d{2},\d{3}) --> (?<end>\d{2,}:\d{2}:\d{2},\d{3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimingPattern();

    public sealed record ParsedCue(int Index, double Start, double End, IReadOnlyList<string> Lines);

    public static void AssertIsWellFormedKoreanSrt(string path, int maxLinesPerCue = 2)
    {
        File.Exists(path).Should().BeTrue($"the pipeline must have written {path}");

        var bytes = File.ReadAllBytes(path);

        bytes.Should().HaveCountGreaterThan(3);
        bytes.Take(3).Should().Equal([0xEF, 0xBB, 0xBF],
            "legacy Windows players fall back to the ANSI code page without a UTF-8 BOM");

        var text = File.ReadAllText(path);

        text.Should().Contain("\r\n", "SRT files are conventionally CRLF");
        Regex.Matches(text, "(?<!\r)\n").Should().BeEmpty("every newline must be part of a CRLF pair");

        var cues = Parse(text);

        cues.Should().NotBeEmpty();
        cues.Select(c => c.Index).Should().Equal(Enumerable.Range(1, cues.Count),
            "indexes must be contiguous and start at 1");

        foreach (var cue in cues)
        {
            cue.End.Should().BeGreaterThan(cue.Start, $"cue {cue.Index} must have a positive duration");
            cue.Start.Should().BeGreaterThanOrEqualTo(0d);
            cue.Lines.Should().NotBeEmpty($"cue {cue.Index} must have text");
            cue.Lines.Should().HaveCountLessThanOrEqualTo(maxLinesPerCue);
            cue.Lines.Should().OnlyContain(l => l.Trim().Length > 0);
        }

        for (var i = 1; i < cues.Count; i++)
        {
            cues[i].Start.Should().BeGreaterThanOrEqualTo(cues[i - 1].End - 1e-6,
                $"cue {i + 1} must not overlap cue {i}");
        }
    }

    public static IReadOnlyList<ParsedCue> Parse(string text)
    {
        var cues = new List<ParsedCue>();

        var blocks = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('﻿')
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.None)
                .Where(l => l.Length > 0)
                .ToArray();

            lines.Length.Should().BeGreaterThanOrEqualTo(3, $"an SRT block needs an index, a timing line and text:\n{block}");

            var index = int.Parse(lines[0], CultureInfo.InvariantCulture);

            var timing = TimingPattern().Match(lines[1]);
            timing.Success.Should().BeTrue($"'{lines[1]}' is not a valid SRT timing line");

            SrtFormatter.TryParseTimestamp(timing.Groups["start"].Value, out var start).Should().BeTrue();
            SrtFormatter.TryParseTimestamp(timing.Groups["end"].Value, out var end).Should().BeTrue();

            cues.Add(new ParsedCue(index, start, end, lines[2..]));
        }

        return cues;
    }
}

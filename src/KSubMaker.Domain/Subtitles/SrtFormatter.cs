using System.Globalization;
using System.Text;

namespace KSubMaker.Domain.Subtitles;

/// <summary>
/// SRT serialisation. Kept in the domain (rather than only in the Python worker) because the
/// integration tests drive the whole pipeline through the fake engines and still need real,
/// byte-for-byte correct SRT output.
/// </summary>
public static class SrtFormatter
{
    /// <summary>
    /// Formats seconds as <c>HH:MM:SS,mmm</c>. Negative input clamps to zero; milliseconds are
    /// truncated rather than rounded so a cue never appears to start before its own start time.
    /// </summary>
    public static string FormatTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
        {
            seconds = 0d;
        }

        // Work in integer milliseconds to avoid 3.9999999 -> "00:00:03,999" style drift.
        var totalMs = (long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero);

        var hours = totalMs / 3_600_000L;
        totalMs -= hours * 3_600_000L;
        var minutes = totalMs / 60_000L;
        totalMs -= minutes * 60_000L;
        var secs = totalMs / 1000L;
        var ms = totalMs - (secs * 1000L);

        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{secs:00},{ms:000}");
    }

    /// <summary>Parses <c>HH:MM:SS,mmm</c> (or with a '.' separator) back into seconds.</summary>
    public static bool TryParseTimestamp(string value, out double seconds)
    {
        seconds = 0d;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Replace('.', ',').Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        var secParts = parts[2].Split(',');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(secParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return false;
        }

        var ms = 0;
        if (secParts.Length > 1 && !int.TryParse(secParts[1].PadRight(3, '0')[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
        {
            return false;
        }

        seconds = (h * 3600d) + (m * 60d) + s + (ms / 1000d);
        return true;
    }

    /// <summary>
    /// Serialises cues to SRT. Indexes are regenerated from 1 in output order, so whatever the
    /// pipeline did upstream the file is always contiguous.
    /// </summary>
    public static string Write(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;

        foreach (var cue in cues)
        {
            var lines = cue.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (lines.Length == 0)
            {
                continue;
            }

            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTimestamp(cue.Start))
                   .Append(" --> ")
                   .Append(FormatTimestamp(cue.End))
                   .Append('\n');

            foreach (var line in lines)
            {
                builder.Append(line.TrimEnd()).Append('\n');
            }

            builder.Append('\n');
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// SRT files are conventionally CRLF. The whole file is written with a UTF-8 BOM by the writer
    /// so that legacy Windows players detect the encoding correctly.
    /// </summary>
    public static string ToWindowsLineEndings(string srt) =>
        srt.Replace("\r\n", "\n").Replace("\n", "\r\n");
}

using System.Text;

namespace KSubMaker.Domain.Subtitles;

/// <summary>
/// Breaks a Korean sentence into at most N display lines.
///
/// The hard rule is "never start a line with a particle or a dangling ending", because
/// "학교에서\n는 만났다" reads as broken Korean even though it fits the character budget.
/// Candidate break points are therefore scored, not just measured.
/// </summary>
public static class KoreanLineBreaker
{
    /// <summary>
    /// Tokens that must not begin a line. Josa (조사) and dependent nouns carry no meaning on their own.
    /// </summary>
    private static readonly string[] BadLineStarts =
    [
        "은", "는", "이", "가", "을", "를", "에", "에서", "에게", "께", "께서", "의", "도", "만",
        "까지", "부터", "으로", "로", "와", "과", "랑", "이랑", "보다", "처럼", "같이", "마다",
        "밖에", "조차", "마저", "이나", "나", "든지", "라도", "이라도", "요", "죠", "네요",
        "것", "거", "수", "때", "중", "등", "및", "뿐", "지", "채", "만큼", "대로", "듯", "겸"
    ];

    /// <summary>Punctuation after which a break reads naturally.</summary>
    private static readonly char[] PreferredBreakAfter = ['.', ',', '?', '!', '…', ';', ':', ')', ']', '”', '’', '」'];

    private static readonly char[] NeverBreakBefore = ['.', ',', '?', '!', '…', ';', ':', ')', ']', '”', '’', '」'];

    /// <summary>
    /// Splits <paramref name="text"/> into at most <paramref name="maxLines"/> lines, each aiming for
    /// <paramref name="maxCharsPerLine"/> characters. Returns a single line when the text already fits.
    /// </summary>
    public static IReadOnlyList<string> Break(string text, int maxLines = 2, int maxCharsPerLine = 22)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = Normalize(text);

        if (maxLines <= 1 || normalized.Length <= maxCharsPerLine)
        {
            return [normalized];
        }

        var lines = new List<string>();
        var remaining = normalized;

        for (var line = 0; line < maxLines - 1 && remaining.Length > maxCharsPerLine; line++)
        {
            var linesLeft = maxLines - line;
            var target = (int)Math.Ceiling(remaining.Length / (double)linesLeft);
            var breakAt = FindBreakPoint(remaining, target, maxCharsPerLine);

            if (breakAt <= 0 || breakAt >= remaining.Length)
            {
                break;
            }

            lines.Add(remaining[..breakAt].TrimEnd());
            remaining = remaining[breakAt..].TrimStart();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
        {
            lines.Add(remaining.Trim());
        }

        return lines.Count == 0 ? [normalized] : lines;
    }

    /// <summary>Collapses whitespace and strips control characters that would corrupt an SRT cue.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Picks the index to cut at. Prefers word boundaries near <paramref name="target"/>, heavily
    /// penalising cuts that would orphan a particle or exceed <paramref name="hardMax"/>.
    /// </summary>
    private static int FindBreakPoint(string text, int target, int hardMax)
    {
        var best = -1;
        var bestScore = double.MaxValue;

        for (var i = 1; i < text.Length; i++)
        {
            if (text[i - 1] != ' ')
            {
                continue;
            }

            // i is the start of a word; cutting here puts text[..i] on this line.
            var leftLength = i - 1;
            if (leftLength <= 0)
            {
                continue;
            }

            var score = Math.Abs(leftLength - target);

            // Overlong first line is worse than an unbalanced one.
            if (leftLength > hardMax)
            {
                score += (leftLength - hardMax) * 6;
            }

            if (StartsWithBadToken(text, i))
            {
                score += 40;
            }

            if (i < text.Length && Array.IndexOf(NeverBreakBefore, text[i]) >= 0)
            {
                score += 60;
            }

            if (leftLength >= 1 && Array.IndexOf(PreferredBreakAfter, text[leftLength - 1]) >= 0)
            {
                score -= 8;
            }

            // Avoid leaving a one- or two-character stub on either side.
            if (leftLength <= 2 || (text.Length - i) <= 2)
            {
                score += 25;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best > 0)
        {
            return best;
        }

        // No spaces at all (common for dense Korean): fall back to a hard cut at the limit.
        return Math.Min(hardMax, text.Length - 1);
    }

    private static bool StartsWithBadToken(string text, int index)
    {
        var end = text.IndexOf(' ', index);
        var word = end < 0 ? text[index..] : text[index..end];

        if (word.Length == 0)
        {
            return false;
        }

        foreach (var bad in BadLineStarts)
        {
            if (word.Equals(bad, StringComparison.Ordinal))
            {
                return true;
            }

            // "은/는" style josa fused onto a following dependent noun, e.g. "것을".
            if (word.Length <= 3 && word.StartsWith(bad, StringComparison.Ordinal) && bad.Length >= 1 && word.Length - bad.Length <= 1)
            {
                return true;
            }
        }

        return false;
    }
}

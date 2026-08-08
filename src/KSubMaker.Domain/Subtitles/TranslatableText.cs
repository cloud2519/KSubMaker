using System.Text;

namespace KSubMaker.Domain.Subtitles;

/// <summary>
/// Answers one question for both pipelines: <b>is there anything here for a translation engine to
/// do?</b>
///
/// <para>Japanese subtitles are full of cues that carry no words at all — <c>♪</c> for a song sting,
/// <c>～</c> for a drawn-out vowel, <c>…</c>, <c>。</c>, <c>！？</c>, <c>＊</c>, a lone bracket pair.
/// NLLB deterministically returns an empty string for those, the response validator counts the blank
/// as a corrupt response, and one such cue was enough to discard 134 seconds of finished work and
/// fail the whole job. Cues that fail this test never reach the engine; their source text is carried
/// through unchanged so the cue still appears in the SRT with its original timing.</para>
///
/// <para>The test is deliberately Unicode-wide. An ASCII-only "does it contain a letter" check would
/// classify every Japanese, Korean, Cyrillic, Greek, Arabic or Thai line as untranslatable, which is
/// the precise opposite of what is wanted here.</para>
///
/// <para><c>worker/ksubmaker_worker/batching.py::has_translatable_content</c> is the Python half of
/// this rule; <c>TranslatableTextParityTests</c> replays a shared fixture through both so the two
/// implementations cannot drift.</para>
/// </summary>
public static class TranslatableText
{
    /// <summary>
    /// True when <paramref name="text"/> contains at least one letter or decimal digit in any
    /// script. Symbols, punctuation, marks, separators and control characters do not count.
    /// </summary>
    public static bool HasTranslatableContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Runes rather than chars: a letter outside the BMP is a surrogate pair, and neither half is
        // a letter on its own. Rare CJK-extension kanji do turn up in Japanese subtitles.
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return true;
            }
        }

        return false;
    }
}

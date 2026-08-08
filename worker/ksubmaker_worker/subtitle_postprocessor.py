"""Turns translated text plus the original timings into display-ready cues.

A faithful port of ``KSubMaker.Domain.Subtitles.SubtitlePostProcessor``, ``KoreanLineBreaker`` and
``SegmentSplitter``. The C# integration tests and this worker must produce the same file for the
same input, so the algorithms — including the line-break scoring weights — are kept identical.

Invariant that drives the whole design: **translation never moves a timecode.** Timings come only
from the ASR segments; this module may merge, split or nudge them for readability, but the numbers
always originate from the audio, never from a language model.
"""

from __future__ import annotations

import unicodedata
from dataclasses import dataclass, replace
from typing import Any, Iterable, Mapping, Sequence

MAX_MERGE_GAP_SECONDS = 1.0

#: Exactly what .NET's ``char.IsWhiteSpace`` accepts. Python's ``str.isspace()`` additionally
#: returns True for U+001C..U+001F, which .NET classifies as control characters and *drops*; using
#: Python's definition would turn a stray file separator into a visible space.
_WHITESPACE = frozenset(
    "\t\n\v\f\r \x85\xa0     　"
) | frozenset(chr(code) for code in range(0x2000, 0x200B))


def _int_setting(settings: Mapping[str, Any], key: str, default: int) -> int:
    """Read an int setting, treating an absent or null value as "use the default".

    A present value — including a nonsensical one — is returned as-is so the caller's clamp can
    apply, which is what the C# ``Math.Max`` calls do to the record's own defaults.
    """
    value = settings.get(key)
    if value is None:
        return default
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _float_setting(settings: Mapping[str, Any], key: str, default: float) -> float:
    value = settings.get(key)
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


@dataclass(frozen=True)
class FormattingOptions:
    max_lines_per_cue: int = 2
    max_chars_per_line: int = 22
    min_cue_duration_seconds: float = 1.0
    max_cue_duration_seconds: float = 7.0
    min_cue_gap_milliseconds: int = 50
    merge_short_cues: bool = True

    @property
    def min_gap_seconds(self) -> float:
        return self.min_cue_gap_milliseconds / 1000.0

    @property
    def max_chars_per_cue(self) -> int:
        return self.max_lines_per_cue * self.max_chars_per_line

    @classmethod
    def from_settings(cls, settings: Mapping[str, Any]) -> "FormattingOptions":
        """Same clamps as ``SubtitleFormattingOptions.From(AppSettings)``."""
        min_duration = max(0.1, _float_setting(settings, "minCueDurationSeconds", 1.0))
        merge = settings.get("mergeShortCues")

        return cls(
            max_lines_per_cue=max(1, _int_setting(settings, "maxLinesPerCue", 2)),
            max_chars_per_line=max(8, _int_setting(settings, "maxCharsPerLine", 22)),
            min_cue_duration_seconds=min_duration,
            max_cue_duration_seconds=max(
                min_duration, _float_setting(settings, "maxCueDurationSeconds", 7.0)
            ),
            min_cue_gap_milliseconds=max(0, _int_setting(settings, "minCueGapMilliseconds", 50)),
            merge_short_cues=True if merge is None else bool(merge),
        )


@dataclass(frozen=True)
class Cue:
    """A finished subtitle cue ready to be serialised to SRT."""

    index: int
    start: float
    end: float
    lines: tuple[str, ...]

    @property
    def duration(self) -> float:
        return self.end - self.start

    @property
    def text(self) -> str:
        return "\n".join(self.lines)


@dataclass(frozen=True)
class _Draft:
    start: float
    end: float
    text: str

    @property
    def duration(self) -> float:
        return self.end - self.start


# ---------------------------------------------------------------------------
# Korean line breaking
# ---------------------------------------------------------------------------

#: Josa (조사) and dependent nouns. Never allowed to start a line: "학교에서\n는 만났다" reads as
#: broken Korean even though it fits the character budget.
BAD_LINE_STARTS: tuple[str, ...] = (
    "은", "는", "이", "가", "을", "를", "에", "에서", "에게", "께", "께서", "의", "도", "만",
    "까지", "부터", "으로", "로", "와", "과", "랑", "이랑", "보다", "처럼", "같이", "마다",
    "밖에", "조차", "마저", "이나", "나", "든지", "라도", "이라도", "요", "죠", "네요",
    "것", "거", "수", "때", "중", "등", "및", "뿐", "지", "채", "만큼", "대로", "듯", "겸",
)

PREFERRED_BREAK_AFTER = ".,?!…;:)]”’」"
NEVER_BREAK_BEFORE = ".,?!…;:)]”’」"


def normalize(text: str) -> str:
    """Collapse whitespace and strip control characters that would corrupt an SRT cue.

    Ported from ``KoreanLineBreaker.Normalize``. One deliberate addition: Cf characters (BOM,
    zero-width space, RTL marks) are dropped as well as Cc. .NET's ``char.IsControl`` covers only
    Cc, but an invisible BOM sitting in the middle of a cue is a defect in a subtitle file and
    removing it cannot change what the viewer reads.
    """
    if not text:
        return ""

    out: list[str] = []
    last_was_space = False

    for ch in text:
        if ch in _WHITESPACE:
            if not last_was_space and out:
                out.append(" ")
                last_was_space = True
            continue

        if unicodedata.category(ch) in ("Cc", "Cf"):
            continue

        out.append(ch)
        last_was_space = False

    return "".join(out).strip()


def break_lines(text: str, max_lines: int = 2, max_chars_per_line: int = 22) -> list[str]:
    """Split Korean text into at most ``max_lines`` display lines."""
    if not text or not text.strip():
        return []

    normalized = normalize(text)

    if max_lines <= 1 or len(normalized) <= max_chars_per_line:
        return [normalized]

    lines: list[str] = []
    remaining = normalized

    for line_index in range(max_lines - 1):
        if len(remaining) <= max_chars_per_line:
            break

        lines_left = max_lines - line_index
        target = -(-len(remaining) // lines_left)  # ceil division
        break_at = _find_break_point(remaining, target, max_chars_per_line)

        if break_at <= 0 or break_at >= len(remaining):
            break

        lines.append(remaining[:break_at].rstrip())
        remaining = remaining[break_at:].lstrip()

    if remaining.strip():
        lines.append(remaining.strip())

    return lines or [normalized]


def _find_break_point(text: str, target: int, hard_max: int) -> int:
    """Index to cut at, scoring candidates rather than just measuring them.

    Same weights as ``KoreanLineBreaker.FindBreakPoint``: orphaning a particle costs 40, breaking
    before punctuation costs 60, an overlong first line costs 6 per character.
    """
    best = -1
    best_score = float("inf")

    for i in range(1, len(text)):
        if text[i - 1] != " ":
            continue

        left_length = i - 1
        if left_length <= 0:
            continue

        score = float(abs(left_length - target))

        if left_length > hard_max:
            score += (left_length - hard_max) * 6

        if _starts_with_bad_token(text, i):
            score += 40

        if i < len(text) and text[i] in NEVER_BREAK_BEFORE:
            score += 60

        if left_length >= 1 and text[left_length - 1] in PREFERRED_BREAK_AFTER:
            score -= 8

        if left_length <= 2 or (len(text) - i) <= 2:
            score += 25

        if score < best_score:
            best_score = score
            best = i

    if best > 0:
        return best

    # No spaces at all (common for dense Korean): hard cut at the limit.
    return min(hard_max, len(text) - 1)


def _starts_with_bad_token(text: str, index: int) -> bool:
    space = text.find(" ", index)
    word = text[index:] if space < 0 else text[index:space]

    if not word:
        return False

    for bad in BAD_LINE_STARTS:
        if word == bad:
            return True
        # A josa fused onto a following dependent noun, e.g. "것을".
        if len(word) <= 3 and word.startswith(bad) and len(word) - len(bad) <= 1:
            return True

    return False


# ---------------------------------------------------------------------------
# segment splitting (pre-translation)
# ---------------------------------------------------------------------------


def split_segments(
    segments: Sequence[Mapping[str, Any]],
    max_chars: int = 90,
    max_duration_seconds: float = 7.0,
) -> list[dict[str, Any]]:
    """Split over-long ASR segments *before* translation, using word timestamps where present.

    Doing this before translation is what makes word timestamps genuinely useful: once the text is
    Korean there is no word-level alignment left to exploit. Ids are reassigned contiguously from 1
    so downstream batching and validation stay simple.
    """
    output: list[dict[str, Any]] = []

    for segment in segments:
        text = normalize(str(segment.get("text", "") or ""))
        if not text:
            continue

        start = float(segment.get("start", 0.0) or 0.0)
        end = float(segment.get("end", 0.0) or 0.0)
        words = [w for w in (segment.get("words") or []) if isinstance(w, dict)]
        normalized = {**dict(segment), "text": text, "start": start, "end": end, "words": words}

        if len(text) <= max_chars and (end - start) <= max_duration_seconds:
            output.append(normalized)
            continue

        if len(words) > 1:
            output.extend(_split_by_words(normalized, max_chars, max_duration_seconds))
        else:
            output.extend(_split_proportionally(normalized, max_chars))

    for new_id, segment in enumerate(output, start=1):
        segment["id"] = new_id

    return output


def _split_by_words(
    segment: dict[str, Any], max_chars: int, max_duration_seconds: float
) -> list[dict[str, Any]]:
    pieces: list[dict[str, Any]] = []
    chunk: list[dict[str, Any]] = []
    length = 0

    for word in segment["words"]:
        word_text = str(word.get("word", "") or "")
        projected_length = length + len(word_text)
        projected_duration = (
            0.0 if not chunk else float(word.get("end", 0.0) or 0.0) - float(chunk[0].get("start", 0.0) or 0.0)
        )

        must_flush = bool(chunk) and (
            projected_length > max_chars or projected_duration > max_duration_seconds
        )
        # Prefer flushing right after sentence-ending punctuation once past half the budget: it
        # produces far more natural cues than a purely length-driven cut.
        wants_flush = bool(chunk) and length > max_chars // 2 and _ends_sentence(
            str(chunk[-1].get("word", "") or "")
        )

        if must_flush or wants_flush:
            pieces.append(_from_words(segment, chunk))
            chunk = []
            length = 0

        chunk.append(word)
        length += len(word_text)

    if chunk:
        pieces.append(_from_words(segment, chunk))

    return pieces


def _ends_sentence(word: str) -> bool:
    trimmed = word.rstrip()
    return bool(trimmed) and trimmed[-1] in ".?!…"


def _from_words(source: dict[str, Any], words: list[dict[str, Any]]) -> dict[str, Any]:
    text = normalize("".join(str(w.get("word", "") or "") for w in words))
    start = float(words[0].get("start", 0.0) or 0.0)
    end = max(float(words[-1].get("end", 0.0) or 0.0), start + 0.001)

    return {
        "id": source.get("id", 0),
        "start": start,
        "end": end,
        "text": text,
        "words": list(words),
    }


def _split_proportionally(segment: dict[str, Any], max_chars: int) -> list[dict[str, Any]]:
    """Fallback with no word timestamps: cut on punctuation, interpolate the time."""
    pieces: list[str] = []
    remaining = segment["text"]

    while len(remaining) > max_chars:
        cut = -1
        upper = min(max_chars, len(remaining) - 1)

        for i in range(upper, max_chars // 3, -1):
            if remaining[i - 1] in ".?!…,;":
                cut = i
                break

        if cut <= 0:
            space = remaining.rfind(" ", 0, upper + 1)
            cut = space + 1 if space > 0 else upper

        pieces.append(remaining[:cut].strip())
        remaining = remaining[cut:].lstrip()

    if remaining:
        pieces.append(remaining.strip())

    total_chars = sum(len(p) for p in pieces)
    start = float(segment["start"])
    end = float(segment["end"])
    duration = end - start
    cursor = start
    output: list[dict[str, Any]] = []

    for i, piece in enumerate(pieces):
        share = (1.0 / len(pieces)) if total_chars == 0 else len(piece) / total_chars
        piece_end = end if i == len(pieces) - 1 else min(end, cursor + duration * share)
        if piece_end <= cursor:
            piece_end = min(end, cursor + 0.001)

        output.append(
            {
                "id": segment.get("id", 0),
                "start": cursor,
                "end": piece_end,
                "text": piece,
                "words": [],
            }
        )
        cursor = piece_end

    return output


# ---------------------------------------------------------------------------
# cue building
# ---------------------------------------------------------------------------


def build_cues(
    segments: Sequence[Mapping[str, Any]],
    translations: Mapping[int, str],
    options: FormattingOptions | None = None,
) -> list[Cue]:
    """Join ASR segments to their translations by id, then merge / split / repair timings.

    Segments with no translation are dropped rather than emitted untranslated: a stray English
    line in a Korean subtitle file looks like a bug to the user, whereas a missing line reads as a
    silent moment.
    """
    opts = options or FormattingOptions()

    draft: list[_Draft] = []
    ordered = sorted(
        segments, key=lambda s: (float(s.get("start", 0.0) or 0.0), int(s.get("id", 0) or 0))
    )

    for segment in ordered:
        segment_id = int(segment.get("id", 0) or 0)
        if segment_id not in translations:
            continue

        text = normalize(translations[segment_id])
        if not text:
            continue

        start = max(0.0, float(segment.get("start", 0.0) or 0.0))
        end = max(start + 0.001, float(segment.get("end", 0.0) or 0.0))
        draft.append(_Draft(start, end, text))

    if not draft:
        return []

    if opts.merge_short_cues:
        draft = _merge_short(draft, opts)

    draft = _split_long(draft, opts)
    draft = _fix_timings(draft, opts)

    cues: list[Cue] = []
    index = 1

    for item in draft:
        lines = break_lines(item.text, opts.max_lines_per_cue, opts.max_chars_per_line)
        if not lines:
            continue
        cues.append(Cue(index=index, start=item.start, end=item.end, lines=tuple(lines)))
        index += 1

    return cues


def _merge_short(items: list[_Draft], options: FormattingOptions) -> list[_Draft]:
    """Merge a cue into its successor when it is too short to read and the two are adjacent.

    Never merges across a pause longer than one second — that is almost always a scene change.
    """
    result: list[_Draft] = []
    i = 0

    while i < len(items):
        current = items[i]

        while i + 1 < len(items):
            nxt = items[i + 1]
            gap = nxt.start - current.end
            merged_duration = nxt.end - current.start
            merged_length = len(current.text) + 1 + len(nxt.text)

            current_too_short = (
                current.duration < options.min_cue_duration_seconds or len(current.text) <= 4
            )

            if (
                not current_too_short
                or gap > MAX_MERGE_GAP_SECONDS
                or gap < 0
                or merged_duration > options.max_cue_duration_seconds
                or merged_length > options.max_chars_per_cue
            ):
                break

            current = _Draft(current.start, nxt.end, f"{current.text} {nxt.text}")
            i += 1

        result.append(current)
        i += 1

    return result


def _split_long(items: list[_Draft], options: FormattingOptions) -> list[_Draft]:
    """Split a cue that cannot fit, distributing its duration proportionally to character count."""
    result: list[_Draft] = []

    for cue in items:
        if (
            len(cue.text) <= options.max_chars_per_cue
            and cue.duration <= options.max_cue_duration_seconds
        ):
            result.append(cue)
            continue

        parts = _split_text(cue.text, options.max_chars_per_cue)
        if len(parts) <= 1:
            result.append(cue)
            continue

        total_chars = sum(len(p) for p in parts)
        cursor = cue.start

        for i, part in enumerate(parts):
            share = (1.0 / len(parts)) if total_chars == 0 else len(part) / total_chars
            duration = cue.duration * share
            end = cue.end if i == len(parts) - 1 else min(cue.end, cursor + duration)

            if end <= cursor:
                end = min(cue.end, cursor + 0.001)

            result.append(_Draft(cursor, end, part))
            cursor = end

    return result


def _split_text(text: str, max_chars: int) -> list[str]:
    parts: list[str] = []
    remaining = text

    while len(remaining) > max_chars:
        cut = _find_sentence_cut(remaining, max_chars)
        if cut <= 0 or cut >= len(remaining):
            cut = min(max_chars, len(remaining) - 1)

        parts.append(remaining[:cut].strip())
        remaining = remaining[cut:].lstrip()

    if remaining:
        parts.append(remaining.strip())

    return parts


def _find_sentence_cut(text: str, max_chars: int) -> int:
    limit = min(max_chars, len(text) - 1)

    for i in range(limit, max_chars // 2, -1):
        if text[i - 1] in ".?!…":
            return i

    for i in range(limit, max_chars // 3, -1):
        if text[i - 1] in ",;:":
            return i

    space = text.rfind(" ", 0, limit + 1)
    return space + 1 if space > 0 else limit


def _fix_timings(items: list[_Draft], options: FormattingOptions) -> list[_Draft]:
    """Enforce min/max duration, the minimum gap and strict monotonicity.

    A cue is only ever stretched into space that is actually free, so repairing one cue can never
    push the next one out of sync with the audio.
    """
    ordered = sorted(items, key=lambda c: (c.start, c.end))
    gap = options.min_gap_seconds

    for i, cue in enumerate(ordered):
        start = max(0.0, cue.start)
        end = cue.end

        if i > 0:
            previous_end = ordered[i - 1].end
            if start < previous_end + gap:
                start = previous_end + gap

        if end < start + 0.001:
            end = start + 0.001

        if end - start < options.min_cue_duration_seconds:
            desired = start + options.min_cue_duration_seconds
            ceiling = ordered[i + 1].start - gap if i + 1 < len(ordered) else float("inf")
            end = max(start + 0.001, min(desired, ceiling))

        if end - start > options.max_cue_duration_seconds:
            end = start + options.max_cue_duration_seconds

        ordered[i] = replace(cue, start=start, end=end)

    return ordered


def cues_to_dicts(cues: Iterable[Cue]) -> list[dict[str, Any]]:
    """Serialisable form, used by the finalization checkpoint."""
    return [
        {"index": c.index, "start": c.start, "end": c.end, "lines": list(c.lines)} for c in cues
    ]

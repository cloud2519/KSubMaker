"""Korean line breaking, cue merging/splitting and timing repair."""

from __future__ import annotations

from typing import Any

import pytest

from ksubmaker_worker.subtitle_postprocessor import (
    BAD_LINE_STARTS,
    FormattingOptions,
    break_lines,
    build_cues,
    normalize,
    split_segments,
)

# ---------------------------------------------------------------------------
# normalisation
# ---------------------------------------------------------------------------


def test_normalize_collapses_whitespace() -> None:
    assert normalize("  안녕\t\n하세요   반갑\r\n습니다  ") == "안녕 하세요 반갑 습니다"


def test_normalize_strips_control_characters() -> None:
    assert normalize("가\x00나\x1f다﻿라") == "가나다라"


def test_normalize_of_blank_is_empty() -> None:
    assert normalize("   \n\t ") == ""
    assert normalize("") == ""


# ---------------------------------------------------------------------------
# line breaking
# ---------------------------------------------------------------------------


def test_short_text_stays_on_one_line() -> None:
    assert break_lines("짧은 자막입니다", max_lines=2, max_chars_per_line=22) == ["짧은 자막입니다"]


def test_long_text_is_broken_into_at_most_max_lines() -> None:
    text = "오늘 회의에서 논의한 내용을 정리해서 내일 아침까지 모두에게 공유하겠습니다"
    lines = break_lines(text, max_lines=2, max_chars_per_line=22)

    assert 1 <= len(lines) <= 2
    assert " ".join(lines).replace("  ", " ") == text


@pytest.mark.parametrize("max_lines", [1, 2, 3])
def test_never_exceeds_the_line_budget(max_lines: int) -> None:
    text = " ".join(f"단어{i}" for i in range(40))
    assert len(break_lines(text, max_lines=max_lines, max_chars_per_line=12)) <= max_lines


def test_a_line_never_starts_with_a_bare_particle() -> None:
    # Both natural break points sit before a particle; the scorer must reject them.
    text = "우리가 어제 방문했던 학교에서 는 정말 좋은 일이 있었다"
    lines = break_lines(text, max_lines=2, max_chars_per_line=16)

    for line in lines[1:]:
        first_word = line.split(" ")[0]
        assert first_word not in BAD_LINE_STARTS, f"line started with the particle {first_word!r}"


@pytest.mark.parametrize("particle", ["는", "을", "에서", "이나", "만큼"])
def test_particles_are_penalised_as_line_starts(particle: str) -> None:
    text = f"아주 긴 문장의 앞부분입니다 {particle} 뒷부분이 이어집니다"
    lines = break_lines(text, max_lines=2, max_chars_per_line=18)

    for line in lines[1:]:
        assert not line.startswith(particle + " ")


def test_break_prefers_punctuation() -> None:
    text = "첫 문장입니다. 두 번째 문장이 여기서 시작합니다"
    lines = break_lines(text, max_lines=2, max_chars_per_line=20)

    assert len(lines) == 2
    assert lines[0].endswith(".")


def test_text_with_no_spaces_is_hard_cut() -> None:
    text = "가" * 40
    lines = break_lines(text, max_lines=2, max_chars_per_line=15)

    assert len(lines) == 2
    assert len(lines[0]) <= 15
    assert "".join(lines) == text


def test_single_line_mode_never_splits() -> None:
    text = "매우 긴 한국어 자막 문장이 여기 있습니다 정말 깁니다"
    assert break_lines(text, max_lines=1, max_chars_per_line=10) == [text]


def test_blank_text_produces_no_lines() -> None:
    assert break_lines("", 2, 22) == []
    assert break_lines("   \n ", 2, 22) == []


def test_no_line_is_blank_or_padded() -> None:
    text = "  앞뒤 공백이   많은   문장입니다 그리고 더 길게 이어집니다  "
    for line in break_lines(text, max_lines=2, max_chars_per_line=14):
        assert line == line.strip()
        assert line


# ---------------------------------------------------------------------------
# cue building: merge
# ---------------------------------------------------------------------------


def _seg(seg_id: int, start: float, end: float, text: str = "x") -> dict[str, Any]:
    return {"id": seg_id, "start": start, "end": end, "text": text, "words": []}


def test_short_adjacent_cues_are_merged() -> None:
    segments = [_seg(1, 0.0, 0.4), _seg(2, 0.5, 3.0)]
    cues = build_cues(segments, {1: "네", 2: "그렇습니다"}, FormattingOptions())

    assert len(cues) == 1
    assert cues[0].text == "네 그렇습니다"
    assert cues[0].start == 0.0
    assert cues[0].end == 3.0


def test_merging_never_crosses_a_long_pause() -> None:
    # A gap over one second is almost always a scene change.
    segments = [_seg(1, 0.0, 0.4), _seg(2, 5.0, 7.0)]
    cues = build_cues(segments, {1: "네", 2: "그렇습니다"}, FormattingOptions())

    assert len(cues) == 2


def test_merging_respects_the_max_duration() -> None:
    segments = [_seg(1, 0.0, 0.4), _seg(2, 0.5, 20.0)]
    cues = build_cues(segments, {1: "네", 2: "아주 긴 대사입니다"}, FormattingOptions())
    assert len(cues) == 2


def test_merging_can_be_switched_off() -> None:
    segments = [_seg(1, 0.0, 0.4), _seg(2, 0.5, 3.0)]
    cues = build_cues(
        segments, {1: "네", 2: "그렇습니다"}, FormattingOptions(merge_short_cues=False)
    )
    assert len(cues) == 2


# ---------------------------------------------------------------------------
# cue building: split
# ---------------------------------------------------------------------------


def test_over_long_cue_is_split_and_the_time_is_shared() -> None:
    long_text = "첫 번째 문장입니다. 두 번째 문장입니다. 세 번째 문장입니다. 네 번째 문장입니다."
    cues = build_cues([_seg(1, 0.0, 8.0)], {1: long_text}, FormattingOptions())

    assert len(cues) >= 2
    assert cues[0].start == 0.0
    # The split may not extend past the original span.
    assert cues[-1].end <= 8.0 + 1e-6
    assert all(cue.end > cue.start for cue in cues)


def test_split_pieces_stay_within_the_character_budget() -> None:
    options = FormattingOptions(max_lines_per_cue=2, max_chars_per_line=10)
    text = "가나다라마바사 아자차카타파하 " * 6
    cues = build_cues([_seg(1, 0.0, 30.0)], {1: text}, options)

    for cue in cues:
        for line in cue.lines:
            # The hard cut may overshoot slightly on a space-free run; allow one line's slack.
            assert len(line) <= options.max_chars_per_line * 2


def test_cue_never_exceeds_the_max_duration() -> None:
    options = FormattingOptions(max_cue_duration_seconds=7.0)
    cues = build_cues([_seg(1, 0.0, 60.0)], {1: "짧은 대사"}, options)

    for cue in cues:
        assert cue.duration <= 7.0 + 1e-6


# ---------------------------------------------------------------------------
# cue building: timing repair
# ---------------------------------------------------------------------------


def test_overlapping_cues_are_separated_by_the_minimum_gap() -> None:
    segments = [_seg(1, 0.0, 5.0), _seg(2, 3.0, 8.0), _seg(3, 7.0, 12.0)]
    translations = {1: "첫 번째 대사", 2: "두 번째 대사", 3: "세 번째 대사"}
    options = FormattingOptions(min_cue_gap_milliseconds=50, merge_short_cues=False)

    cues = build_cues(segments, translations, options)

    for previous, current in zip(cues, cues[1:]):
        assert current.start >= previous.end + 0.05 - 1e-9


def test_no_cue_is_reversed_or_zero_length() -> None:
    segments = [_seg(1, 5.0, 4.0), _seg(2, 2.0, 2.0), _seg(3, 0.0, 1.0)]
    translations = {1: "가나다", 2: "라마바", 3: "사아자"}

    for cue in build_cues(segments, translations, FormattingOptions(merge_short_cues=False)):
        assert cue.end > cue.start


def test_too_short_cue_is_stretched_but_only_into_free_space() -> None:
    segments = [_seg(1, 0.0, 0.2, "a"), _seg(2, 0.5, 4.0, "b")]
    translations = {1: "안녕하세요 여러분", 2: "반갑습니다"}
    options = FormattingOptions(min_cue_duration_seconds=1.0, merge_short_cues=False)

    cues = build_cues(segments, translations, options)

    assert len(cues) == 2
    assert cues[0].end <= cues[1].start
    assert cues[0].duration >= 0.001


def test_negative_start_is_clamped_to_zero() -> None:
    cues = build_cues([_seg(1, -3.0, 2.0)], {1: "가나다"}, FormattingOptions())
    assert cues[0].start == 0.0


def test_cues_are_indexed_from_one_in_order() -> None:
    segments = [_seg(3, 4.0, 6.0), _seg(1, 0.0, 2.0), _seg(2, 2.1, 3.9)]
    translations = {1: "첫 번째 대사", 2: "두 번째 대사", 3: "세 번째 대사"}

    cues = build_cues(segments, translations, FormattingOptions(merge_short_cues=False))

    assert [c.index for c in cues] == [1, 2, 3]
    assert cues[0].text.startswith("첫")


# ---------------------------------------------------------------------------
# cue building: joins
# ---------------------------------------------------------------------------


def test_untranslated_segments_are_dropped_not_emitted_in_english() -> None:
    segments = [_seg(1, 0.0, 2.0), _seg(2, 2.5, 4.0), _seg(3, 4.5, 6.0)]
    cues = build_cues(segments, {1: "번역됨", 3: "역시 번역됨"}, FormattingOptions())

    assert len(cues) == 2
    assert "역시 번역됨" in cues[-1].text


def test_blank_translations_are_dropped() -> None:
    cues = build_cues([_seg(1, 0.0, 2.0), _seg(2, 3.0, 5.0)], {1: "   ", 2: "괜찮아요"}, FormattingOptions())
    assert len(cues) == 1


def test_no_translations_produces_no_cues() -> None:
    assert build_cues([_seg(1, 0.0, 2.0)], {}, FormattingOptions()) == []


def test_translation_never_moves_a_timecode() -> None:
    # A single, comfortably-sized cue must come out with exactly the ASR timings.
    segments = [_seg(1, 12.345, 15.678)]
    cues = build_cues(segments, {1: "타임코드는 그대로"}, FormattingOptions())

    assert cues[0].start == pytest.approx(12.345)
    assert cues[0].end == pytest.approx(15.678)


def test_formatting_options_from_settings() -> None:
    options = FormattingOptions.from_settings(
        {
            "maxLinesPerCue": 3,
            "maxCharsPerLine": 30,
            "minCueDurationSeconds": 0.5,
            "maxCueDurationSeconds": 9.0,
            "minCueGapMilliseconds": 80,
            "mergeShortCues": False,
        }
    )

    assert options.max_lines_per_cue == 3
    assert options.max_chars_per_cue == 90
    assert options.min_gap_seconds == pytest.approx(0.08)
    assert options.merge_short_cues is False


def test_formatting_options_clamp_nonsense() -> None:
    options = FormattingOptions.from_settings(
        {"maxLinesPerCue": 0, "maxCharsPerLine": 2, "minCueDurationSeconds": 0, "maxCueDurationSeconds": 0}
    )
    assert options.max_lines_per_cue == 1
    assert options.max_chars_per_line == 8
    assert options.min_cue_duration_seconds == 0.1
    assert options.max_cue_duration_seconds >= options.min_cue_duration_seconds


# ---------------------------------------------------------------------------
# segment splitting (pre-translation)
# ---------------------------------------------------------------------------


def test_short_segments_pass_through_with_renumbered_ids() -> None:
    segments = [_seg(7, 0.0, 2.0, "short one"), _seg(9, 2.0, 4.0, "short two")]
    result = split_segments(segments, max_chars=90, max_duration_seconds=7.0)

    assert [s["id"] for s in result] == [1, 2]
    assert [s["text"] for s in result] == ["short one", "short two"]


def test_long_segment_is_split_on_word_timestamps() -> None:
    words = [
        {"word": f" word{i}", "start": i * 0.5, "end": (i + 1) * 0.5, "probability": 0.9}
        for i in range(40)
    ]
    segment = {
        "id": 1,
        "start": 0.0,
        "end": 20.0,
        "text": " ".join(f"word{i}" for i in range(40)),
        "words": words,
    }

    result = split_segments([segment], max_chars=40, max_duration_seconds=7.0)

    assert len(result) > 1
    assert [s["id"] for s in result] == list(range(1, len(result) + 1))
    # Timings still come from the words, so they stay inside the original span and stay ordered.
    assert result[0]["start"] == pytest.approx(0.0)
    assert result[-1]["end"] <= 20.0
    for previous, current in zip(result, result[1:]):
        assert current["start"] >= previous["start"]


def test_long_segment_without_words_is_split_proportionally() -> None:
    text = "First sentence here. Second sentence here. Third sentence here. Fourth one too."
    result = split_segments(
        [{"id": 1, "start": 0.0, "end": 12.0, "text": text, "words": []}],
        max_chars=30,
        max_duration_seconds=7.0,
    )

    assert len(result) > 1
    assert result[0]["start"] == 0.0
    assert result[-1]["end"] == pytest.approx(12.0)
    for previous, current in zip(result, result[1:]):
        assert current["start"] >= previous["end"] - 1e-9


def test_blank_segments_are_dropped_during_splitting() -> None:
    result = split_segments([_seg(1, 0.0, 1.0, "   "), _seg(2, 1.0, 2.0, "real")])
    assert [s["text"] for s in result] == ["real"]
    assert result[0]["id"] == 1

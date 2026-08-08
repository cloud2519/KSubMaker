"""SRT timestamps, index generation, encoding, atomic write and the conflict policy."""

from __future__ import annotations

from pathlib import Path

import pytest

from ksubmaker_worker import errors
from ksubmaker_worker.subtitle_postprocessor import Cue
from ksubmaker_worker.subtitle_writer import (
    UTF8_BOM,
    format_timestamp,
    parse_srt,
    parse_timestamp,
    resolve_output_path,
    to_windows_line_endings,
    write_srt,
    write_subtitle_file,
)

# ---------------------------------------------------------------------------
# timestamps
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("seconds", "expected"),
    [
        (0.0, "00:00:00,000"),
        (0.001, "00:00:00,001"),
        (1.5, "00:00:01,500"),
        (59.999, "00:00:59,999"),
        (60.0, "00:01:00,000"),
        (3599.999, "00:59:59,999"),
        (3600.0, "01:00:00,000"),
        (3661.25, "01:01:01,250"),
        (36000.0, "10:00:00,000"),
        (359999.999, "99:59:59,999"),
    ],
)
def test_format_timestamp(seconds: float, expected: str) -> None:
    assert format_timestamp(seconds) == expected


def test_format_timestamp_avoids_floating_point_drift() -> None:
    # 3.9999999 must not become 00:00:03,999 -- that is the drift the integer-millisecond path
    # exists to prevent.
    assert format_timestamp(3.9999999) == "00:00:04,000"
    assert format_timestamp(0.1 + 0.2) == "00:00:00,300"


def test_format_timestamp_rounds_half_away_from_zero_like_dotnet() -> None:
    # Python's built-in round() is banker's rounding and would give 00:00:00,000 here.
    assert format_timestamp(0.0005) == "00:00:00,001"
    assert format_timestamp(0.0015) == "00:00:00,002"


@pytest.mark.parametrize("bad", [-1.0, float("nan"), float("inf"), float("-inf")])
def test_format_timestamp_clamps_nonsense_to_zero(bad: float) -> None:
    assert format_timestamp(bad) == "00:00:00,000"


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("00:00:00,000", 0.0),
        ("01:02:03,004", 3723.004),
        ("01:02:03.004", 3723.004),
        ("00:00:01,5", 1.5),
    ],
)
def test_parse_timestamp(text: str, expected: float) -> None:
    assert parse_timestamp(text) == pytest.approx(expected)


@pytest.mark.parametrize("bad", ["", "   ", "nonsense", "1:2", "aa:bb:cc,ddd"])
def test_parse_timestamp_rejects_garbage(bad: str) -> None:
    assert parse_timestamp(bad) is None


def test_timestamp_round_trip() -> None:
    for seconds in (0.0, 1.234, 61.5, 3723.004, 7200.999):
        assert parse_timestamp(format_timestamp(seconds)) == pytest.approx(seconds, abs=0.001)


# ---------------------------------------------------------------------------
# body
# ---------------------------------------------------------------------------


def _cue(index: int, start: float, end: float, *lines: str) -> Cue:
    return Cue(index=index, start=start, end=end, lines=tuple(lines))


def test_write_srt_structure() -> None:
    body = write_srt([_cue(1, 0.0, 2.0, "첫 줄", "둘째 줄"), _cue(2, 2.5, 4.0, "다음")])

    assert body == (
        "1\n"
        "00:00:00,000 --> 00:00:02,000\n"
        "첫 줄\n"
        "둘째 줄\n"
        "\n"
        "2\n"
        "00:00:02,500 --> 00:00:04,000\n"
        "다음\n"
        "\n"
    )


def test_indexes_are_regenerated_from_one() -> None:
    body = write_srt([_cue(97, 0.0, 1.0, "가"), _cue(4, 1.0, 2.0, "나"), _cue(4, 2.0, 3.0, "다")])
    assert [line for line in body.split("\n") if line.isdigit()] == ["1", "2", "3"]


def test_blank_cues_are_skipped_and_do_not_consume_an_index() -> None:
    body = write_srt([_cue(1, 0.0, 1.0, "가"), _cue(2, 1.0, 2.0, "   "), _cue(3, 2.0, 3.0, "다")])

    assert [line for line in body.split("\n") if line.isdigit()] == ["1", "2"]
    assert "다" in body


def test_write_srt_accepts_plain_dicts() -> None:
    body = write_srt([{"start": 0.0, "end": 1.0, "lines": ["가"]}, {"start": 1.0, "end": 2.0, "text": "나\n다"}])
    assert "가" in body and "나" in body and "다" in body


def test_empty_input_produces_an_empty_string() -> None:
    assert write_srt([]) == ""


def test_windows_line_endings_are_idempotent() -> None:
    once = to_windows_line_endings("a\nb\n")
    assert once == "a\r\nb\r\n"
    assert to_windows_line_endings(once) == once


# ---------------------------------------------------------------------------
# file writing
# ---------------------------------------------------------------------------


def test_file_is_utf8_with_bom_and_crlf(tmp_path: Path) -> None:
    target = tmp_path / "movie.ko.srt"
    written, _ = write_subtitle_file([_cue(1, 0.0, 1.0, "한국어 자막")], target)

    assert written == str(target)

    raw = target.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")
    assert b"\r\n" in raw
    # Every LF must be part of a CRLF: a bare LF breaks old Windows players.
    assert b"\n" not in raw.replace(b"\r\n", b"")
    assert b"\r\r" not in raw

    text = raw.decode("utf-8-sig")
    assert "한국어 자막" in text
    assert text.startswith("1\r\n")


def test_no_temp_file_is_left_behind(tmp_path: Path) -> None:
    target = tmp_path / "movie.ko.srt"
    write_subtitle_file([_cue(1, 0.0, 1.0, "가")], target)

    assert [p.name for p in tmp_path.iterdir()] == ["movie.ko.srt"]


def test_parent_directories_are_created(tmp_path: Path) -> None:
    target = tmp_path / "a" / "b" / "movie.ko.srt"
    write_subtitle_file([_cue(1, 0.0, 1.0, "가")], target)
    assert target.is_file()


def test_writing_zero_cues_is_an_error_not_a_silent_success(tmp_path: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        write_subtitle_file([], tmp_path / "movie.ko.srt")

    assert excinfo.value.code == errors.OUTPUT_WRITE_FAILED
    assert not (tmp_path / "movie.ko.srt").exists()


def test_non_ascii_path_is_handled(tmp_path: Path) -> None:
    target = tmp_path / "영상 (2024) 한국어.ko.srt"
    written, _ = write_subtitle_file([_cue(1, 0.0, 1.0, "가")], target)
    assert Path(written).is_file()


# ---------------------------------------------------------------------------
# conflict policy
# ---------------------------------------------------------------------------


def test_default_policy_is_skip(tmp_path: Path) -> None:
    target = tmp_path / "movie.ko.srt"
    target.write_text("기존 내용", encoding="utf-8")

    written, reason = write_subtitle_file([_cue(1, 0.0, 1.0, "새 내용")], target)

    assert written is None
    assert reason == "이미 자막 파일이 있어 건너뜁니다."
    assert target.read_text(encoding="utf-8") == "기존 내용"


def test_overwrite_policy_replaces_the_file(tmp_path: Path) -> None:
    target = tmp_path / "movie.ko.srt"
    target.write_text("기존 내용", encoding="utf-8")

    written, reason = write_subtitle_file([_cue(1, 0.0, 1.0, "새 내용")], target, "overwrite")

    assert written == str(target)
    assert reason == "기존 자막 파일을 덮어씁니다."
    assert "새 내용" in target.read_text(encoding="utf-8-sig")


def test_numbered_policy_creates_a_new_file(tmp_path: Path) -> None:
    target = tmp_path / "movie.ko.srt"
    target.write_text("기존 내용", encoding="utf-8")

    written, reason = write_subtitle_file([_cue(1, 0.0, 1.0, "새 내용")], target, "numbered")

    assert written == str(tmp_path / "movie.ko (2).srt")
    assert reason is not None and "번호" in reason
    assert target.read_text(encoding="utf-8") == "기존 내용"


def test_numbered_policy_keeps_counting(tmp_path: Path) -> None:
    (tmp_path / "movie.ko.srt").write_text("a", encoding="utf-8")
    (tmp_path / "movie.ko (2).srt").write_text("b", encoding="utf-8")

    written, _ = write_subtitle_file([_cue(1, 0.0, 1.0, "가")], tmp_path / "movie.ko.srt", "numbered")
    assert written == str(tmp_path / "movie.ko (3).srt")


def test_resolve_output_path_when_nothing_exists(tmp_path: Path) -> None:
    path, should_write, reason = resolve_output_path(tmp_path / "new.srt")
    assert (path, should_write, reason) == (tmp_path / "new.srt", True, None)


# ---------------------------------------------------------------------------
# SRT parsing (embedded subtitle source mode)
# ---------------------------------------------------------------------------


SAMPLE = """1
00:00:01,000 --> 00:00:03,500
Hello there.

2
00:00:04,000 --> 00:00:06,000
Second line
continues here.
"""


def test_parse_srt_round_trip() -> None:
    segments = parse_srt(SAMPLE)

    assert len(segments) == 2
    assert segments[0] == {"id": 1, "start": 1.0, "end": 3.5, "text": "Hello there.", "words": []}
    assert segments[1]["text"] == "Second line continues here."


def test_parse_srt_tolerates_crlf_and_bom() -> None:
    segments = parse_srt(UTF8_BOM + SAMPLE.replace("\n", "\r\n"))
    assert len(segments) == 2


def test_parse_srt_skips_broken_blocks_without_failing() -> None:
    broken = SAMPLE + "\n3\nnot-a-timecode\nsome text\n\n4\n00:00:07,000 --> 00:00:08,000\n좋아\n"
    segments = parse_srt(broken)

    assert [s["text"] for s in segments] == ["Hello there.", "Second line continues here.", "좋아"]


def test_parse_srt_handles_a_missing_index_line() -> None:
    segments = parse_srt("00:00:01,000 --> 00:00:02,000\nno index here\n")
    assert segments[0]["text"] == "no index here"


def test_parse_srt_of_empty_text_is_empty() -> None:
    assert parse_srt("") == []

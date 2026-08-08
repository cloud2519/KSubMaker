"""SRT serialisation and atomic write.

Byte-for-byte compatible with ``KSubMaker.Domain.Subtitles.SrtFormatter``: ``HH:MM:SS,mmm``,
indexes renumbered from 1, CRLF line endings, UTF-8 **with BOM** (legacy Windows players sniff the
BOM to pick the encoding; without it Korean text renders as mojibake).
"""

from __future__ import annotations

import math
import os
from pathlib import Path
from typing import Any, Iterable, Sequence

from . import errors
from .errors import WorkerError
from .logging_setup import get_logger
from .subtitle_postprocessor import Cue

_log = get_logger("srt")

UTF8_BOM = "﻿"

CONFLICT_SKIP = "skip"
CONFLICT_OVERWRITE = "overwrite"
CONFLICT_NUMBERED = "numbered"


def format_timestamp(seconds: float) -> str:
    """``HH:MM:SS,mmm``. NaN/inf/negative clamp to zero."""
    if seconds is None or math.isnan(seconds) or math.isinf(seconds) or seconds < 0:
        seconds = 0.0

    # Work in integer milliseconds so 3.9999999 does not become "00:00:03,999".
    total_ms = int(_round_half_away(seconds * 1000.0))

    hours, total_ms = divmod(total_ms, 3_600_000)
    minutes, total_ms = divmod(total_ms, 60_000)
    secs, ms = divmod(total_ms, 1000)

    return f"{hours:02d}:{minutes:02d}:{secs:02d},{ms:03d}"


def _round_half_away(value: float) -> float:
    """Round half away from zero, matching .NET's ``MidpointRounding.AwayFromZero``.

    Python's built-in ``round`` uses banker's rounding, which would make 0.0005 s land on a
    different millisecond than the C# formatter for exactly-half values.
    """
    return math.floor(value + 0.5) if value >= 0 else math.ceil(value - 0.5)


def parse_timestamp(value: str) -> float | None:
    """Parse ``HH:MM:SS,mmm`` (or with a '.') back into seconds. None when unparseable."""
    if not value or not value.strip():
        return None

    parts = value.strip().replace(".", ",").split(":")
    if len(parts) != 3:
        return None

    seconds_parts = parts[2].split(",")

    try:
        hours = int(parts[0])
        minutes = int(parts[1])
        seconds = int(seconds_parts[0])
    except ValueError:
        return None

    milliseconds = 0
    if len(seconds_parts) > 1:
        try:
            milliseconds = int(seconds_parts[1].ljust(3, "0")[:3])
        except ValueError:
            return None

    return hours * 3600.0 + minutes * 60.0 + seconds + milliseconds / 1000.0


def write_srt(cues: Iterable[Cue | dict[str, Any]]) -> str:
    """Serialise cues to SRT text with ``\\n`` endings. Indexes are regenerated from 1."""
    chunks: list[str] = []
    index = 1

    for cue in cues:
        start, end, lines = _unpack(cue)
        kept = [line.rstrip() for line in lines if line and line.strip()]
        if not kept:
            continue

        chunks.append(str(index))
        chunks.append(f"{format_timestamp(start)} --> {format_timestamp(end)}")
        chunks.extend(kept)
        chunks.append("")
        index += 1

    return "\n".join(chunks) + ("\n" if chunks else "")


def _unpack(cue: Cue | dict[str, Any]) -> tuple[float, float, Sequence[str]]:
    if isinstance(cue, Cue):
        return cue.start, cue.end, cue.lines

    lines = cue.get("lines")
    if not isinstance(lines, (list, tuple)):
        text = cue.get("text")
        lines = str(text).split("\n") if text else []

    return (
        float(cue.get("start", 0.0) or 0.0),
        float(cue.get("end", 0.0) or 0.0),
        [str(line) for line in lines],
    )


def to_windows_line_endings(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\n", "\r\n")


def resolve_output_path(
    desired_path: str | Path,
    conflict_policy: str = CONFLICT_SKIP,
) -> tuple[Path, bool, str | None]:
    """Apply the conflict policy. Returns (path, should_write, Korean reason)."""
    target = Path(desired_path)

    if not target.exists():
        return target, True, None

    policy = (conflict_policy or CONFLICT_SKIP).strip().lower()

    if policy == CONFLICT_OVERWRITE:
        return target, True, "기존 자막 파일을 덮어씁니다."

    if policy in (CONFLICT_NUMBERED, "createnumberedcopy", "numberedcopy"):
        stem = target.stem
        suffix = target.suffix
        for i in range(2, 1000):
            candidate = target.with_name(f"{stem} ({i}){suffix}")
            if not candidate.exists():
                return candidate, True, "기존 파일이 있어 번호를 붙여 새 파일로 저장합니다."
        return target, False, "번호를 붙일 수 있는 파일명을 찾지 못했습니다."

    return target, False, "이미 자막 파일이 있어 건너뜁니다."


def write_subtitle_file(
    cues: Sequence[Cue | dict[str, Any]],
    desired_path: str | Path,
    conflict_policy: str = CONFLICT_SKIP,
) -> tuple[str | None, str | None]:
    """Write the SRT atomically.

    Returns ``(path, reason)``; ``path`` is None when the conflict policy said skip. Refuses to
    write an empty file: a zero-cue subtitle is always a pipeline bug, and silently producing one
    would look like success to the user.
    """
    if not cues:
        raise WorkerError(
            errors.OUTPUT_WRITE_FAILED,
            "저장할 자막이 없습니다. 번역 결과가 비어 있습니다.",
            detail="write_subtitle_file called with zero cues",
        )

    target, should_write, reason = resolve_output_path(desired_path, conflict_policy)

    if not should_write:
        _log.info("not writing %s: %s", target, reason)
        return None, reason

    body = to_windows_line_endings(write_srt(cues))
    temp = target.with_name(target.name + ".tmp")

    try:
        target.parent.mkdir(parents=True, exist_ok=True)

        # newline="" keeps the CRLF we just produced; without it Python would translate again on
        # Windows and emit CRCRLF.
        with temp.open("w", encoding="utf-8", newline="") as handle:
            handle.write(UTF8_BOM)
            handle.write(body)
            handle.flush()
            os.fsync(handle.fileno())

        os.replace(temp, target)
    except OSError as exc:
        try:
            temp.unlink(missing_ok=True)
        except OSError as cleanup_exc:  # pragma: no cover - defensive
            _log.debug("could not delete %s: %r", temp, cleanup_exc)

        code = errors.DISK_SPACE_LOW if getattr(exc, "errno", None) == 28 else errors.OUTPUT_WRITE_FAILED
        message = (
            "디스크 공간이 부족하여 자막 파일을 저장하지 못했습니다."
            if code == errors.DISK_SPACE_LOW
            else f"자막 파일을 저장하지 못했습니다: {target.name}"
        )
        raise WorkerError(code, message, detail=repr(exc)) from exc

    _log.info("wrote %d cues to %s", len(cues), target)
    return str(target), reason


def parse_srt(text: str) -> list[dict[str, Any]]:
    """Parse SRT text into ``{id, start, end, text}`` segments.

    Used by the ``embeddedSubtitle`` source mode, where the "transcript" comes from an existing
    subtitle track instead of ASR. Malformed blocks are skipped, not fatal: a single broken cue in
    a 1200-cue track must not fail the job.
    """
    body = text.lstrip(UTF8_BOM).replace("\r\n", "\n").replace("\r", "\n")
    segments: list[dict[str, Any]] = []

    for block in body.split("\n\n"):
        lines = [line for line in block.split("\n") if line.strip()]
        if len(lines) < 2:
            continue

        cursor = 0
        if "-->" not in lines[0]:
            cursor = 1
        if cursor >= len(lines) or "-->" not in lines[cursor]:
            continue

        left, _, right = lines[cursor].partition("-->")
        start = parse_timestamp(left.strip())
        end = parse_timestamp(right.strip().split(" ")[0])
        if start is None or end is None:
            continue

        content = " ".join(line.strip() for line in lines[cursor + 1 :]).strip()
        if not content:
            continue

        segments.append(
            {
                "id": len(segments) + 1,
                "start": start,
                "end": max(end, start + 0.001),
                "text": content,
                "words": [],
            }
        )

    return segments

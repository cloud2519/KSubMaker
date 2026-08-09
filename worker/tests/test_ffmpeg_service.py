"""ffmpeg / ffprobe wrapping.

Uses the real ffmpeg on PATH when there is one (a tiny synthetic clip), and fakes for the failure
paths that are impractical to provoke for real.
"""

from __future__ import annotations

import shutil
import subprocess
import wave
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import errors
from ksubmaker_worker.cancellation import CancellationToken
from ksubmaker_worker.ffmpeg_service import (
    FfmpegService,
    _iter_status_chunks,
    _parse_time,
    find_binary,
)

HAS_FFMPEG = shutil.which("ffmpeg") is not None and shutil.which("ffprobe") is not None
needs_ffmpeg = pytest.mark.skipif(not HAS_FFMPEG, reason="ffmpeg/ffprobe not installed")


@pytest.fixture(scope="module")
def clip(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """A 3-second synthetic video with one audio track."""
    if not HAS_FFMPEG:
        pytest.skip("ffmpeg not installed")

    path = tmp_path_factory.mktemp("media") / "테스트 영상 (2024).mp4"
    subprocess.run(  # noqa: S603
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", str(path),
        ],
        check=True,
        capture_output=True,
        timeout=120,
    )
    return path


@pytest.fixture(scope="module")
def silent_clip(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """A video with no audio track at all."""
    if not HAS_FFMPEG:
        pytest.skip("ffmpeg not installed")

    path = tmp_path_factory.mktemp("media") / "silent.mp4"
    subprocess.run(  # noqa: S603
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", str(path),
        ],
        check=True,
        capture_output=True,
        timeout=120,
    )
    return path


# ---------------------------------------------------------------------------
# discovery
# ---------------------------------------------------------------------------


def test_bundled_binary_wins_over_path(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    bundled = tmp_path / "ffmpeg" / "bin" / "ffmpeg"
    bundled.parent.mkdir(parents=True)
    bundled.write_text("#!/bin/sh\n")
    bundled.chmod(0o755)

    monkeypatch.setenv("KSUBMAKER_TOOLS_DIR", str(tmp_path))

    # PATH must not be consulted while a bundled copy exists.
    monkeypatch.setattr("shutil.which", lambda _name: "/usr/bin/ffmpeg")

    assert find_binary("ffmpeg") == str(bundled.resolve())


def test_path_is_the_last_resort(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("KSUBMAKER_TOOLS_DIR", str(tmp_path / "nothing-here"))
    monkeypatch.setattr("shutil.which", lambda _name: "/somewhere/ffmpeg")

    assert find_binary("ffmpeg") == "/somewhere/ffmpeg"


def test_missing_binary_raises_ffmpeg_not_found(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("KSUBMAKER_TOOLS_DIR", str(tmp_path / "nothing-here"))
    monkeypatch.setattr("shutil.which", lambda _name: None)

    service = FfmpegService()
    with pytest.raises(errors.WorkerError) as excinfo:
        _ = service.ffmpeg_path

    assert excinfo.value.code == errors.FFMPEG_NOT_FOUND
    assert service.available() is False


# ---------------------------------------------------------------------------
# probe
# ---------------------------------------------------------------------------


@needs_ffmpeg
def test_probe_reports_duration_and_tracks(clip: Path) -> None:
    result = FfmpegService().probe(str(clip))

    assert result["videoPath"] == str(clip)
    assert result["durationSeconds"] == pytest.approx(3.0, abs=0.5)
    assert len(result["audioTracks"]) == 1
    assert result["audioTracks"][0]["channels"] >= 1
    assert result["subtitleTracks"] == []
    assert "error" not in result


@needs_ffmpeg
def test_probe_of_a_video_with_no_audio(silent_clip: Path) -> None:
    assert FfmpegService().probe(str(silent_clip))["audioTracks"] == []


@needs_ffmpeg
def test_probe_of_a_corrupt_file_reports_an_error_instead_of_raising(tmp_path: Path) -> None:
    broken = tmp_path / "broken.mp4"
    broken.write_bytes(b"this is definitely not a video container" * 40)

    result = FfmpegService().probe(str(broken))

    assert result["error"]
    assert result["durationSeconds"] == 0.0


def test_probe_of_a_missing_file_raises_video_not_found(tmp_path: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        FfmpegService(ffmpeg="/bin/true", ffprobe="/bin/true").probe(str(tmp_path / "nope.mkv"))

    assert excinfo.value.code == errors.VIDEO_NOT_FOUND


# ---------------------------------------------------------------------------
# audio extraction
# ---------------------------------------------------------------------------


@needs_ffmpeg
def test_extract_audio_produces_16k_mono_pcm(clip: Path, tmp_path: Path) -> None:
    target = tmp_path / "out" / "audio.wav"
    progress: list[float] = []

    result = FfmpegService().extract_audio(
        str(clip), str(target), progress=progress.append
    )

    assert result == str(target)
    assert target.is_file()

    with wave.open(str(target), "rb") as handle:
        assert handle.getnchannels() == 1
        assert handle.getframerate() == 16_000
        assert handle.getsampwidth() == 2
        assert handle.getnframes() > 0

    assert progress and progress[-1] == 100.0
    assert progress == sorted(progress)


@needs_ffmpeg
def test_extract_audio_leaves_no_temp_file(clip: Path, tmp_path: Path) -> None:
    target = tmp_path / "audio.wav"
    FfmpegService().extract_audio(str(clip), str(target))

    assert [p.name for p in tmp_path.iterdir()] == ["audio.wav"]


@needs_ffmpeg
def test_extract_audio_from_a_video_with_no_audio_track(silent_clip: Path, tmp_path: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        FfmpegService().extract_audio(str(silent_clip), str(tmp_path / "audio.wav"))

    assert excinfo.value.code == errors.AUDIO_TRACK_NOT_FOUND
    assert not (tmp_path / "audio.wav").exists()
    assert not (tmp_path / "audio.wav.tmp").exists()


@needs_ffmpeg
def test_extract_audio_from_a_corrupt_file_fails_cleanly(tmp_path: Path) -> None:
    broken = tmp_path / "broken.mkv"
    broken.write_bytes(b"\x00\x01\x02not a container" * 100)

    with pytest.raises(errors.WorkerError) as excinfo:
        FfmpegService().extract_audio(
            str(broken), str(tmp_path / "audio.wav"), duration_seconds=10.0
        )

    assert excinfo.value.code in (errors.VIDEO_UNREADABLE, errors.FFMPEG_FAILED)
    assert not (tmp_path / "audio.wav").exists()


@needs_ffmpeg
def test_extract_audio_handles_a_non_ascii_path(clip: Path, tmp_path: Path) -> None:
    # The source fixture already has a space and Hangul in its name; give the target one too.
    target = tmp_path / "출력 폴더" / "오디오 (16k).wav"
    assert FfmpegService().extract_audio(str(clip), str(target)) == str(target)
    assert target.is_file()


@needs_ffmpeg
def test_cancelling_extraction_kills_ffmpeg_and_deletes_the_temp(clip: Path, tmp_path: Path) -> None:
    token = CancellationToken("t")
    target = tmp_path / "audio.wav"

    def cancel_on_first_progress(_percent: float) -> None:
        token.cancel()

    with pytest.raises(errors.CancelledError):
        FfmpegService().extract_audio(
            str(clip), str(target), token=token, progress=cancel_on_first_progress
        )

    assert not target.exists()
    assert not (tmp_path / "audio.wav.tmp").exists()


def test_extraction_of_a_missing_source_raises_video_not_found(tmp_path: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        FfmpegService(ffmpeg="/bin/true", ffprobe="/bin/true").extract_audio(
            str(tmp_path / "nope.mkv"), str(tmp_path / "a.wav")
        )

    assert excinfo.value.code == errors.VIDEO_NOT_FOUND


def test_a_nonzero_exit_becomes_ffmpeg_failed(tmp_path: Path) -> None:
    source = tmp_path / "movie.mkv"
    source.write_bytes(b"x")

    service = FfmpegService(ffmpeg="/bin/false", ffprobe="/bin/false")
    with pytest.raises(errors.WorkerError) as excinfo:
        service.extract_audio(str(source), str(tmp_path / "a.wav"), duration_seconds=5.0)

    assert excinfo.value.code == errors.FFMPEG_FAILED
    assert excinfo.value.recoverable is True


def test_a_missing_ffmpeg_binary_becomes_ffmpeg_not_found(tmp_path: Path) -> None:
    source = tmp_path / "movie.mkv"
    source.write_bytes(b"x")

    service = FfmpegService(ffmpeg=str(tmp_path / "no-such-ffmpeg"), ffprobe="/bin/true")
    with pytest.raises(errors.WorkerError) as excinfo:
        service.extract_audio(str(source), str(tmp_path / "a.wav"), duration_seconds=5.0)

    assert excinfo.value.code == errors.FFMPEG_NOT_FOUND


def test_extraction_never_builds_a_shell_string(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    source = tmp_path / "movie; rm -rf ~.mkv"
    source.write_bytes(b"x")
    seen: dict[str, Any] = {}

    class FakePopen:
        def __init__(self, argv, **kwargs):  # noqa: ANN001
            seen["argv"] = argv
            seen["kwargs"] = kwargs
            self.stderr = _EmptyStream()
            self.returncode = 0

        def poll(self):  # noqa: ANN201
            return 0

        def wait(self, timeout=None):  # noqa: ANN001, ANN201
            return 0

    monkeypatch.setattr(subprocess, "Popen", FakePopen)

    target = tmp_path / "a.wav"
    target.write_bytes(b"RIFF")  # so the post-run size check passes
    monkeypatch.setattr("os.replace", lambda *_args: None)
    (tmp_path / "a.wav.tmp").write_bytes(b"RIFF-data")

    FfmpegService(ffmpeg="/usr/bin/ffmpeg", ffprobe="/usr/bin/ffprobe").extract_audio(
        str(source), str(target), duration_seconds=5.0
    )

    assert isinstance(seen["argv"], list)
    assert str(source) in seen["argv"]
    assert "shell" not in seen["kwargs"] or seen["kwargs"]["shell"] is False


def _captured_argv(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, **kwargs: Any
) -> list[str]:
    """Run ``extract_audio`` against a stubbed ffmpeg and return the argv it would have run."""
    source = tmp_path / "movie.mkv"
    source.write_bytes(b"x")
    seen: dict[str, Any] = {}

    class FakePopen:
        def __init__(self, argv, **popen_kwargs):  # noqa: ANN001, ARG002
            seen["argv"] = argv
            self.stderr = _EmptyStream()
            self.returncode = 0

        def poll(self):  # noqa: ANN201
            return 0

        def wait(self, timeout=None):  # noqa: ANN001, ANN201, ARG002
            return 0

    monkeypatch.setattr(subprocess, "Popen", FakePopen)
    monkeypatch.setattr("os.replace", lambda *_args: None)

    target = tmp_path / "a.wav"
    target.write_bytes(b"RIFF")
    (tmp_path / "a.wav.tmp").write_bytes(b"RIFF-data")

    FfmpegService(ffmpeg="/usr/bin/ffmpeg", ffprobe="/usr/bin/ffprobe").extract_audio(
        str(source), str(target), **kwargs
    )
    return list(seen["argv"])


def test_the_progress_length_alone_never_trims_the_output(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """The one that bit us: ``duration_seconds`` is a denominator, not a length limit.

    While the two were a single parameter, every ordinary extraction ran with
    ``-t <container duration>``. A container that under-reports its own length — an ASF, a VBR
    MP3 whose header estimate is short — silently produced a truncated wav, and the only symptom
    was a subtitle that stopped before the film did.
    """
    argv = _captured_argv(monkeypatch, tmp_path, duration_seconds=7200.0)

    assert "-t" not in argv


def test_a_test_run_trims_and_measures_itself_against_the_trimmed_length(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    argv = _captured_argv(monkeypatch, tmp_path, duration_seconds=7200.0, trim_seconds=60.0)

    assert argv[argv.index("-t") + 1] == "60.000"


@pytest.mark.parametrize("trim", [None, 0, -1.0])
def test_no_trim_means_the_whole_track(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, trim: float | None
) -> None:
    """All three reach us — an older host omits the field, the current one defaults it to 0."""
    argv = _captured_argv(monkeypatch, tmp_path, duration_seconds=7200.0, trim_seconds=trim)

    assert "-t" not in argv


class _EmptyStream:
    def read(self, _size: int = -1) -> bytes:
        return b""


# ---------------------------------------------------------------------------
# subtitle extraction
# ---------------------------------------------------------------------------


@needs_ffmpeg
def test_extract_subtitle_track(tmp_path: Path) -> None:
    srt = tmp_path / "source.srt"
    srt.write_text(
        "1\n00:00:00,500 --> 00:00:02,000\nHello there.\n\n"
        "2\n00:00:02,500 --> 00:00:04,000\nGeneral Kenobi.\n",
        encoding="utf-8",
    )

    mkv = tmp_path / "with-subs.mkv"
    subprocess.run(  # noqa: S603
        [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=4",
            "-i", str(srt),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:s", "srt", str(mkv),
        ],
        check=True,
        capture_output=True,
        timeout=120,
    )

    text = FfmpegService().extract_subtitle_track(str(mkv), 0)

    assert "Hello there." in text
    assert "General Kenobi." in text


@needs_ffmpeg
def test_extracting_a_missing_subtitle_track_fails(clip: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        FfmpegService().extract_subtitle_track(str(clip), 3)

    assert excinfo.value.code in (errors.FFMPEG_FAILED, errors.AUDIO_TRACK_NOT_FOUND)


# ---------------------------------------------------------------------------
# stderr parsing
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("line", "expected"),
    [
        ("frame=  100 fps=0.0 time=00:00:04.20 bitrate=N/A speed=8.4x", 4.2),
        ("time=01:02:03.50", 3723.5),
        ("time=-00:00:00.00", 0.0),
        ("out_time_ms=2500000", 2.5),
        ("no timing information here", None),
    ],
)
def test_parse_time(line: str, expected: float | None) -> None:
    result = _parse_time(line)
    if expected is None:
        assert result is None
    else:
        assert result == pytest.approx(expected)


def test_status_chunks_split_on_carriage_returns() -> None:
    class Stream:
        def __init__(self, data: bytes) -> None:
            self._data = data
            self._offset = 0

        def read(self, size: int) -> bytes:
            chunk = self._data[self._offset : self._offset + size]
            self._offset += len(chunk)
            return chunk

    # ffmpeg separates status updates with \r, so a readline-based reader would block until the
    # whole run finished.
    raw = b"line one\rtime=00:00:01.00\rtime=00:00:02.00\nfinal"
    assert list(_iter_status_chunks(Stream(raw))) == [
        "line one",
        "time=00:00:01.00",
        "time=00:00:02.00",
        "final",
    ]

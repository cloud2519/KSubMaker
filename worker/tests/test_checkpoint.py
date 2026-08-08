"""Checkpoint save / resume / invalidate-on-source-change."""

from __future__ import annotations

import json
import os
import time
from pathlib import Path

from ksubmaker_worker.checkpoint import (
    AUDIO as AUDIO_ARTIFACT,
)
from ksubmaker_worker.checkpoint import (
    TRANSCRIPTION as TRANSCRIPTION_ARTIFACT,
)
from ksubmaker_worker.checkpoint import (
    TRANSLATION as TRANSLATION_ARTIFACT,
)
from ksubmaker_worker.checkpoint import (
    JOB_FILE,
    PARTIAL_TRANSLATION_FILE,
    TRANSCRIPTION_FILE,
    CheckpointStore,
    missing_ids,
    source_fingerprint,
    stale_artifacts,
)


def _video(tmp_path: Path, content: bytes = b"video-bytes") -> Path:
    path = tmp_path / "movie.mkv"
    path.write_bytes(content)
    return path


def _store(tmp_path: Path, video: Path) -> CheckpointStore:
    return CheckpointStore(tmp_path / "cache" / "job-1", "job-1", str(video))


TRANSCRIPTION = {
    "sourceLanguage": "en",
    "languageProbability": 0.97,
    "durationSeconds": 12.5,
    "modelId": "whisper-small",
    "segments": [
        {"id": 1, "start": 0.0, "end": 2.0, "text": "Hello.", "words": []},
        {"id": 2, "start": 2.0, "end": 4.0, "text": "World.", "words": []},
    ],
}


# ---------------------------------------------------------------------------
# round trips
# ---------------------------------------------------------------------------


def test_job_round_trip(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)

    store.save_job(
        completed_stage="transcribing",
        audio_path="/tmp/audio.wav",
        detected_language="en",
        whisper_model="whisper-small",
    )

    job = store.load_job()
    assert job is not None
    assert job["jobId"] == "job-1"
    assert job["completedStage"] == "transcribing"
    assert job["detectedLanguage"] == "en"
    assert job["sourceFileSize"] == video.stat().st_size


def test_transcription_round_trip(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_transcription(TRANSCRIPTION)

    loaded = store.load_transcription()
    assert loaded == TRANSCRIPTION


def test_partial_translation_round_trip_rekeys_to_int(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_partial_translation({1: "안녕하세요", 2: "세상"})

    # JSON object keys are strings on disk...
    raw = json.loads((store.directory / PARTIAL_TRANSLATION_FILE).read_text(encoding="utf-8"))
    assert raw == {"1": "안녕하세요", "2": "세상"}

    # ...and integers again in memory.
    assert store.load_partial_translation() == {1: "안녕하세요", 2: "세상"}


def test_finalization_round_trip(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_finalization(output_path="/tmp/a.ko.srt", cue_count=42, skipped=False, reason=None)

    final = store.load_finalization()
    assert final is not None
    assert final["cueCount"] == 42


def test_missing_files_read_as_absent(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))

    assert store.load_job() is None
    assert store.load_transcription() is None
    assert store.load_partial_translation() == {}
    assert store.load_finalization() is None


# ---------------------------------------------------------------------------
# atomicity
# ---------------------------------------------------------------------------


def test_writes_leave_no_temp_files(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(completed_stage="probing")
    store.save_transcription(TRANSCRIPTION)
    store.save_partial_translation({1: "가"})

    assert not [p for p in store.directory.iterdir() if p.name.endswith(".tmp")]


def test_corrupt_checkpoint_degrades_to_absent(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_transcription(TRANSCRIPTION)

    (store.directory / TRANSCRIPTION_FILE).write_text('{"segments": [tru', encoding="utf-8")

    # A truncated file must read as "no checkpoint" so the stage simply runs again.
    assert store.load_transcription() is None


def test_transcription_with_no_segments_is_rejected(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_transcription({"sourceLanguage": "en", "segments": []})

    assert store.load_transcription() is None


# ---------------------------------------------------------------------------
# resume
# ---------------------------------------------------------------------------


def test_resume_is_valid_for_an_unchanged_source(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.save_job(completed_stage="transcribing")

    assert store.is_valid_for_source() is True


def test_resume_is_invalid_when_the_source_size_changed(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.save_job(completed_stage="transcribing")

    video.write_bytes(b"a-completely-different-and-longer-video-file")

    assert store.is_valid_for_source() is False


def test_resume_is_invalid_when_the_source_mtime_changed(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.save_job(completed_stage="transcribing")

    # Same size, different mtime: a re-encode with identical length still has new timecodes.
    future = time.time() + 3600
    os.utime(video, (future, future))

    assert store.is_valid_for_source() is False


def test_small_mtime_jitter_is_tolerated(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.save_job(completed_stage="transcribing")

    stat = video.stat()
    os.utime(video, (stat.st_atime, stat.st_mtime + 0.4))

    assert store.is_valid_for_source() is True


def test_resume_is_invalid_with_no_checkpoint(tmp_path: Path) -> None:
    assert _store(tmp_path, _video(tmp_path)).is_valid_for_source() is False


def test_resume_is_invalid_when_the_source_vanished(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.save_job(completed_stage="transcribing")

    video.unlink()

    assert store.is_valid_for_source() is False


def test_checkpoint_from_an_older_build_without_a_fingerprint_is_trusted(tmp_path: Path) -> None:
    video = _video(tmp_path)
    store = _store(tmp_path, video)
    store.directory.mkdir(parents=True, exist_ok=True)
    (store.directory / JOB_FILE).write_text(
        json.dumps({"jobId": "job-1", "completedStage": "transcribing"}), encoding="utf-8"
    )

    # Redoing hours of ASR because an old build wrote no fingerprint would be worse than trusting it.
    assert store.is_valid_for_source() is True


def test_clear_removes_every_checkpoint_file(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(completed_stage="transcribing")
    store.save_transcription(TRANSCRIPTION)
    store.save_partial_translation({1: "가"})
    store.save_finalization(output_path=None, cue_count=0)

    store.clear()

    assert store.load_job() is None
    assert store.load_transcription() is None
    assert store.load_partial_translation() == {}
    assert store.load_finalization() is None


def test_clear_keeps_the_extracted_audio(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(completed_stage="transcribing")
    store.audio_path().write_bytes(b"RIFF....")

    store.clear()

    # Re-extracting a 2 GB film's audio because a checkpoint was cleared would be a real cost.
    assert store.audio_path().is_file()


def test_clear_on_a_missing_directory_is_a_no_op(tmp_path: Path) -> None:
    CheckpointStore(tmp_path / "nope", "j", str(tmp_path / "movie.mkv")).clear()


def test_clear_translation_keeps_the_audio_and_the_transcript(tmp_path: Path) -> None:
    store = _seeded(tmp_path)

    store.clear_translation()

    assert store.load_partial_translation() == {}
    assert store.load_finalization() is None
    # The expensive stages survive: that is the whole point of clearing only the translation.
    assert store.load_transcription() == TRANSCRIPTION
    assert store.has_audio()


def test_clear_transcription_also_drops_the_translation(tmp_path: Path) -> None:
    store = _seeded(tmp_path)

    store.clear_transcription()

    # Translations are keyed by segment id, and re-running ASR renumbers the segments; keeping
    # them would attach the old Korean text to unrelated timecodes.
    assert store.load_transcription() is None
    assert store.load_partial_translation() == {}
    assert store.has_audio()


def test_clear_audio_removes_only_the_wav(tmp_path: Path) -> None:
    store = _seeded(tmp_path)

    store.clear_audio()

    assert not store.has_audio()
    assert store.load_transcription() == TRANSCRIPTION


def test_a_zero_byte_wav_does_not_count_as_extracted_audio(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.directory.mkdir(parents=True, exist_ok=True)
    store.audio_path().write_bytes(b"")

    # A kill during extraction leaves the file ffmpeg created but never filled. Handing that to
    # Whisper yields an empty transcript rather than an error, so it must not read as reusable.
    assert store.has_audio() is False


# ---------------------------------------------------------------------------
# settings drift
# ---------------------------------------------------------------------------

_AUDIO = {"sourceMode": "audio", "audioTrackIndex": None}
_ASR = {"whisperModel": "whisper-medium", "language": "ja"}
_MT = {"engine": "local-translation", "model": "nllb-200-distilled-1.3B", "style": "natural"}


def _recorded(**overrides: object) -> dict[str, object]:
    record = {
        "audioSettings": dict(_AUDIO),
        "transcriptionSettings": dict(_ASR),
        "translationSettings": dict(_MT),
    }
    record.update(overrides)
    return record


def _stale(recorded: dict[str, object] | None) -> set[str]:
    return stale_artifacts(recorded, audio=_AUDIO, transcription=_ASR, translation=_MT)


def test_unchanged_settings_invalidate_nothing() -> None:
    assert _stale(_recorded()) == set()


def test_a_changed_translation_model_invalidates_only_the_translation() -> None:
    previous = _recorded(translationSettings={**_MT, "model": "qwen2.5-7b-instruct-q4km"})

    # The whole reason this exists: switching engines must not leave a file half-translated by
    # each, but it must also not throw away an hour of ASR.
    assert _stale(previous) == {TRANSLATION_ARTIFACT}


def test_a_changed_style_invalidates_only_the_translation() -> None:
    assert _stale(_recorded(translationSettings={**_MT, "style": "polite"})) == {TRANSLATION_ARTIFACT}


def test_a_changed_glossary_invalidates_only_the_translation() -> None:
    previous = _recorded(translationSettings={**_MT, "glossary": {"Tokyo": "도쿄"}})
    assert _stale(previous) == {TRANSLATION_ARTIFACT}


def test_a_changed_whisper_model_invalidates_the_transcript_and_below() -> None:
    previous = _recorded(transcriptionSettings={**_ASR, "whisperModel": "whisper-large-v3"})
    assert _stale(previous) == {TRANSCRIPTION_ARTIFACT, TRANSLATION_ARTIFACT}


def test_a_changed_audio_track_invalidates_everything() -> None:
    previous = _recorded(audioSettings={**_AUDIO, "audioTrackIndex": 2})
    assert _stale(previous) == {AUDIO_ARTIFACT, TRANSCRIPTION_ARTIFACT, TRANSLATION_ARTIFACT}


def test_a_checkpoint_without_fingerprints_is_trusted() -> None:
    # Same call is_valid_for_source makes for a missing source fingerprint, for the same reason:
    # an older build's checkpoint is not evidence that anything changed.
    assert _stale({"jobId": "job-1", "completedStage": "transcribing"}) == set()


def test_a_missing_record_invalidates_nothing() -> None:
    assert _stale(None) == set()
    assert _stale({}) == set()


def test_fingerprints_survive_the_job_round_trip(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(
        completed_stage="transcribing",
        audio_settings=_AUDIO,
        transcription_settings=_ASR,
        translation_settings=_MT,
    )

    assert _stale(store.load_job()) == set()


def test_save_job_omits_fingerprints_it_was_not_given(tmp_path: Path) -> None:
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(completed_stage="transcribing")

    job = store.load_job()
    assert job is not None
    # Absent, not null: "no fingerprint" has to keep meaning "written by an older build".
    assert "translationSettings" not in job


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def _seeded(tmp_path: Path) -> CheckpointStore:
    """A store with every artefact on disk."""
    store = _store(tmp_path, _video(tmp_path))
    store.save_job(completed_stage="writingSubtitle")
    store.save_transcription(TRANSCRIPTION)
    store.save_partial_translation({1: "안녕하세요"})
    store.save_finalization(output_path="/tmp/a.ko.srt", cue_count=1)
    store.audio_path().write_bytes(b"RIFF....")
    return store


def test_missing_ids_reports_untranslated_segments_in_order() -> None:
    segments = [{"id": i, "text": "x"} for i in (1, 2, 3, 4)]
    assert missing_ids(segments, {2: "가", 4: "다"}) == [1, 3]


def test_missing_ids_treats_a_blank_translation_as_missing() -> None:
    segments = [{"id": 1, "text": "x"}, {"id": 2, "text": "y"}]
    assert missing_ids(segments, {1: "가", 2: "   "}) == [2]


def test_source_fingerprint_of_a_missing_file_is_empty(tmp_path: Path) -> None:
    assert source_fingerprint(tmp_path / "nope.mkv") == {}

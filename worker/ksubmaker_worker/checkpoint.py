"""Per-job checkpoints under the job's ``checkpointDir``.

Four files, all written temp-then-``os.replace`` so a power cut leaves either the previous
complete file or the new one, never a truncated one:

* ``job.json``                  -- stage reached, source fingerprint, audio path
* ``transcription.json``        -- the ASR result (the expensive part)
* ``translation.partial.json``  -- ``{segment id: Korean text}`` accumulated batch by batch
* ``finalization.json``         -- the cues actually written, plus the output path

Resume rules: transcription present ⇒ skip ASR; partial translations present ⇒ translate only the
missing ids. Everything is invalidated when the source file's size or mtime changed, because a
re-encoded video with the same name has completely different timecodes.

Settings invalidate resume too, and they do it *per artefact*. ``job.json`` records the settings
each artefact was produced under, and :func:`stale_artifacts` compares them against the current
run. Changing the translation model or the 문체 must not silently leave half the file translated
by the old engine, but it also must not throw away an hour of ASR — which is exactly what a single
"settings changed, start over" flag would do. The tiers are ordered: audio invalidates everything
downstream of it, transcription invalidates the translation keyed to its segment ids.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, Mapping

from .logging_setup import get_logger

_log = get_logger("checkpoint")

JOB_FILE = "job.json"
TRANSCRIPTION_FILE = "transcription.json"
PARTIAL_TRANSLATION_FILE = "translation.partial.json"
FINALIZATION_FILE = "finalization.json"
AUDIO_FILE = "audio.wav"
TEMP_SUFFIX = ".tmp"

STAGE_NONE = "none"

#: ``job.json`` keys holding the settings each artefact was produced under. Mirrored by
#: ``KSubMaker.Application.Abstractions.JobCheckpoint``.
AUDIO_SETTINGS_KEY = "audioSettings"
TRANSCRIPTION_SETTINGS_KEY = "transcriptionSettings"
TRANSLATION_SETTINGS_KEY = "translationSettings"

#: Artefact names returned by :func:`stale_artifacts`.
AUDIO = "audio"
TRANSCRIPTION = "transcription"
TRANSLATION = "translation"


def source_fingerprint(path: str | Path) -> dict[str, Any]:
    """Size + mtime of the source file. Empty when it does not exist."""
    try:
        stat = Path(path).stat()
    except OSError as exc:
        _log.debug("could not stat %s: %r", path, exc)
        return {}
    return {"sourceFileSize": stat.st_size, "sourceLastWriteUnix": round(stat.st_mtime, 3)}


class CheckpointStore:
    """Reads and writes the four checkpoint files for one job."""

    def __init__(self, directory: str | Path, job_id: str = "", video_path: str = "") -> None:
        self.directory = Path(directory)
        self.job_id = job_id
        self.video_path = video_path

    # -- raw IO ---------------------------------------------------------------

    def _path(self, name: str) -> Path:
        return self.directory / name

    def _read(self, name: str) -> Any | None:
        path = self._path(name)
        if not path.is_file():
            return None

        try:
            with path.open("r", encoding="utf-8") as handle:
                return json.load(handle)
        except json.JSONDecodeError as exc:
            # A truncated checkpoint must degrade to "no checkpoint" and let the stage run again,
            # never take the job down.
            _log.warning("discarding corrupt checkpoint %s: %r", path, exc)
            return None
        except OSError as exc:
            _log.warning("could not read checkpoint %s: %r", path, exc)
            return None

    def _write(self, name: str, payload: Any) -> None:
        self.directory.mkdir(parents=True, exist_ok=True)
        final = self._path(name)
        temp = final.with_name(final.name + TEMP_SUFFIX)

        try:
            with temp.open("w", encoding="utf-8") as handle:
                json.dump(payload, handle, ensure_ascii=False, separators=(",", ":"))
                handle.flush()
                # Flush to the device before the rename: without this the rename can reach the
                # disk before the data does, which is exactly the corruption this design avoids.
                os.fsync(handle.fileno())

            os.replace(temp, final)
        except OSError as exc:
            _log.warning("could not write checkpoint %s: %r", final, exc)
            try:
                temp.unlink(missing_ok=True)
            except OSError:
                pass

    # -- job ------------------------------------------------------------------

    def load_job(self) -> dict[str, Any] | None:
        data = self._read(JOB_FILE)
        return data if isinstance(data, dict) else None

    def save_job(
        self,
        *,
        completed_stage: str,
        audio_path: str | None = None,
        detected_language: str | None = None,
        whisper_model: str | None = None,
        audio_settings: Mapping[str, Any] | None = None,
        transcription_settings: Mapping[str, Any] | None = None,
        translation_settings: Mapping[str, Any] | None = None,
    ) -> None:
        payload: dict[str, Any] = {
            "jobId": self.job_id,
            "videoPath": self.video_path,
            "completedStage": completed_stage,
            "audioPath": audio_path,
            "detectedLanguage": detected_language,
            "whisperModel": whisper_model,
        }
        payload.update(source_fingerprint(self.video_path))

        # Written on every save so the record always describes the run that produced the artefacts
        # currently on disk. Omitted (rather than nulled) when the caller has nothing to record, so
        # that "no fingerprint" keeps meaning "written by an older build" — see stale_artifacts.
        for key, value in (
            (AUDIO_SETTINGS_KEY, audio_settings),
            (TRANSCRIPTION_SETTINGS_KEY, transcription_settings),
            (TRANSLATION_SETTINGS_KEY, translation_settings),
        ):
            if value is not None:
                payload[key] = dict(value)

        self._write(JOB_FILE, payload)

    def refresh_settings(
        self,
        *,
        audio_settings: Mapping[str, Any],
        transcription_settings: Mapping[str, Any],
        translation_settings: Mapping[str, Any],
    ) -> None:
        """Re-stamp ``job.json`` with the settings of the run about to start.

        Called right after stale artefacts are discarded, and that timing is the point: the record
        must describe what is *now* on disk. Leaving the old fingerprints in place until the job
        finishes means a run that fails halfway is diagnosed as stale all over again on the next
        attempt — so a failing translation would restart from zero every time instead of resuming,
        which is the opposite of what the checkpoint is for.

        Everything else in the record, ``completedStage`` above all, is preserved.
        """
        job = self.load_job()
        if job is None:
            # No record to amend. The stage that produces the artefacts writes a complete one.
            return

        job[AUDIO_SETTINGS_KEY] = dict(audio_settings)
        job[TRANSCRIPTION_SETTINGS_KEY] = dict(transcription_settings)
        job[TRANSLATION_SETTINGS_KEY] = dict(translation_settings)
        self._write(JOB_FILE, job)

    def is_valid_for_source(self) -> bool:
        """False when there is no checkpoint, or the source changed underneath it."""
        job = self.load_job()
        if job is None:
            return False

        current = source_fingerprint(self.video_path)
        if not current:
            return False

        recorded_size = job.get("sourceFileSize")
        recorded_mtime = job.get("sourceLastWriteUnix")

        if recorded_size is None and recorded_mtime is None:
            # Written by an older build with no fingerprint: trust it rather than redo hours of ASR.
            return True

        if recorded_size is not None and int(recorded_size) != int(current["sourceFileSize"]):
            _log.info("checkpoint invalidated: source size changed")
            return False

        if recorded_mtime is not None and abs(
            float(recorded_mtime) - float(current["sourceLastWriteUnix"])
        ) > 1.0:
            # One second of tolerance: FAT timestamps and some network shares round mtime.
            _log.info("checkpoint invalidated: source mtime changed")
            return False

        return True

    # -- transcription --------------------------------------------------------

    def load_transcription(self) -> dict[str, Any] | None:
        data = self._read(TRANSCRIPTION_FILE)
        if not isinstance(data, dict):
            return None
        if not isinstance(data.get("segments"), list) or not data["segments"]:
            _log.warning("transcription checkpoint has no segments; ignoring it")
            return None
        return data

    def save_transcription(self, result: Mapping[str, Any]) -> None:
        self._write(TRANSCRIPTION_FILE, dict(result))

    # -- translation ----------------------------------------------------------

    def load_partial_translation(self) -> dict[int, str]:
        """Translations completed so far, keyed by segment id. Absent means "nothing yet"."""
        data = self._read(PARTIAL_TRANSLATION_FILE)
        if not isinstance(data, dict):
            return {}

        result: dict[int, str] = {}
        for key, value in data.items():
            try:
                # JSON object keys are always strings; the pipeline keys by int.
                result[int(key)] = str(value)
            except (TypeError, ValueError):
                continue
        return result

    def save_partial_translation(self, translations: Mapping[int, str]) -> None:
        self._write(
            PARTIAL_TRANSLATION_FILE,
            {str(key): value for key, value in translations.items()},
        )

    # -- finalization ---------------------------------------------------------

    def load_finalization(self) -> dict[str, Any] | None:
        data = self._read(FINALIZATION_FILE)
        return data if isinstance(data, dict) else None

    def save_finalization(
        self,
        *,
        output_path: str | None,
        cue_count: int,
        skipped: bool = False,
        reason: str | None = None,
    ) -> None:
        self._write(
            FINALIZATION_FILE,
            {
                "jobId": self.job_id,
                "outputPath": output_path,
                "cueCount": cue_count,
                "skipped": skipped,
                "reason": reason,
            },
        )

    # -- housekeeping ---------------------------------------------------------

    def clear(self) -> None:
        """Delete every checkpoint file (but keep the directory and any extracted audio)."""
        self._unlink(JOB_FILE, TRANSCRIPTION_FILE, PARTIAL_TRANSLATION_FILE, FINALIZATION_FILE)

    def clear_translation(self) -> None:
        """Drop the translation only, leaving the audio and the transcript intact.

        ``finalization.json`` goes with it: it records the cues that were written, which were built
        from translations that are about to be redone.
        """
        self._unlink(PARTIAL_TRANSLATION_FILE, FINALIZATION_FILE)

    def clear_transcription(self) -> None:
        """Drop the transcript and, necessarily, the translation.

        Translations are keyed by segment id, and a re-run of ASR renumbers and re-cuts the
        segments. Keeping them would attach old Korean text to unrelated timecodes.
        """
        self._unlink(TRANSCRIPTION_FILE, PARTIAL_TRANSLATION_FILE, FINALIZATION_FILE)

    def clear_audio(self) -> None:
        self._unlink(AUDIO_FILE)

    def _unlink(self, *names: str) -> None:
        for name in names:
            try:
                self._path(name).unlink(missing_ok=True)
            except OSError as exc:
                _log.debug("could not delete %s: %r", name, exc)

    def audio_path(self) -> Path:
        """Canonical location of the extracted wav for this job."""
        return self.directory / AUDIO_FILE

    def has_audio(self) -> bool:
        """True when a non-empty extracted wav is on disk.

        Size is checked, not just existence: a kill during extraction leaves a zero-byte file that
        ffmpeg created but never filled, and handing that to Whisper produces an empty transcript
        rather than an error.
        """
        try:
            return self.audio_path().stat().st_size > 0
        except OSError:
            return False


def stale_artifacts(
    recorded: Mapping[str, Any] | None,
    *,
    audio: Mapping[str, Any],
    transcription: Mapping[str, Any],
    translation: Mapping[str, Any],
) -> set[str]:
    """Which cached artefacts the current settings invalidate.

    Returns a subset of ``{AUDIO, TRANSCRIPTION, TRANSLATION}``, already closed downstream: a stale
    audio track implies a stale transcript implies a stale translation, because each is derived
    from the one before it.

    A fingerprint the record does not carry is treated as **matching**. That is the same call
    :meth:`CheckpointStore.is_valid_for_source` makes for a missing source fingerprint, and for the
    same reason: a checkpoint written by an older build is not evidence that anything changed, and
    redoing an hour of ASR on that suspicion is the worse error.
    """
    if not recorded:
        return set()

    stale: set[str] = set()

    def drifted(key: str, current: Mapping[str, Any]) -> bool:
        previous = recorded.get(key)
        if not isinstance(previous, dict):
            return False
        # Structural comparison, not a hash of a canonical string: it sidesteps every key-ordering
        # and text-encoding question, and it leaves job.json readable when this has to be debugged.
        return previous != dict(current)

    if drifted(AUDIO_SETTINGS_KEY, audio):
        stale.update({AUDIO, TRANSCRIPTION, TRANSLATION})
    if drifted(TRANSCRIPTION_SETTINGS_KEY, transcription):
        stale.update({TRANSCRIPTION, TRANSLATION})
    if drifted(TRANSLATION_SETTINGS_KEY, translation):
        stale.add(TRANSLATION)

    return stale


def missing_ids(segments: list[dict[str, Any]], translated: Mapping[int, str]) -> list[int]:
    """Segment ids that still need translating, in transcript order."""
    return [
        int(s["id"])
        for s in segments
        if int(s.get("id", 0) or 0) not in translated
        or not str(translated.get(int(s.get("id", 0) or 0), "")).strip()
    ]

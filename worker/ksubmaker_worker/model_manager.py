"""Model install / verify / delete under the models directory.

Downloads are HTTPS-only, resumable (``Range`` into a ``.part`` file) and verified against the
SHA-256 the Hugging Face tree API reports for LFS blobs. Non-LFS blobs (small config/tokenizer
files) have no published digest, so those are hashed locally after download and the digest is
recorded in the manifest — which is what makes offline re-verification meaningful for them.

The file list is **discovered from the repository listing**, mirroring
``KSubMaker.Domain.Models.ModelFileSelector`` on the C# side. A hand-written list is a fallback,
never the source of truth: the app once shipped one that asked ``faster-whisper-large-v3`` for a
``vocabulary.txt`` it does not publish, and the only way anyone found out was a 404 in a user's
download dialog.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import threading
import time
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence
from urllib.parse import quote

from . import errors
from .cancellation import CancellationToken
from .errors import WorkerError
from .logging_setup import get_logger

_log = get_logger("models")

MANIFEST_NAME = ".ksubmaker-manifest.json"
PART_SUFFIX = ".part"
HF_ENDPOINT = "https://huggingface.co"
_CHUNK = 1024 * 256
_PROGRESS_INTERVAL_SECONDS = 0.5


# ---------------------------------------------------------------------------
# file selection — the mirror of ModelFileSelector.cs
# ---------------------------------------------------------------------------

#: Include pattern for a repository holding exactly one model (the CTranslate2 conversions).
ANY_FILE = r"^.+$"

#: A CTranslate2 model is unusable without its weights.
CT2_ESSENTIAL_FILE = r"^model\.bin$"

#: A llama.cpp model is unusable without at least one GGUF shard.
GGUF_ESSENTIAL_FILE = r"\.gguf$"

#: Repository furniture that is never part of a model: dotfiles, licences, markdown.
DEFAULT_EXCLUDE_PATTERN = r"^(?:.*/)?(?:\.[^/]+|licen[sc]e[^/]*|[^/]*\.md)$"

#: ``qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf`` → stem / index / total / extension.
_SHARD_RE = re.compile(r"^(?P<stem>.+)-(?P<index>\d+)-of-(?P<total>\d+)(?P<ext>\.[^.]+)$")


def select_repository_files(
    paths: Iterable[str],
    include_pattern: str,
    exclude_pattern: str = DEFAULT_EXCLUDE_PATTERN,
) -> list[str]:
    """The repository-relative paths that make up one model, in ordinal order.

    Ordinal order is load-bearing, not cosmetic: it is what puts ``…-00001-of-00002.gguf`` ahead
    of ``…-00002-of-00002.gguf`` so :func:`entry_point_file` picks the shard llama.cpp expects.
    """
    include = re.compile(include_pattern, re.IGNORECASE)
    exclude = re.compile(exclude_pattern, re.IGNORECASE)

    selected = {
        path
        for path in paths
        if path and path.strip() and include.match(path) and not exclude.match(path)
    }
    return sorted(selected)


def describe_selection_problem(
    repository_id: str,
    selected: Sequence[str],
    essential_pattern: str | None,
) -> str | None:
    """A Korean explanation of why ``selected`` cannot be downloaded, or ``None`` when it can.

    Downloading nothing and reporting success is the one outcome that must be impossible: the user
    would see "설치됨" on an empty folder and a model-load failure on the next job.
    """
    if not selected:
        return (
            f"저장소에서 내려받을 파일을 찾지 못했습니다: {repository_id}. "
            "저장소 주소가 바뀌었거나 파일 선택 규칙이 더 이상 맞지 않습니다."
        )

    if essential_pattern:
        essential = re.compile(essential_pattern, re.IGNORECASE)
        if not any(essential.search(name) for name in selected):
            return (
                f"저장소에 모델 본체 파일이 없습니다: {repository_id}. "
                f"필수 파일 규칙 '{essential_pattern}'과(와) 일치하는 파일이 하나도 없습니다."
            )

    return None


def entry_point_file(selected: Sequence[str], essential_pattern: str) -> str | None:
    """The one path handed to the inference engine for a single-file model.

    llama.cpp opens the remaining shards itself, so this must be the *first* shard — handing it
    ``…-00002-of-00002.gguf`` fails with a confusing "invalid magic" error.
    """
    essential = re.compile(essential_pattern, re.IGNORECASE)
    for name in sorted(selected):
        if essential.search(name):
            return name
    return None


def expand_shards(requested: Iterable[str], available: Iterable[str]) -> list[str]:
    """Replace requested names with the shards the repository actually publishes.

    Used when the caller gave a plain file list and no include pattern (the ``downloadModel``
    command does): ``qwen2.5-7b-instruct-q4_k_m.gguf`` does not exist in the Qwen 7B repository,
    but ``…-00001-of-00002.gguf`` and ``…-00002-of-00002.gguf`` do. Without this the download dies
    on a 404 for a name that was never real.
    """
    available_list = [name for name in available if name]
    if not available_list:
        # No listing to reason about; the caller's list is all we have.
        return list(requested)

    present = set(available_list)
    resolved: list[str] = []

    for name in requested:
        if name in present:
            resolved.append(name)
            continue

        shards = sorted(_shards_of(name, available_list))
        # A name with no shards either is kept as-is; reporting it is
        # ``missing_from_repository``'s job, not this function's.
        resolved.extend(shards or [name])

    return resolved


def missing_from_repository(requested: Iterable[str], available: Iterable[str]) -> list[str]:
    """Requested names the repository does not publish, after shard expansion."""
    available_list = [name for name in available if name]
    if not available_list:
        return []

    present = set(available_list)
    return [
        name
        for name in requested
        if name not in present and not _shards_of(name, available_list)
    ]


def _shards_of(name: str, available: Sequence[str]) -> list[str]:
    """Every ``<stem>-<n>-of-<m><ext>`` in ``available`` that is a split form of ``name``."""
    stem, _, extension = name.rpartition(".")
    if not stem:
        return []

    prefix = f"{stem}-"
    suffix = f".{extension}"

    shards: list[str] = []
    for candidate in available:
        if not candidate.startswith(prefix) or not candidate.endswith(suffix):
            continue
        if _SHARD_RE.match(candidate) is not None:
            shards.append(candidate)

    return shards


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


def models_root() -> Path:
    """Root of the models tree.

    ``KSUBMAKER_MODELS_DIR`` is set by the host when it launches the worker; the fallback keeps
    a developer run (and this test suite) working without it.
    """
    override = os.environ.get("KSUBMAKER_MODELS_DIR")
    if override:
        return Path(override)

    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "KSubMaker" / "models"

    return Path.home() / ".local" / "share" / "KSubMaker" / "models"


def model_directory(model_id: str, models_dir: Path | str | None = None) -> Path:
    root = Path(models_dir) if models_dir is not None else models_root()
    return root / _sanitize(model_id)


def _sanitize(component: str) -> str:
    """Same intent as ``AppPaths.Sanitize``: an id is never allowed to create a nested folder."""
    invalid = set('<>:"/\\|?*')
    return "".join("_" if ch in invalid or ord(ch) < 32 else ch for ch in component) or "_"


def find_local_model(model_id: str, models_dir: Path | str | None = None) -> Path | None:
    """A local model directory that actually contains weights, or None.

    Deliberately strict: an empty directory left behind by a cancelled download must not shadow
    the online fallback, otherwise the user gets "model load failed" instead of a download.
    """
    if not model_id or model_id == "auto":
        return None

    candidate = model_directory(model_id, models_dir)
    if _looks_like_model(candidate):
        return candidate

    # Tolerate a hub-style nested layout, e.g. models/<id>/snapshots/<sha>/.
    if candidate.is_dir():
        for child in sorted(candidate.rglob("*")):
            if child.is_dir() and _looks_like_model(child):
                return child

    return None


def _looks_like_model(path: Path) -> bool:
    if not path.is_dir():
        return False
    for name in ("model.bin", "model.safetensors"):
        if (path / name).is_file():
            return True
    return any(child.suffix == ".gguf" and child.is_file() for child in path.iterdir())


def find_local_file(model_id: str, models_dir: Path | str | None = None) -> Path | None:
    """Single-file model (GGUF) resolution.

    Returns the **first** shard when the quantisation is split (Qwen 7B q4_k_m is two files).
    llama.cpp reads the remaining shards itself, but only if it is pointed at shard one.
    """
    directory = model_directory(model_id, models_dir)
    if directory.is_dir():
        candidates = [child for child in directory.iterdir() if child.is_file() and child.suffix == ".gguf"]
        first = first_shard(candidates)
        if first is not None:
            return first

    root = Path(models_dir) if models_dir is not None else models_root()
    direct = root / f"{model_id}.gguf"
    return direct if direct.is_file() else None


def first_shard(paths: Sequence[Path]) -> Path | None:
    """Shard one of a split model, or the single file when it is not split."""
    ordered = sorted(paths, key=lambda path: path.name)

    for path in ordered:
        match = _SHARD_RE.match(path.name)
        if match is not None and int(match.group("index")) == 1:
            return path

    return ordered[0] if ordered else None


# ---------------------------------------------------------------------------
# manifest
# ---------------------------------------------------------------------------


def read_manifest(directory: Path) -> dict[str, Any]:
    path = directory / MANIFEST_NAME
    if not path.is_file():
        return {}
    try:
        with path.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        _log.warning("unreadable manifest at %s: %r", path, exc)
        return {}
    return data if isinstance(data, dict) else {}


def write_manifest(directory: Path, manifest: dict[str, Any]) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / MANIFEST_NAME
    temp = path.with_name(path.name + ".tmp")

    with temp.open("w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.flush()
        os.fsync(handle.fileno())

    os.replace(temp, path)


def sha256_file(path: Path, token: CancellationToken | None = None) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            if token is not None:
                token.raise_if_cancelled()
            chunk = handle.read(_CHUNK)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


# ---------------------------------------------------------------------------
# manager
# ---------------------------------------------------------------------------


class ModelManager:
    """Everything the ``listModels`` / ``downloadModel`` / ``verifyModel`` / ``deleteModel`` commands need."""

    def __init__(
        self,
        models_dir: Path | str | None = None,
        *,
        session_factory: Callable[[], Any] | None = None,
    ) -> None:
        self.models_dir = Path(models_dir) if models_dir is not None else models_root()
        self._session_factory = session_factory
        self._cancels: dict[str, CancellationToken] = {}
        self._lock = threading.Lock()

    # -- session ---------------------------------------------------------------

    def _session(self) -> Any:
        if self._session_factory is not None:
            return self._session_factory()

        try:
            import requests  # noqa: PLC0415 - lazy so the module imports without it
        except ImportError as exc:  # pragma: no cover - requests is a declared dependency
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                "다운로드 구성 요소를 불러오지 못했습니다. 설치가 손상되었을 수 있습니다.",
                detail=repr(exc),
            ) from exc

        session = requests.Session()
        session.headers.update({"User-Agent": "KSubMaker-Worker/1.0"})
        token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN")
        if token:
            session.headers["Authorization"] = f"Bearer {token}"
        return session

    # -- listing ---------------------------------------------------------------

    def list_models(self, model_ids: Iterable[str] | None = None) -> list[dict[str, Any]]:
        """Enumerate installed models. Never raises: a bad directory becomes one entry with a message."""
        entries: list[dict[str, Any]] = []
        seen: set[str] = set()

        if model_ids:
            for model_id in model_ids:
                entries.append(self.describe(model_id))
                seen.add(_sanitize(model_id))

        if self.models_dir.is_dir():
            try:
                children = sorted(child for child in self.models_dir.iterdir() if child.is_dir())
            except OSError as exc:
                _log.warning("could not list %s: %r", self.models_dir, exc)
                children = []

            for child in children:
                if child.name in seen:
                    continue
                entries.append(self.describe(child.name))

        return entries

    def describe(self, model_id: str) -> dict[str, Any]:
        directory = model_directory(model_id, self.models_dir)
        manifest = read_manifest(directory)

        size = 0
        downloaded = 0
        if directory.is_dir():
            for path in directory.rglob("*"):
                if not path.is_file():
                    continue
                try:
                    length = path.stat().st_size
                except OSError:
                    continue
                if path.name.endswith(PART_SUFFIX):
                    downloaded += length
                elif path.name != MANIFEST_NAME:
                    size += length

        installed = bool(manifest.get("complete")) and size > 0
        if not installed and size > 0 and _looks_like_model(directory):
            # Installed by an older build (or copied in by hand) with no manifest: still usable,
            # just not verifiable until the user runs a verify.
            installed = True

        message: str | None = None
        if not installed and downloaded > 0:
            message = "다운로드가 중단되었습니다. 다시 시작하면 이어서 내려받습니다."

        return {
            "modelId": model_id,
            "path": str(directory) if directory.exists() else None,
            "installed": installed,
            "verified": bool(manifest.get("verified")),
            "sizeBytes": size,
            "downloadedBytes": downloaded + size,
            "message": message,
        }

    # -- download --------------------------------------------------------------

    def cancel_download(self, model_id: str) -> bool:
        with self._lock:
            token = self._cancels.get(model_id)
        if token is None:
            return False
        token.cancel()
        return True

    def download(
        self,
        *,
        model_id: str,
        repository_id: str,
        files: list[str],
        target_dir: str | Path | None = None,
        token: CancellationToken | None = None,
        on_progress: Callable[[int, int, str, float], None] | None = None,
        include_pattern: str | None = None,
        essential_pattern: str | None = None,
    ) -> dict[str, Any]:
        """Download every file of a model, resuming any ``.part`` already on disk.

        ``files`` is the **fallback** list. When ``include_pattern`` is given the real list is
        selected from the repository listing instead and ``files`` is only used if that listing
        cannot be fetched. Without a pattern the requested names are still checked against the
        listing and expanded to their shards, because "the file you asked for does not exist" is
        worth saying up front rather than 404-ing three files into a multi-gigabyte download.

        Returns the ``downloadCompleted`` payload. Raises :class:`WorkerError` on failure; a
        cancellation yields ``cancelled: True`` rather than an error, because the user asked.
        """
        if not files and not include_pattern:
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                "내려받을 파일 목록이 비어 있습니다.",
                detail=f"empty file list for {model_id}",
            )

        directory = Path(target_dir) if target_dir else model_directory(model_id, self.models_dir)
        directory.mkdir(parents=True, exist_ok=True)

        token = token or CancellationToken(f"download:{model_id}")
        with self._lock:
            self._cancels[model_id] = token

        session = self._session()
        manifest = read_manifest(directory)
        manifest.setdefault("modelId", model_id)
        manifest["repositoryId"] = repository_id
        manifest["complete"] = False
        manifest.setdefault("files", {})

        try:
            digests = self._fetch_digests(session, repository_id)
            sizes = self._fetch_sizes(session, repository_id)
            resolved = self._resolve_files(
                session=session,
                repository_id=repository_id,
                files=files,
                include_pattern=include_pattern,
                essential_pattern=essential_pattern,
            )
            total_expected = sum(sizes.get(name, 0) for name in resolved)
            received_total = 0
            started = time.monotonic()

            for name in resolved:
                token.raise_if_cancelled()

                destination = directory / name
                destination.parent.mkdir(parents=True, exist_ok=True)

                expected_sha = digests.get(name)
                recorded = manifest["files"].get(name) or {}

                if destination.is_file() and recorded.get("sha256"):
                    if expected_sha is None or recorded["sha256"] == expected_sha:
                        received_total += destination.stat().st_size
                        _log.info("%s already present; skipping", name)
                        continue

                def report(chunk_bytes: int, _name: str = name) -> None:
                    if on_progress is None:
                        return
                    elapsed = max(1e-6, time.monotonic() - started)
                    on_progress(
                        received_total + chunk_bytes,
                        max(total_expected, received_total + chunk_bytes),
                        _name,
                        (received_total + chunk_bytes) / elapsed,
                    )

                written = self._download_file(
                    session=session,
                    repository_id=repository_id,
                    name=name,
                    destination=destination,
                    token=token,
                    on_chunk=report,
                )
                received_total += written

                actual_sha = sha256_file(destination, token)
                if expected_sha and actual_sha != expected_sha:
                    destination.unlink(missing_ok=True)
                    raise WorkerError(
                        errors.MODEL_VERIFICATION_FAILED,
                        f"내려받은 파일이 손상되었습니다: {name}. 다시 시도하세요.",
                        detail=f"sha256 mismatch for {name}: expected {expected_sha}, got {actual_sha}",
                    )

                manifest["files"][name] = {
                    "sha256": actual_sha,
                    "sizeBytes": destination.stat().st_size,
                    # False = we hashed it ourselves because the hub published no LFS digest.
                    "digestFromHub": expected_sha is not None,
                }
                write_manifest(directory, manifest)

            manifest["complete"] = True
            manifest["verified"] = True
            manifest["completedAt"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            write_manifest(directory, manifest)

            return {
                "modelId": model_id,
                "path": str(directory),
                "verified": True,
                "totalBytes": received_total,
                "cancelled": False,
            }

        except errors.CancelledError:
            _log.info("download of %s cancelled", model_id)
            write_manifest(directory, manifest)
            return {
                "modelId": model_id,
                "path": str(directory),
                "verified": False,
                "totalBytes": 0,
                "cancelled": True,
            }
        finally:
            with self._lock:
                self._cancels.pop(model_id, None)
            closer = getattr(session, "close", None)
            if callable(closer):
                try:
                    closer()
                except OSError as exc:  # pragma: no cover - defensive
                    _log.debug("session close failed: %r", exc)

    def _download_file(
        self,
        *,
        session: Any,
        repository_id: str,
        name: str,
        destination: Path,
        token: CancellationToken,
        on_chunk: Callable[[int], None],
    ) -> int:
        url = self.file_url(repository_id, name)
        _require_https(url)

        part = destination.with_name(destination.name + PART_SUFFIX)
        existing = part.stat().st_size if part.is_file() else 0

        headers: dict[str, str] = {}
        if existing > 0:
            headers["Range"] = f"bytes={existing}-"
            _log.info("resuming %s at %d bytes", name, existing)

        try:
            response = session.get(url, headers=headers, stream=True, timeout=60, allow_redirects=True)
        except Exception as exc:  # noqa: BLE001 - requests raises a family of connection errors
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                f"모델 파일을 내려받지 못했습니다: {name}. 네트워크 연결을 확인하세요.",
                detail=repr(exc),
            ) from exc

        status = int(getattr(response, "status_code", 0))

        if existing > 0 and status == 200:
            # The server ignored our Range header; start over rather than concatenating garbage.
            _log.warning("server ignored Range for %s; restarting the download", name)
            existing = 0
            part.unlink(missing_ok=True)
        elif existing > 0 and status == 416:
            # Already have the whole file.
            _finish_part(part, destination)
            _close(response)
            return existing

        if status not in (200, 206):
            _close(response)
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                f"모델 파일을 내려받지 못했습니다: {name} (HTTP {status})",
                detail=f"unexpected status {status} for {url}",
            )

        mode = "ab" if existing > 0 else "wb"
        written = existing

        try:
            with part.open(mode) as handle:
                for chunk in response.iter_content(chunk_size=_CHUNK):
                    if token.cancelled:
                        handle.flush()
                        raise errors.CancelledError()
                    if not chunk:
                        continue
                    handle.write(chunk)
                    written += len(chunk)
                    on_chunk(written - existing)
                handle.flush()
                os.fsync(handle.fileno())
        except errors.CancelledError:
            raise
        except OSError as exc:
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                f"모델 파일을 저장하지 못했습니다: {name}",
                detail=repr(exc),
            ) from exc
        except Exception as exc:  # noqa: BLE001 - stream errors mid-download
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                f"모델 파일 전송이 중단되었습니다: {name}. 다시 시도하면 이어서 내려받습니다.",
                detail=repr(exc),
            ) from exc
        finally:
            _close(response)

        _finish_part(part, destination)
        return written

    @staticmethod
    def file_url(repository_id: str, name: str) -> str:
        safe = "/".join(quote(part) for part in name.split("/"))
        return f"{HF_ENDPOINT}/{repository_id}/resolve/main/{safe}?download=true"

    def _resolve_files(
        self,
        *,
        session: Any,
        repository_id: str,
        files: Sequence[str],
        include_pattern: str | None,
        essential_pattern: str | None,
    ) -> list[str]:
        """Decide what to download: the repository listing wins, ``files`` is the offline fallback."""
        available = [
            entry["path"]
            for entry in self._tree(session, repository_id)
            if isinstance(entry.get("path"), str) and entry.get("type", "file") == "file"
        ]

        if include_pattern:
            if available:
                resolved = select_repository_files(available, include_pattern)
                _log.info("%s: %d file(s) selected from the repository listing", repository_id, len(resolved))
            else:
                resolved = select_repository_files(files, include_pattern)
                _log.warning("%s: no repository listing; falling back to the caller's file list", repository_id)
        else:
            # No pattern: honour the caller's list, but let the listing correct split-shard names.
            resolved = expand_shards(files, available)

            missing = missing_from_repository(files, available)
            if missing:
                raise WorkerError(
                    errors.MODEL_DOWNLOAD_FAILED,
                    f"저장소에 없는 파일을 요청했습니다: {repository_id} ({', '.join(missing)}). "
                    "모델 목록이 최신인지 확인하세요.",
                    detail=f"{repository_id} does not publish {missing!r}",
                )

        problem = describe_selection_problem(repository_id, resolved, essential_pattern)
        if problem is not None:
            raise WorkerError(
                errors.MODEL_DOWNLOAD_FAILED,
                problem,
                detail=f"file selection for {repository_id} resolved to {resolved!r}",
            )

        return resolved

    def _fetch_digests(self, session: Any, repository_id: str) -> dict[str, str]:
        """Per-file SHA-256 from the hub tree API. Only LFS blobs publish one."""
        digests: dict[str, str] = {}
        for entry in self._tree(session, repository_id):
            lfs = entry.get("lfs")
            if not isinstance(lfs, dict):
                continue
            sha = lfs.get("sha256") or lfs.get("oid")
            path = entry.get("path")
            if isinstance(sha, str) and isinstance(path, str):
                digests[path] = sha.removeprefix("sha256:")
        return digests

    def _fetch_sizes(self, session: Any, repository_id: str) -> dict[str, int]:
        sizes: dict[str, int] = {}
        for entry in self._tree(session, repository_id):
            path = entry.get("path")
            if not isinstance(path, str):
                continue
            lfs = entry.get("lfs")
            size = lfs.get("size") if isinstance(lfs, dict) else entry.get("size")
            if isinstance(size, int):
                sizes[path] = size
        return sizes

    def _tree(self, session: Any, repository_id: str) -> list[dict[str, Any]]:
        """Cached repository listing. A failure here is non-fatal: we just lose the digests."""
        cache_key = f"_tree_cache_{repository_id}"
        cached = getattr(self, cache_key, None)
        if cached is not None:
            return cached

        url = f"{HF_ENDPOINT}/api/models/{repository_id}/tree/main?recursive=1"
        _require_https(url)

        entries: list[dict[str, Any]] = []
        try:
            response = session.get(url, timeout=30)
            if int(getattr(response, "status_code", 0)) == 200:
                payload = response.json()
                if isinstance(payload, list):
                    entries = [item for item in payload if isinstance(item, dict)]
            else:
                _log.warning("tree API for %s returned %s", repository_id, response.status_code)
            _close(response)
        except Exception as exc:  # noqa: BLE001
            _log.warning("could not read the file tree for %s: %r", repository_id, exc)

        setattr(self, cache_key, entries)
        return entries

    # -- verify / delete -------------------------------------------------------

    def verify(
        self,
        model_id: str,
        target_dir: str | Path | None = None,
        token: CancellationToken | None = None,
    ) -> dict[str, Any]:
        """Re-hash every manifest entry offline. No network access whatsoever."""
        directory = Path(target_dir) if target_dir else model_directory(model_id, self.models_dir)

        if not directory.is_dir():
            return {
                "modelId": model_id,
                "path": None,
                "installed": False,
                "verified": False,
                "sizeBytes": 0,
                "downloadedBytes": 0,
                "message": "설치된 모델을 찾을 수 없습니다.",
            }

        manifest = read_manifest(directory)
        recorded = manifest.get("files")
        if not isinstance(recorded, dict) or not recorded:
            return {
                "modelId": model_id,
                "path": str(directory),
                "installed": _looks_like_model(directory),
                "verified": False,
                "sizeBytes": _directory_size(directory),
                "downloadedBytes": _directory_size(directory),
                "message": "검증 정보를 찾을 수 없습니다. 모델을 다시 내려받으세요.",
            }

        problems: list[str] = []
        total = 0

        for name, meta in recorded.items():
            if token is not None:
                token.raise_if_cancelled()

            path = directory / name
            if not path.is_file():
                problems.append(f"{name}: 파일 없음")
                continue

            total += path.stat().st_size
            expected = (meta or {}).get("sha256")
            if not expected:
                problems.append(f"{name}: 해시 정보 없음")
                continue

            actual = sha256_file(path, token)
            if actual != expected:
                problems.append(f"{name}: 해시 불일치")

        verified = not problems
        manifest["verified"] = verified
        write_manifest(directory, manifest)

        return {
            "modelId": model_id,
            "path": str(directory),
            "installed": True,
            "verified": verified,
            "sizeBytes": total,
            "downloadedBytes": total,
            "message": None if verified else "검증 실패: " + ", ".join(problems[:5]),
        }

    def delete(self, model_id: str, target_dir: str | Path | None = None) -> dict[str, Any]:
        directory = Path(target_dir) if target_dir else model_directory(model_id, self.models_dir)

        if not directory.exists():
            return {
                "modelId": model_id,
                "path": None,
                "installed": False,
                "verified": False,
                "sizeBytes": 0,
                "downloadedBytes": 0,
                "message": "이미 삭제되었습니다.",
            }

        size = _directory_size(directory)

        try:
            shutil.rmtree(directory)
        except OSError as exc:
            raise WorkerError(
                errors.OUTPUT_WRITE_FAILED,
                f"모델 폴더를 삭제하지 못했습니다: {directory.name}. 다른 프로그램이 사용 중일 수 있습니다.",
                detail=repr(exc),
            ) from exc

        return {
            "modelId": model_id,
            "path": None,
            "installed": False,
            "verified": False,
            "sizeBytes": 0,
            "downloadedBytes": 0,
            "message": f"{size} 바이트를 삭제했습니다.",
        }


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------


def _require_https(url: str) -> None:
    if not url.lower().startswith("https://"):
        raise WorkerError(
            errors.MODEL_DOWNLOAD_FAILED,
            "안전하지 않은 주소에서는 모델을 내려받지 않습니다.",
            detail=f"refusing non-HTTPS url {url}",
        )


def _finish_part(part: Path, destination: Path) -> None:
    if part.is_file():
        os.replace(part, destination)


def _close(response: Any) -> None:
    closer = getattr(response, "close", None)
    if callable(closer):
        try:
            closer()
        except OSError as exc:  # pragma: no cover - defensive
            _log.debug("response close failed: %r", exc)


def _directory_size(directory: Path) -> int:
    total = 0
    for path in directory.rglob("*"):
        if path.is_file():
            try:
                total += path.stat().st_size
            except OSError:
                continue
    return total

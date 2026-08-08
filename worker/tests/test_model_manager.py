"""Model listing, resumable download, verification and deletion.

The HTTP session is always a fake: no test here touches the network.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import errors
from ksubmaker_worker.cancellation import CancellationToken
from ksubmaker_worker.model_manager import (
    ANY_FILE,
    CT2_ESSENTIAL_FILE,
    GGUF_ESSENTIAL_FILE,
    MANIFEST_NAME,
    ModelManager,
    describe_selection_problem,
    entry_point_file,
    expand_shards,
    find_local_file,
    find_local_model,
    first_shard,
    missing_from_repository,
    model_directory,
    models_root,
    read_manifest,
    select_repository_files,
    sha256_file,
)

#: Verbatim hub listings captured on 2026-08-02; see the README next to them.
FIXTURES = Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "huggingface"

QWEN_7B_Q4KM = r"^qwen2\.5-7b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$"
QWEN_3B_Q4KM = r"^qwen2\.5-3b-instruct-q4_k_m(?:-\d+-of-\d+)?\.gguf$"


def listing(repository_id: str) -> list[str]:
    """Repository-relative paths of a captured listing."""
    path = FIXTURES / (repository_id.replace("/", "__") + ".json")
    entries = json.loads(path.read_text(encoding="utf-8"))
    return [entry["path"] for entry in entries if entry.get("type", "file") == "file"]

# ---------------------------------------------------------------------------
# fake transport
# ---------------------------------------------------------------------------


class FakeResponse:
    def __init__(self, *, status: int = 200, body: bytes = b"", payload: Any = None) -> None:
        self.status_code = status
        self._body = body
        self._payload = payload
        self.text = body.decode("utf-8", "replace")

    def iter_content(self, chunk_size: int = 1024):  # noqa: ANN201
        for offset in range(0, len(self._body), chunk_size):
            yield self._body[offset : offset + chunk_size]

    def json(self) -> Any:
        if self._payload is None:
            raise ValueError("no json")
        return self._payload

    def close(self) -> None:
        return None


class FakeSession:
    """Serves a fixed set of files, honouring Range requests."""

    def __init__(self, files: dict[str, bytes], *, publish_digests: bool = True) -> None:
        self.files = files
        self.publish_digests = publish_digests
        self.requests: list[tuple[str, dict[str, str]]] = []
        self.ignore_range = False
        self.fail_status: int | None = None

    def get(self, url: str, headers: dict[str, str] | None = None, **_kwargs: Any) -> FakeResponse:
        headers = headers or {}
        self.requests.append((url, headers))

        if "/api/models/" in url:
            if not self.publish_digests:
                return FakeResponse(status=404)
            tree = [
                {
                    "path": name,
                    "size": len(body),
                    "lfs": {"sha256": hashlib.sha256(body).hexdigest(), "size": len(body)},
                }
                for name, body in self.files.items()
            ]
            return FakeResponse(payload=tree)

        if self.fail_status is not None:
            return FakeResponse(status=self.fail_status, body=b"nope")

        name = url.split("/resolve/main/")[-1].split("?")[0]
        body = self.files.get(name)
        if body is None:
            return FakeResponse(status=404, body=b"not found")

        range_header = headers.get("Range")
        if range_header and not self.ignore_range:
            start = int(range_header.removeprefix("bytes=").split("-")[0])
            if start >= len(body):
                return FakeResponse(status=416)
            return FakeResponse(status=206, body=body[start:])

        return FakeResponse(status=200, body=body)

    def close(self) -> None:
        return None


FILES = {"config.json": b'{"model": "test"}', "model.bin": b"\x00\x11" * 5000}


def _manager(tmp_path: Path, session: FakeSession) -> ModelManager:
    return ModelManager(tmp_path, session_factory=lambda: session)


# ---------------------------------------------------------------------------
# paths
# ---------------------------------------------------------------------------


def test_models_root_honours_the_environment(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("KSUBMAKER_MODELS_DIR", str(tmp_path / "custom"))
    assert models_root() == tmp_path / "custom"


def test_model_ids_cannot_escape_the_models_directory(tmp_path: Path) -> None:
    directory = model_directory("evil/../../id", tmp_path)
    assert directory.parent == tmp_path
    assert "/" not in directory.name


def test_find_local_model_requires_actual_weights(tmp_path: Path) -> None:
    empty = tmp_path / "whisper-small"
    empty.mkdir()

    # An empty directory left by a cancelled download must not shadow the download path.
    assert find_local_model("whisper-small", tmp_path) is None

    (empty / "model.bin").write_bytes(b"weights")
    assert find_local_model("whisper-small", tmp_path) == empty


def test_find_local_model_walks_a_nested_snapshot_layout(tmp_path: Path) -> None:
    nested = tmp_path / "whisper-small" / "snapshots" / "abc123"
    nested.mkdir(parents=True)
    (nested / "model.bin").write_bytes(b"weights")

    assert find_local_model("whisper-small", tmp_path) == nested


def test_find_local_model_ignores_auto(tmp_path: Path) -> None:
    assert find_local_model("auto", tmp_path) is None
    assert find_local_model("", tmp_path) is None


def test_find_local_file_locates_a_gguf(tmp_path: Path) -> None:
    directory = tmp_path / "qwen"
    directory.mkdir()
    gguf = directory / "qwen2.5-3b.gguf"
    gguf.write_bytes(b"GGUF")

    assert find_local_file("qwen", tmp_path) == gguf


def test_find_local_file_returns_the_first_shard(tmp_path: Path) -> None:
    directory = tmp_path / "qwen2.5-7b-instruct-q4km"
    directory.mkdir()
    second = directory / "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf"
    first = directory / "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf"
    second.write_bytes(b"GGUF-2")
    first.write_bytes(b"GGUF-1")

    # llama.cpp opens the remaining shards itself, but only when handed shard one.
    assert find_local_file("qwen2.5-7b-instruct-q4km", tmp_path) == first


def test_first_shard_of_an_unsplit_model_is_the_file_itself(tmp_path: Path) -> None:
    only = tmp_path / "qwen2.5-3b-instruct-q4_k_m.gguf"
    only.write_bytes(b"GGUF")

    assert first_shard([only]) == only
    assert first_shard([]) is None


# ---------------------------------------------------------------------------
# file selection (mirrors ModelFileSelectorTests.cs)
# ---------------------------------------------------------------------------


def test_a_ct2_repository_selects_everything_that_is_not_furniture() -> None:
    selected = select_repository_files(listing("Systran/faster-whisper-large-v3"), ANY_FILE)

    # The hardcoded list used to say vocabulary.txt here, which 404s on this repository.
    assert selected == [
        "config.json",
        "model.bin",
        "preprocessor_config.json",
        "tokenizer.json",
        "vocabulary.json",
    ]


def test_readme_gitattributes_and_licence_files_are_excluded() -> None:
    selected = select_repository_files(listing("entai2965/nllb-200-distilled-600M-ctranslate2"), ANY_FILE)

    assert selected == [
        "config.json",
        "model.bin",
        "sentencepiece.bpe.model",
        "shared_vocabulary.json",
        "special_tokens_map.json",
        "tokenizer.json",
        "tokenizer_config.json",
    ]
    assert not any(name in selected for name in (".gitattributes", "README.md", "LICENSE.model.md"))


def test_a_split_quantisation_selects_every_shard_in_order() -> None:
    selected = select_repository_files(listing("Qwen/Qwen2.5-7B-Instruct-GGUF"), QWEN_7B_Q4KM)

    assert selected == [
        "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf",
        "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf",
    ]
    assert entry_point_file(selected, GGUF_ESSENTIAL_FILE) == selected[0]


def test_an_unsplit_quantisation_selects_exactly_one_file() -> None:
    selected = select_repository_files(listing("Qwen/Qwen2.5-3B-Instruct-GGUF"), QWEN_3B_Q4KM)

    assert selected == ["qwen2.5-3b-instruct-q4_k_m.gguf"]
    assert entry_point_file(selected, GGUF_ESSENTIAL_FILE) == selected[0]


def test_the_quant_pattern_does_not_pick_up_neighbouring_quantisations() -> None:
    selected = select_repository_files(listing("Qwen/Qwen2.5-7B-Instruct-GGUF"), QWEN_7B_Q4KM)

    assert not any("q5_k_m" in name or "q4_0" in name or "fp16" in name for name in selected)


def test_an_empty_selection_is_described_as_a_problem() -> None:
    problem = describe_selection_problem("acme/gone", [], CT2_ESSENTIAL_FILE)

    assert problem is not None
    assert "acme/gone" in problem


def test_a_selection_without_weights_is_described_as_a_problem() -> None:
    problem = describe_selection_problem("acme/tokenizer-only", ["tokenizer.json"], CT2_ESSENTIAL_FILE)

    assert problem is not None
    assert "acme/tokenizer-only" in problem


def test_a_healthy_selection_has_no_problem() -> None:
    assert describe_selection_problem("acme/ok", ["config.json", "model.bin"], CT2_ESSENTIAL_FILE) is None


def test_expand_shards_replaces_a_name_the_repository_split() -> None:
    available = listing("Qwen/Qwen2.5-7B-Instruct-GGUF")

    assert expand_shards(["qwen2.5-7b-instruct-q4_k_m.gguf"], available) == [
        "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf",
        "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf",
    ]
    assert missing_from_repository(["qwen2.5-7b-instruct-q4_k_m.gguf"], available) == []


def test_expand_shards_without_a_listing_keeps_the_caller_list() -> None:
    assert expand_shards(["model.bin"], []) == ["model.bin"]


def test_missing_from_repository_reports_a_name_that_does_not_exist() -> None:
    available = listing("Systran/faster-whisper-large-v3")

    assert missing_from_repository(["vocabulary.txt"], available) == ["vocabulary.txt"]


# ---------------------------------------------------------------------------
# download
# ---------------------------------------------------------------------------


def test_download_writes_every_file_and_a_manifest(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    result = _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=list(FILES)
    )

    assert result["cancelled"] is False
    assert result["verified"] is True

    directory = Path(result["path"])
    for name, body in FILES.items():
        assert (directory / name).read_bytes() == body

    manifest = read_manifest(directory)
    assert manifest["complete"] is True
    assert set(manifest["files"]) == set(FILES)
    assert manifest["files"]["model.bin"]["digestFromHub"] is True


def test_download_uses_https_only(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=["config.json"]
    )

    assert all(url.startswith("https://") for url, _ in session.requests)


def test_no_part_file_is_left_behind(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    result = _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=list(FILES)
    )

    assert not list(Path(result["path"]).glob("*.part"))


def test_download_resumes_from_a_part_file(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    directory = model_directory("test-model", tmp_path)
    directory.mkdir(parents=True)

    # Pretend a previous run got half of model.bin.
    half = len(FILES["model.bin"]) // 2
    (directory / "model.bin.part").write_bytes(FILES["model.bin"][:half])

    _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=["model.bin"]
    )

    ranges = [headers.get("Range") for url, headers in session.requests if "model.bin" in url]
    assert f"bytes={half}-" in ranges
    assert (directory / "model.bin").read_bytes() == FILES["model.bin"]


def test_a_server_that_ignores_range_restarts_cleanly(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    session.ignore_range = True

    directory = model_directory("test-model", tmp_path)
    directory.mkdir(parents=True)
    (directory / "model.bin.part").write_bytes(b"stale partial data")

    _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=["model.bin"]
    )

    # Concatenating onto stale data would corrupt the file; restarting is the only safe move.
    assert (directory / "model.bin").read_bytes() == FILES["model.bin"]


def test_an_already_complete_file_is_skipped(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    manager = _manager(tmp_path, session)

    manager.download(model_id="test-model", repository_id="acme/test", files=["config.json"])
    before = len(session.requests)

    manager.download(model_id="test-model", repository_id="acme/test", files=["config.json"])
    after = len(session.requests)

    # Only the tree API call, no re-download.
    assert after - before <= 1


def test_a_corrupt_download_is_rejected_and_deleted(tmp_path: Path) -> None:
    class LyingSession(FakeSession):
        def get(self, url, headers=None, **kwargs):  # noqa: ANN001, ANN201
            if "/api/models/" in url:
                return FakeResponse(
                    payload=[{"path": "model.bin", "size": 8, "lfs": {"sha256": "0" * 64}}]
                )
            return super().get(url, headers, **kwargs)

    session = LyingSession(FILES)
    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, session).download(
            model_id="test-model", repository_id="acme/test", files=["model.bin"]
        )

    assert excinfo.value.code == errors.MODEL_VERIFICATION_FAILED
    assert not (model_directory("test-model", tmp_path) / "model.bin").exists()


def test_a_http_error_becomes_model_download_failed(tmp_path: Path) -> None:
    session = FakeSession(FILES)
    session.fail_status = 503

    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, session).download(
            model_id="test-model", repository_id="acme/test", files=["model.bin"]
        )

    assert excinfo.value.code == errors.MODEL_DOWNLOAD_FAILED


def test_an_empty_file_list_is_rejected(tmp_path: Path) -> None:
    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, FakeSession(FILES)).download(
            model_id="test-model", repository_id="acme/test", files=[]
        )

    assert excinfo.value.code == errors.MODEL_DOWNLOAD_FAILED


def test_an_include_pattern_discovers_the_file_list(tmp_path: Path) -> None:
    """The caller's list is wrong on purpose: the repository listing has to win."""
    published = {
        "README.md": b"# model card",
        ".gitattributes": b"* filter=lfs",
        "config.json": b'{"model": "test"}',
        "model.bin": b"\x00\x11" * 5000,
        "vocabulary.json": b"[]",
    }
    session = FakeSession(published)

    result = _manager(tmp_path, session).download(
        model_id="test-model",
        repository_id="acme/test",
        files=["config.json", "model.bin", "vocabulary.txt"],
        include_pattern=ANY_FILE,
        essential_pattern=CT2_ESSENTIAL_FILE,
    )

    directory = Path(result["path"])
    assert sorted(p.name for p in directory.iterdir() if p.name != MANIFEST_NAME) == [
        "config.json",
        "model.bin",
        "vocabulary.json",
    ]
    assert not (directory / "vocabulary.txt").exists()
    assert not (directory / "README.md").exists()


def test_the_caller_list_is_the_fallback_when_the_listing_is_unavailable(tmp_path: Path) -> None:
    session = FakeSession(FILES, publish_digests=False)  # the tree API answers 404

    result = _manager(tmp_path, session).download(
        model_id="test-model",
        repository_id="acme/test",
        files=list(FILES),
        include_pattern=ANY_FILE,
        essential_pattern=CT2_ESSENTIAL_FILE,
    )

    directory = Path(result["path"])
    for name in FILES:
        assert (directory / name).is_file()


def test_a_selection_that_resolves_to_nothing_fails_loudly(tmp_path: Path) -> None:
    session = FakeSession({"README.md": b"# nothing else here"})

    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, session).download(
            model_id="test-model",
            repository_id="acme/empty",
            files=["model.bin"],
            include_pattern=ANY_FILE,
            essential_pattern=CT2_ESSENTIAL_FILE,
        )

    assert excinfo.value.code == errors.MODEL_DOWNLOAD_FAILED
    assert "acme/empty" in excinfo.value.message
    # Nothing downloaded means nothing installed; a manifest here would be a lie.
    assert not (model_directory("test-model", tmp_path) / MANIFEST_NAME).exists()


def test_a_selection_without_weights_fails_loudly(tmp_path: Path) -> None:
    session = FakeSession({"tokenizer.json": b"{}", "config.json": b"{}"})

    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, session).download(
            model_id="test-model",
            repository_id="acme/no-weights",
            files=["config.json"],
            include_pattern=ANY_FILE,
            essential_pattern=CT2_ESSENTIAL_FILE,
        )

    assert excinfo.value.code == errors.MODEL_DOWNLOAD_FAILED
    assert "acme/no-weights" in excinfo.value.message


def test_a_split_gguf_is_downloaded_whole_without_a_pattern(tmp_path: Path) -> None:
    """The host asks for the unsplit name; the repository only has shards."""
    published = {
        "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf": b"GGUF-part-1",
        "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf": b"GGUF-part-2",
    }
    session = FakeSession(published)

    result = _manager(tmp_path, session).download(
        model_id="qwen2.5-7b-instruct-q4km",
        repository_id="Qwen/Qwen2.5-7B-Instruct-GGUF",
        files=["qwen2.5-7b-instruct-q4_k_m.gguf"],
    )

    directory = Path(result["path"])
    for name in published:
        assert (directory / name).is_file()

    assert find_local_file("qwen2.5-7b-instruct-q4km", tmp_path) == (
        directory / "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf"
    )


def test_asking_for_a_file_the_repository_does_not_have_fails_before_downloading(tmp_path: Path) -> None:
    session = FakeSession({"config.json": b"{}", "model.bin": b"\x00", "vocabulary.json": b"[]"})

    with pytest.raises(errors.WorkerError) as excinfo:
        _manager(tmp_path, session).download(
            model_id="whisper-large-v3",
            repository_id="Systran/faster-whisper-large-v3",
            files=["config.json", "model.bin", "vocabulary.txt"],
        )

    assert excinfo.value.code == errors.MODEL_DOWNLOAD_FAILED
    assert "vocabulary.txt" in excinfo.value.message
    assert "Systran/faster-whisper-large-v3" in excinfo.value.message
    # Only the tree call went out; no file was fetched before the mistake was caught.
    assert all("/resolve/main/" not in url for url, _ in session.requests)


def test_non_lfs_files_are_hashed_locally(tmp_path: Path) -> None:
    session = FakeSession(FILES, publish_digests=False)
    result = _manager(tmp_path, session).download(
        model_id="test-model", repository_id="acme/test", files=["config.json"]
    )

    manifest = read_manifest(Path(result["path"]))
    entry = manifest["files"]["config.json"]
    assert entry["digestFromHub"] is False
    assert entry["sha256"] == hashlib.sha256(FILES["config.json"]).hexdigest()


def test_progress_is_reported(tmp_path: Path) -> None:
    seen: list[tuple[int, int, str]] = []
    _manager(tmp_path, FakeSession(FILES)).download(
        model_id="test-model",
        repository_id="acme/test",
        files=list(FILES),
        on_progress=lambda received, total, name, _speed: seen.append((received, total, name)),
    )

    assert seen
    assert [s[0] for s in seen] == sorted(s[0] for s in seen)


def test_cancelling_a_download_reports_cancelled_not_an_error(tmp_path: Path) -> None:
    token = CancellationToken("d")
    token.cancel()

    result = _manager(tmp_path, FakeSession(FILES)).download(
        model_id="test-model", repository_id="acme/test", files=list(FILES), token=token
    )

    assert result["cancelled"] is True
    assert result["verified"] is False


def test_cancel_download_finds_a_running_download(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    assert manager.cancel_download("not-running") is False


# ---------------------------------------------------------------------------
# verify
# ---------------------------------------------------------------------------


def test_verify_passes_for_an_intact_install(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    result = manager.verify("test-model")

    assert result["verified"] is True
    assert result["installed"] is True
    assert result["sizeBytes"] > 0


def test_verify_fails_when_a_file_was_edited(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    (model_directory("test-model", tmp_path) / "model.bin").write_bytes(b"tampered")

    result = manager.verify("test-model")

    assert result["verified"] is False
    assert "해시 불일치" in result["message"]


def test_verify_fails_when_a_file_is_missing(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    (model_directory("test-model", tmp_path) / "config.json").unlink()

    result = manager.verify("test-model")
    assert result["verified"] is False
    assert "파일 없음" in result["message"]


def test_verify_needs_no_network(tmp_path: Path) -> None:
    class ExplodingSession(FakeSession):
        def get(self, *_args: Any, **_kwargs: Any):  # noqa: ANN201
            raise AssertionError("verification must never touch the network")

    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    offline = ModelManager(tmp_path, session_factory=lambda: ExplodingSession(FILES))
    assert offline.verify("test-model")["verified"] is True


def test_verify_of_an_absent_model(tmp_path: Path) -> None:
    result = _manager(tmp_path, FakeSession(FILES)).verify("never-installed")

    assert result["installed"] is False
    assert result["verified"] is False
    assert result["message"]


def test_verify_of_a_hand_copied_model_without_a_manifest(tmp_path: Path) -> None:
    directory = model_directory("manual", tmp_path)
    directory.mkdir(parents=True)
    (directory / "model.bin").write_bytes(b"weights")

    result = _manager(tmp_path, FakeSession(FILES)).verify("manual")

    assert result["installed"] is True
    assert result["verified"] is False
    assert "검증 정보" in result["message"]


# ---------------------------------------------------------------------------
# list / delete
# ---------------------------------------------------------------------------


def test_list_models_reports_installed_state(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    entries = manager.list_models()
    entry = next(e for e in entries if e["modelId"] == "test-model")

    assert entry["installed"] is True
    assert entry["verified"] is True
    assert entry["sizeBytes"] == sum(len(b) for b in FILES.values())


def test_list_models_flags_an_interrupted_download(tmp_path: Path) -> None:
    directory = model_directory("half-done", tmp_path)
    directory.mkdir(parents=True)
    (directory / "model.bin.part").write_bytes(b"x" * 100)

    entry = next(e for e in _manager(tmp_path, FakeSession(FILES)).list_models() if e["modelId"] == "half-done")

    assert entry["installed"] is False
    assert "이어서" in entry["message"]


def test_list_models_on_an_empty_directory(tmp_path: Path) -> None:
    assert _manager(tmp_path, FakeSession(FILES)).list_models() == []


def test_delete_removes_the_directory(tmp_path: Path) -> None:
    manager = _manager(tmp_path, FakeSession(FILES))
    manager.download(model_id="test-model", repository_id="acme/test", files=list(FILES))

    result = manager.delete("test-model")

    assert result["installed"] is False
    assert not model_directory("test-model", tmp_path).exists()


def test_deleting_an_absent_model_is_not_an_error(tmp_path: Path) -> None:
    result = _manager(tmp_path, FakeSession(FILES)).delete("never-installed")
    assert result["installed"] is False
    assert "이미 삭제" in result["message"]


# ---------------------------------------------------------------------------
# manifest helpers
# ---------------------------------------------------------------------------


def test_manifest_write_is_atomic(tmp_path: Path) -> None:
    from ksubmaker_worker.model_manager import write_manifest

    write_manifest(tmp_path, {"modelId": "x", "complete": True})

    assert (tmp_path / MANIFEST_NAME).is_file()
    assert not list(tmp_path.glob("*.tmp"))


def test_a_corrupt_manifest_reads_as_empty(tmp_path: Path) -> None:
    (tmp_path / MANIFEST_NAME).write_text("{not json", encoding="utf-8")
    assert read_manifest(tmp_path) == {}


def test_sha256_of_a_file(tmp_path: Path) -> None:
    path = tmp_path / "blob"
    path.write_bytes(b"hello world")
    assert sha256_file(path) == hashlib.sha256(b"hello world").hexdigest()


def test_hashing_stops_on_cancellation(tmp_path: Path) -> None:
    path = tmp_path / "blob"
    path.write_bytes(b"x" * (1024 * 1024))

    token = CancellationToken("t")
    token.cancel()

    with pytest.raises(errors.CancelledError):
        sha256_file(path, token)

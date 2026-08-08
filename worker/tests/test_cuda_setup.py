"""CUDA DLL-directory registration and the error classification that depends on it.

The Windows branch is exercised on Linux by injecting ``is_windows=True``, a fake
``site-packages/nvidia/*/bin`` tree on a tmp_path, and a fake ``add_dll_directory`` — everything the
real code touches is a parameter for exactly this reason. What cannot be covered here is whether
``os.add_dll_directory`` makes the Windows loader actually find the DLL; only a real GPU machine
answers that.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker import cuda_setup


@pytest.fixture(autouse=True)
def _clear_cache() -> Any:
    """The module caches its report per process; every test starts from nothing."""
    cuda_setup.reset()
    yield
    cuda_setup.reset()


def _fake_site_packages(root: Path, *, cublas: bool = True, cudnn: bool = True) -> Path:
    """Build the layout pip produces for nvidia-cublas-cu12 / nvidia-cudnn-cu12."""
    site_packages = root / "Lib" / "site-packages"

    if cublas:
        cublas_bin = site_packages / "nvidia" / "cublas" / "bin"
        cublas_bin.mkdir(parents=True)
        (cublas_bin / cuda_setup.CUBLAS_DLL).write_bytes(b"MZ fake")
        (cublas_bin / cuda_setup.CUBLASLT_DLL).write_bytes(b"MZ fake")

    if cudnn:
        cudnn_bin = site_packages / "nvidia" / "cudnn" / "bin"
        cudnn_bin.mkdir(parents=True)
        (cudnn_bin / cuda_setup.CUDNN_DLL).write_bytes(b"MZ fake")

    return site_packages


# ---------------------------------------------------------------------------
# discovery
# ---------------------------------------------------------------------------


def test_every_nvidia_component_bin_directory_is_discovered(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path)

    found = cuda_setup.discover_dll_directories(
        site_packages=[str(site_packages)], interpreter_dir=str(tmp_path)
    )

    assert str((site_packages / "nvidia" / "cublas" / "bin").resolve()) in found
    assert str((site_packages / "nvidia" / "cudnn" / "bin").resolve()) in found


def test_the_interpreter_bin_and_library_bin_are_also_considered(tmp_path: Path) -> None:
    # The manual escape hatch from TROUBLESHOOTING.md: DLLs copied next to python.exe.
    (tmp_path / "bin").mkdir()
    (tmp_path / "Library" / "bin").mkdir(parents=True)

    found = cuda_setup.discover_dll_directories(site_packages=[], interpreter_dir=str(tmp_path))

    assert str((tmp_path / "bin").resolve()) in found
    assert str((tmp_path / "Library" / "bin").resolve()) in found


def test_nonexistent_directories_are_not_offered(tmp_path: Path) -> None:
    found = cuda_setup.discover_dll_directories(
        site_packages=[str(tmp_path / "nope")], interpreter_dir=str(tmp_path / "gone")
    )

    assert found == []


def test_the_same_directory_reached_twice_is_registered_once(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path, cudnn=False)

    found = cuda_setup.discover_dll_directories(
        # sysconfig purelib and site.getsitepackages() routinely return the same path.
        site_packages=[str(site_packages), str(site_packages)],
        interpreter_dir=str(tmp_path),
    )

    assert len(found) == len(set(found)) == 1


# ---------------------------------------------------------------------------
# registration
# ---------------------------------------------------------------------------


def test_registration_adds_every_directory_and_reports_the_dlls(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path)
    added: list[str] = []

    report = cuda_setup.register_cuda_dll_directories(
        site_packages=[str(site_packages)],
        interpreter_dir=str(tmp_path),
        add_dll_directory=added.append,
        is_windows=True,
    )

    assert report.windows is True
    assert len(added) == 2
    assert added == report.added
    assert set(report.found) == {
        cuda_setup.CUBLAS_DLL,
        cuda_setup.CUBLASLT_DLL,
        cuda_setup.CUDNN_DLL,
    }
    assert report.missing == []
    assert report.complete is True


def test_a_missing_cudnn_wheel_is_reported_not_hidden(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path, cudnn=False)

    report = cuda_setup.register_cuda_dll_directories(
        site_packages=[str(site_packages)],
        interpreter_dir=str(tmp_path),
        add_dll_directory=lambda _path: None,
        is_windows=True,
    )

    assert report.missing == [cuda_setup.CUDNN_DLL]
    assert report.complete is False
    assert cuda_setup.CUDNN_DLL in report.summary()
    assert "MISSING" in report.summary()


def test_a_directory_that_cannot_be_registered_does_not_stop_the_others(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path)
    accepted: list[str] = []

    def flaky(path: str) -> None:
        if "cublas" in path:
            raise OSError("access denied")
        accepted.append(path)

    report = cuda_setup.register_cuda_dll_directories(
        site_packages=[str(site_packages)],
        interpreter_dir=str(tmp_path),
        add_dll_directory=flaky,
        is_windows=True,
    )

    assert len(accepted) == 1
    assert report.errors and "access denied" in report.errors[0]
    # The DLL is still *found* — only the registration failed, and that distinction is what the
    # log has to show for anyone diagnosing this.
    assert cuda_setup.CUBLAS_DLL in report.found


def test_registration_is_a_clean_no_op_off_windows(tmp_path: Path) -> None:
    def explode(_path: str) -> None:  # pragma: no cover - must never be called
        raise AssertionError("add_dll_directory must not be called on a non-Windows host")

    report = cuda_setup.register_cuda_dll_directories(
        site_packages=[str(_fake_site_packages(tmp_path))],
        interpreter_dir=str(tmp_path),
        add_dll_directory=explode,
        is_windows=False,
    )

    assert report.windows is False
    assert report.added == []
    assert report.missing == []
    assert report.complete is True
    assert "not Windows" in report.summary()


def test_the_real_platform_default_never_raises_on_this_host() -> None:
    """Whatever this machine is, the start-up call must return a report instead of throwing."""
    report = cuda_setup.register_cuda_dll_directories()

    assert isinstance(report, cuda_setup.CudaSetupReport)
    assert report.windows is sys.platform.startswith("win")
    assert isinstance(report.to_dict()["searched"], list)


def test_ensure_registered_caches_the_report(tmp_path: Path) -> None:
    calls: list[str] = []

    first = cuda_setup.ensure_registered(
        site_packages=[str(_fake_site_packages(tmp_path))],
        interpreter_dir=str(tmp_path),
        add_dll_directory=calls.append,
        load_library=lambda _path: object(),
        is_windows=True,
    )
    second = cuda_setup.ensure_registered()

    assert first is second is cuda_setup.last_report()
    assert len(calls) == 2, "the second call must not re-register anything"


# ---------------------------------------------------------------------------
# preload — the step that actually makes CTranslate2 resolve cuBLAS
# ---------------------------------------------------------------------------


def test_ensure_registered_loads_the_dlls_and_not_only_the_directories(tmp_path: Path) -> None:
    """The defect this guards, measured on a real GPU machine on 2026-08-07.

    With the directories registered but nothing loaded, CTranslate2 still died at translate_batch
    with "Library cublas64_12.dll is not found or cannot be loaded". Adding a load of the same
    files — by absolute path, into the process — is what made it work. The GPU path had been
    working only because the hardware detector happened to load them first via
    probe_support_libraries, which nothing documented and which sits behind an `if device_detected`.
    """
    site_packages = _fake_site_packages(tmp_path)
    loaded: list[str] = []

    report = cuda_setup.ensure_registered(
        site_packages=[str(site_packages)],
        interpreter_dir=str(tmp_path),
        add_dll_directory=lambda _path: None,
        load_library=lambda path: loaded.append(path) or object(),
        is_windows=True,
    )

    assert report.loaded == [
        cuda_setup.CUBLASLT_DLL,
        cuda_setup.CUBLAS_DLL,
        cuda_setup.CUDNN_DLL,
    ], "cuBLASLt must load before the cuBLAS that depends on it"

    # Absolute paths, not bare names: that is what takes the loader's search order out of play.
    assert all(Path(path).is_absolute() for path in loaded)
    assert all(Path(path).is_file() for path in loaded)


def test_a_dll_that_cannot_be_loaded_is_recorded_and_the_others_still_load(tmp_path: Path) -> None:
    site_packages = _fake_site_packages(tmp_path)

    def flaky(path: str) -> Any:
        if cuda_setup.CUDNN_DLL in path:
            raise OSError("[WinError 126] 지정된 모듈을 찾을 수 없습니다")
        return object()

    report = cuda_setup.ensure_registered(
        site_packages=[str(site_packages)],
        interpreter_dir=str(tmp_path),
        add_dll_directory=lambda _path: None,
        load_library=flaky,
        is_windows=True,
    )

    assert cuda_setup.CUDNN_DLL not in report.loaded
    assert cuda_setup.CUBLAS_DLL in report.loaded
    assert any(cuda_setup.CUDNN_DLL in error for error in report.errors)


def test_a_missing_dll_is_skipped_without_an_error(tmp_path: Path) -> None:
    # A CPU-only machine legitimately has no cuDNN wheel. It is already reported through `missing`;
    # a second complaint from the preload would be noise, and the worker must still start.
    report = cuda_setup.ensure_registered(
        site_packages=[str(_fake_site_packages(tmp_path, cudnn=False))],
        interpreter_dir=str(tmp_path),
        add_dll_directory=lambda _path: None,
        load_library=lambda _path: object(),
        is_windows=True,
    )

    assert report.missing == [cuda_setup.CUDNN_DLL]
    assert report.loaded == [cuda_setup.CUBLASLT_DLL, cuda_setup.CUBLAS_DLL]
    assert report.errors == []


def test_preload_is_a_clean_no_op_off_windows(tmp_path: Path) -> None:
    def explode(_path: str) -> Any:  # pragma: no cover - must never be called
        raise AssertionError("no DLL may be loaded on a non-Windows host")

    report = cuda_setup.ensure_registered(
        site_packages=[str(_fake_site_packages(tmp_path))],
        interpreter_dir=str(tmp_path),
        add_dll_directory=lambda _path: None,
        load_library=explode,
        is_windows=False,
    )

    assert report.loaded == []


# ---------------------------------------------------------------------------
# runtime probe
# ---------------------------------------------------------------------------


def test_the_probe_loads_every_required_library() -> None:
    loaded: list[str] = []

    ok, missing, errors = cuda_setup.probe_support_libraries(
        loader=loaded.append, is_windows=True
    )

    assert ok is True
    assert missing == []
    assert errors == []
    assert loaded == list(cuda_setup.REQUIRED_DLLS)


def test_the_probe_reports_exactly_which_library_failed() -> None:
    def loader(name: str) -> None:
        if name == cuda_setup.CUBLAS_DLL:
            raise OSError("[WinError 126] 지정된 모듈을 찾을 수 없습니다")

    ok, missing, errors = cuda_setup.probe_support_libraries(loader=loader, is_windows=True)

    assert ok is False
    assert missing == [cuda_setup.CUBLAS_DLL]
    assert errors and cuda_setup.CUBLAS_DLL in errors[0]


def test_the_probe_is_a_no_op_off_windows() -> None:
    def explode(_name: str) -> None:  # pragma: no cover - must never be called
        raise AssertionError("the probe is Windows-only")

    assert cuda_setup.probe_support_libraries(loader=explode, is_windows=False) == (True, [], [])


# ---------------------------------------------------------------------------
# error classification
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("message", "expected"),
    [
        # The exact string from the user's log.
        ("Library cublas64_12.dll is not found or cannot be loaded", "cublas64_12.dll"),
        ("Library cudnn64_9.dll is not found or cannot be loaded", "cudnn64_9.dll"),
        ("library CUDNN_OPS64_9.DLL is not found or cannot be loaded", "CUDNN_OPS64_9.DLL"),
        # A plain Windows loader message that names the file.
        ("DLL load failed while importing _ext: cublas64_12.dll", "cublas64_12.dll"),
        ("libcudnn.so.9: cannot open shared object file", "libcudnn.so.9"),
    ],
)
def test_a_missing_support_library_is_recognised(message: str, expected: str) -> None:
    assert cuda_setup.missing_cuda_library(RuntimeError(message)) == expected
    assert cuda_setup.is_cuda_library_missing(message) is True


@pytest.mark.parametrize(
    "message",
    [
        # An OOM message that mentions cuBLAS. Misclassifying this would replace a recoverable
        # error (retry smaller) with a fatal one and lose the whole downgrade ladder.
        "CUBLAS_STATUS_ALLOC_FAILED: cublas allocation failed",
        "CUDA failed with error out of memory",
        "Model whisper-small is not found",
        "Unable to open file 'model.bin'",
        "",
    ],
)
def test_unrelated_failures_are_not_misread_as_a_missing_library(message: str) -> None:
    assert cuda_setup.missing_cuda_library(RuntimeError(message)) is None
    assert cuda_setup.is_cuda_library_missing(RuntimeError(message)) is False


def test_none_is_not_a_missing_library() -> None:
    assert cuda_setup.missing_cuda_library(None) is None
    assert cuda_setup.is_cuda_library_missing(None) is False


def test_the_remedy_message_is_korean_and_names_the_library() -> None:
    message = cuda_setup.remedy_message("cublas64_12.dll")

    assert "cublas64_12.dll" in message
    assert "build-worker.ps1" in message
    assert any("가" <= ch <= "힣" for ch in message)

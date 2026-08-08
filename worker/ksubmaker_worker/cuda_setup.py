"""Windows CUDA support-library discovery, run before anything imports CTranslate2.

Why this module exists
----------------------
``ctranslate2 >= 4.5`` links against **cuBLAS (CUDA 12)** and **cuDNN 9**. Neither ships inside the
``ctranslate2`` wheel, and neither comes with the NVIDIA *display driver* — they are CUDA *toolkit*
libraries. ``scripts/build-worker.ps1`` therefore installs the ``nvidia-cublas-cu12`` and
``nvidia-cudnn-cu12`` wheels into the embedded runtime, which drops the DLLs at::

    <site-packages>/nvidia/cublas/bin/cublas64_12.dll
    <site-packages>/nvidia/cudnn/bin/cudnn64_9.dll

Installing them is **not enough**. Since CPython 3.8 the interpreter calls
``SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)`` on Windows, which takes ``PATH`` out
of the DLL search order altogether; ``site-packages`` is not an application directory either. The
only supported way back in is :func:`os.add_dll_directory`, which appends to the *user directory*
list that the default search order does consult. That has to happen **before the first**
``import ctranslate2``, because the dependency DLLs are resolved while the extension module is
being loaded, and a failed resolution is not retried.

**Registering the directory is still not enough.** With only ``os.add_dll_directory`` in place,
CTranslate2 fails at ``translate_batch`` with ``Library cublas64_12.dll is not found or cannot be
loaded`` — the file is present, the directory was registered successfully, and the load still does
not resolve. So :func:`ensure_registered` also **loads the DLLs by absolute path**, which puts them
in the process's module list; CTranslate2's later resolution by name then finds them already there.
Measured on 2026-08-07: the identical script fails or succeeds on that one step.

Until this existed the GPU path worked only by accident — the hardware detector calls
:func:`probe_support_libraries` at start-up, and *that* load was what CTranslate2 was relying on.
Nothing said so, and the probe sits behind an ``if device_detected:``.

The failure this prevents, from a real user log (RTX 3080 Ti, driver CUDA 13.1, Windows)::

    RuntimeError('Library cublas64_12.dll is not found or cannot be loaded')

Everything here is a clean no-op on non-Windows: the Linux wheels carry an ``RPATH`` and the dynamic
loader finds their dependencies without help, so there is nothing to register and nothing to check.
"""

from __future__ import annotations

import os
import re
import sys
import sysconfig
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Sequence

from .logging_setup import get_logger

_log = get_logger("cuda")

#: cuBLAS entry DLL for CUDA **12**. The major version is part of the file name, which is exactly
#: why a CUDA 13 driver does not satisfy a CTranslate2 build linked against CUDA 12.
CUBLAS_DLL = "cublas64_12.dll"

#: cuBLASLt is a second DLL in the same wheel; cuBLAS itself depends on it.
CUBLASLT_DLL = "cublasLt64_12.dll"

#: cuDNN **9** entry DLL. cuDNN 9 split itself into sub-libraries (``cudnn_graph64_9.dll`` and
#: friends) that this one loads on demand, so probing the entry point is the meaningful check.
CUDNN_DLL = "cudnn64_9.dll"

#: The DLLs whose absence produces the RuntimeError above. Order is the order they are reported in.
REQUIRED_DLLS: tuple[str, ...] = (CUBLAS_DLL, CUDNN_DLL)

#: Also located and reported when present, but their absence alone is not treated as fatal:
#: cuBLASLt lives next to cuBLAS and is pulled in by it.
OPTIONAL_DLLS: tuple[str, ...] = (CUBLASLT_DLL,)

#: CTranslate2's own wording when a support library cannot be resolved. Matching the *shape* of the
#: message is the only option: it raises a plain ``RuntimeError`` with no error code attached.
_MISSING_LIBRARY_PATTERN = re.compile(
    r"library\s+(?P<name>[\w.+-]+)\s+is\s+not\s+found\s+or\s+cannot\s+be\s+loaded",
    re.IGNORECASE,
)

#: Substrings that identify a CUDA *support* library (as opposed to the driver or the model file).
_SUPPORT_LIBRARY_MARKERS: tuple[str, ...] = ("cublas", "cudnn", "cudart", "cufft")

#: Generic "the loader could not resolve this" phrasings, used when the message names a library but
#: does not use CTranslate2's exact sentence.
_LOAD_FAILURE_MARKERS: tuple[str, ...] = (
    "is not found or cannot be loaded",
    "cannot be loaded",
    "could not be loaded",
    "dll load failed",
    "cannot open shared object file",
)


@dataclass
class CudaSetupReport:
    """What :func:`register_cuda_dll_directories` did, in a shape that can be logged or emitted.

    Deliberately a value object with no behaviour beyond formatting: it is produced once at
    start-up, logged to stderr, and then read again by the hardware detector and by diagnostics.
    """

    #: False on Linux/macOS, where the whole mechanism is a no-op.
    windows: bool = False

    #: Directories that were considered (existing ones only).
    searched: list[str] = field(default_factory=list)

    #: Directories actually handed to :func:`os.add_dll_directory`.
    added: list[str] = field(default_factory=list)

    #: DLL name -> absolute path, for every known DLL found while scanning ``searched``.
    found: dict[str, str] = field(default_factory=dict)

    #: Entries of :data:`REQUIRED_DLLS` that were not found in any searched directory.
    missing: list[str] = field(default_factory=list)

    #: DLLs actually loaded into the process by :func:`ensure_registered`. This — not
    #: :attr:`added` — is what makes CTranslate2's own load succeed; see the module docstring.
    loaded: list[str] = field(default_factory=list)

    #: Non-fatal problems (a directory that could not be registered, a permission error).
    errors: list[str] = field(default_factory=list)

    @property
    def complete(self) -> bool:
        """True when every required DLL was located (or when the platform does not need them)."""
        return not self.windows or not self.missing

    def to_dict(self) -> dict[str, Any]:
        """JSON-friendly projection, used by the diagnostics path."""
        return {
            "windows": self.windows,
            "searched": list(self.searched),
            "added": list(self.added),
            "found": dict(self.found),
            "missing": list(self.missing),
            "loaded": list(self.loaded),
            "errors": list(self.errors),
            "complete": self.complete,
        }

    def summary(self) -> str:
        """One line for the stderr log. English: this is a diagnostic, not a user-facing string."""
        if not self.windows:
            return "cuda_setup: not Windows; nothing to register"

        parts = [
            f"cuda_setup: registered {len(self.added)}/{len(self.searched)} DLL directories",
            f"found={sorted(self.found)}",
            f"loaded={self.loaded}",
        ]
        if self.missing:
            parts.append(f"MISSING={self.missing}")
        if self.errors:
            parts.append(f"errors={self.errors}")
        return "; ".join(parts)


# ---------------------------------------------------------------------------
# directory discovery
# ---------------------------------------------------------------------------


def _default_site_packages() -> list[str]:
    """Every plausible ``site-packages`` for this interpreter, most specific first.

    ``sysconfig`` answers for the embedded python-build-standalone runtime; ``site`` answers for a
    normal install or a venv. Both are consulted because the worker runs under both.
    """
    candidates: list[str] = []

    paths = sysconfig.get_paths()
    for key in ("purelib", "platlib"):
        value = paths.get(key)
        if value:
            candidates.append(value)

    # `site` is missing getsitepackages() in some embedded configurations, hence getattr.
    site_module = sys.modules.get("site")
    if site_module is None:
        try:
            import site as site_module  # noqa: PLC0415 - only needed here
        except ImportError:  # pragma: no cover - site is always importable in practice
            site_module = None

    if site_module is not None:
        getter = getattr(site_module, "getsitepackages", None)
        if callable(getter):
            try:
                candidates.extend(getter())
            except (AttributeError, OSError, TypeError) as exc:  # pragma: no cover - defensive
                _log.debug("site.getsitepackages() failed: %r", exc)

        if getattr(site_module, "ENABLE_USER_SITE", False):
            user_getter = getattr(site_module, "getusersitepackages", None)
            if callable(user_getter):
                try:
                    candidates.append(user_getter())
                except (AttributeError, OSError, TypeError) as exc:  # pragma: no cover
                    _log.debug("site.getusersitepackages() failed: %r", exc)

    for entry in sys.path:
        if entry and entry.rstrip("\\/").endswith("site-packages"):
            candidates.append(entry)

    return candidates


def discover_dll_directories(
    *,
    site_packages: Sequence[str] | None = None,
    interpreter_dir: str | os.PathLike[str] | None = None,
) -> list[str]:
    """Directories that may hold the CUDA support DLLs, de-duplicated, existing ones only.

    Three sources, in the order they are searched:

    1. ``<site-packages>/nvidia/<component>/bin`` — where pip puts the ``nvidia-*-cu12`` wheels.
    2. ``<interpreter>/bin`` — the manual escape hatch documented in TROUBLESHOOTING.md, for a user
       who copied the DLLs next to ``python.exe`` rather than re-running the build script.
    3. ``<interpreter>/Library/bin`` — the conda-style layout, which some Windows runtimes use.
    """
    roots = list(site_packages) if site_packages is not None else _default_site_packages()
    base = Path(interpreter_dir) if interpreter_dir is not None else Path(sys.executable).parent

    ordered: list[str] = []
    seen: set[str] = set()

    def offer(path: Path) -> None:
        try:
            if not path.is_dir():
                return
            resolved = str(path.resolve())
        except OSError as exc:  # pragma: no cover - unreadable mount points
            _log.debug("cannot stat candidate DLL directory %s: %r", path, exc)
            return

        key = resolved.casefold()
        if key not in seen:
            seen.add(key)
            ordered.append(resolved)

    for root in roots:
        if not root:
            continue
        nvidia = Path(root) / "nvidia"
        try:
            components = sorted(nvidia.iterdir()) if nvidia.is_dir() else []
        except OSError as exc:  # pragma: no cover - defensive
            _log.debug("cannot list %s: %r", nvidia, exc)
            continue

        for component in components:
            offer(component / "bin")

    offer(base / "bin")
    offer(base / "Library" / "bin")

    return ordered


def _scan_for_dlls(directories: Iterable[str]) -> dict[str, str]:
    """Map known DLL name -> absolute path, first directory wins."""
    found: dict[str, str] = {}

    for directory in directories:
        for name in REQUIRED_DLLS + OPTIONAL_DLLS:
            if name in found:
                continue
            candidate = Path(directory) / name
            try:
                if candidate.is_file():
                    found[name] = str(candidate)
            except OSError as exc:  # pragma: no cover - defensive
                _log.debug("cannot stat %s: %r", candidate, exc)

    return found


# ---------------------------------------------------------------------------
# registration
# ---------------------------------------------------------------------------

_report: CudaSetupReport | None = None


def register_cuda_dll_directories(
    *,
    site_packages: Sequence[str] | None = None,
    interpreter_dir: str | os.PathLike[str] | None = None,
    add_dll_directory: Callable[[str], Any] | None = None,
    is_windows: bool | None = None,
) -> CudaSetupReport:
    """Register every candidate directory with ``os.add_dll_directory`` and report what happened.

    Never raises: a worker that cannot register a directory must still start and fail later with a
    specific error, not refuse to boot. The keyword arguments exist so the Linux test suite can
    exercise the Windows branch against a fake ``site-packages/nvidia/*/bin`` tree.
    """
    windows = sys.platform.startswith("win") if is_windows is None else is_windows

    if not windows:
        # Linux/macOS wheels carry an RPATH; there is no user-directory list to extend, and
        # os.add_dll_directory does not even exist. Returning an empty report keeps the caller and
        # the tests on one code path.
        return CudaSetupReport(windows=False)

    report = CudaSetupReport(windows=True)
    report.searched = discover_dll_directories(
        site_packages=site_packages, interpreter_dir=interpreter_dir
    )

    adder = add_dll_directory if add_dll_directory is not None else getattr(os, "add_dll_directory", None)

    if adder is None:
        # Only reachable on a Python built without the Windows DLL-directory API.
        report.errors.append("os.add_dll_directory is unavailable on this interpreter")
    else:
        for directory in report.searched:
            try:
                adder(directory)
            except OSError as exc:
                # A directory that vanished between the scan and the call, or one the process may
                # not read. Record it and keep going: the others may still be enough.
                report.errors.append(f"{directory}: {exc!r}")
                continue
            report.added.append(directory)

    report.found = _scan_for_dlls(report.searched)
    report.missing = [name for name in REQUIRED_DLLS if name not in report.found]

    return report


#: Handles for the preloaded support DLLs, held for the process lifetime. ctypes does not unload a
#: library when its wrapper is collected, but keeping the references makes that explicit rather
#: than incidental.
_preloaded: list[Any] = []


def preload_support_libraries(
    report: CudaSetupReport,
    load_library: Callable[[str], Any] | None = None,
) -> None:
    """Load the located DLLs into the process, by **absolute path**, recording the outcome.

    This is the step that actually makes CTranslate2 work; see the module docstring for why
    registering the directory is not sufficient. Loading by absolute path rather than by name takes
    the search order out of the question entirely.

    Never raises. A machine with no GPU legitimately has none of these files, and the worker must
    still start and run on the CPU.
    """
    if not report.windows:
        return

    loader = load_library
    if loader is None:
        import ctypes  # noqa: PLC0415 - Windows branch only

        loader = getattr(ctypes, "WinDLL", None)
        if loader is None:  # pragma: no cover - only on a non-Windows interpreter
            return

    # cuBLASLt before cuBLAS: cuBLAS depends on it, and loading the dependency by absolute path
    # first removes any question of how the loader would otherwise have found it.
    for name in (*OPTIONAL_DLLS, *REQUIRED_DLLS):
        path = report.found.get(name)
        if path is None:
            # Already recorded in report.missing when it is a required one.
            continue

        try:
            _preloaded.append(loader(path))
        except Exception as exc:  # noqa: BLE001 - the Windows loader surfaces more than OSError
            report.errors.append(f"preload {name}: {exc!r}")
            continue

        report.loaded.append(name)


def ensure_registered(**kwargs: Any) -> CudaSetupReport:
    """Register the directories, load the DLLs, cache the report. Once per process.

    Called from the worker's entry point *before* the command loop starts, and again (harmlessly)
    from the hardware detector, which may be reached first in a test or an embedded use.

    <b>Anything that constructs a translator or transcriber outside the worker must call this</b>,
    not just :func:`register_cuda_dll_directories`: registration alone leaves CTranslate2 unable to
    resolve cuBLAS.
    """
    global _report

    if _report is None:
        load_library = kwargs.pop("load_library", None)
        report = register_cuda_dll_directories(**kwargs)
        preload_support_libraries(report, load_library)
        _report = report

    return _report


def last_report() -> CudaSetupReport | None:
    """The cached report, or None when :func:`ensure_registered` has not run yet."""
    return _report


def reset() -> None:
    """Drop the cached report. Tests use this; production code calls it never."""
    global _report
    _report = None
    _preloaded.clear()


# ---------------------------------------------------------------------------
# runtime probe
# ---------------------------------------------------------------------------


def probe_support_libraries(
    *,
    loader: Callable[[str], Any] | None = None,
    is_windows: bool | None = None,
) -> tuple[bool, list[str], list[str]]:
    """Try to actually load the CUDA support DLLs. Returns ``(ok, missing, errors)``.

    This is the difference between "a driver is installed" and "the next model load will work".
    ``ctranslate2.get_cuda_device_count()`` only needs the driver; ``cublas64_12.dll`` is what the
    first model load needs, and until it has been loaded once nothing has proved it exists.

    The probe is Windows-only **on purpose**. The packaging hole it guards is a Windows one — the
    pip wheels put their DLLs somewhere the Windows loader will not look. On Linux the same wheels
    are found through the ELF ``RPATH``, so a probe there would only be able to produce false
    negatives on a machine where CUDA is fine.
    """
    windows = sys.platform.startswith("win") if is_windows is None else is_windows

    if not windows:
        return True, [], []

    if loader is None:
        import ctypes  # noqa: PLC0415 - only needed on the Windows branch

        windll = getattr(ctypes, "WinDLL", None)
        if windll is None:  # pragma: no cover - only on a non-Windows interpreter
            return True, [], []
        loader = windll

    missing: list[str] = []
    errors: list[str] = []

    for name in REQUIRED_DLLS:
        try:
            loader(name)
        except OSError as exc:
            missing.append(name)
            errors.append(f"{name}: {exc!r}")
        except Exception as exc:  # noqa: BLE001 - ctypes can surface anything from the loader
            missing.append(name)
            errors.append(f"{name}: {exc!r}")

    return not missing, missing, errors


# ---------------------------------------------------------------------------
# error classification
# ---------------------------------------------------------------------------


def missing_cuda_library(exc: BaseException | str | None) -> str | None:
    """Name of the CUDA support library CTranslate2 could not load, or None.

    Textual matching is the only option: CTranslate2 raises a bare ``RuntimeError`` whose message is
    the sole distinguishing feature. Two shapes are recognised —

    * ``Library cublas64_12.dll is not found or cannot be loaded`` (CTranslate2's own wording), and
    * any other "could not load" phrasing that names a file containing ``cublas`` / ``cudnn`` /
      ``cudart`` (the Windows loader's own ``DLL load failed`` messages).

    A CUDA **out of memory** error can also mention cuBLAS (``CUBLAS_STATUS_ALLOC_FAILED``), which
    is why a load-failure phrase is required and not just the library name.
    """
    if exc is None:
        return None

    text = exc if isinstance(exc, str) else str(exc)
    if not text:
        return None

    match = _MISSING_LIBRARY_PATTERN.search(text)
    if match is not None:
        return match.group("name")

    lowered = text.lower()
    if not any(marker in lowered for marker in _LOAD_FAILURE_MARKERS):
        return None

    for token in re.findall(r"[\w.+-]+", text):
        if any(marker in token.lower() for marker in _SUPPORT_LIBRARY_MARKERS):
            return token

    return None


def is_cuda_library_missing(exc: BaseException | str | None) -> bool:
    """True when ``exc`` is "the CUDA support libraries are not installed", not any other failure."""
    return missing_cuda_library(exc) is not None


def remedy_message(library: str | None = None) -> str:
    """Korean, user-facing: what is missing and exactly what to do about it."""
    name = library or "cuBLAS / cuDNN"
    return (
        f"CUDA 지원 라이브러리({name})를 불러오지 못했습니다. "
        "NVIDIA 드라이버만으로는 부족하며 CUDA 12용 cuBLAS와 cuDNN 9가 함께 설치되어 있어야 합니다. "
        "scripts\\build-worker.ps1을 다시 실행해 워커를 설치하거나, 설정에서 CPU 모드로 전환하세요."
    )

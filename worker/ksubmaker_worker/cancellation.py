"""Cooperative cancellation plus a registry of child processes.

Cancelling a job means two things at once: the Python loops must stop (the token), and any
ffmpeg / llama-server child must die (the registry). Doing only the first leaves a 4 GB
llama-server resident; doing only the second leaves the worker spinning on a dead pipe.
"""

from __future__ import annotations

import subprocess
import threading
from types import TracebackType
from typing import Callable

from .errors import CancelledError
from .logging_setup import get_logger

_log = get_logger("cancellation")

#: How long a child gets to exit after SIGTERM/terminate() before it is killed outright.
TERMINATE_GRACE_SECONDS = 3.0


class CancellationToken:
    """An ``threading.Event`` with a nicer surface and a child-process registry.

    The token is created by the command loop on the main thread and observed by the job thread,
    so every mutation is guarded: ``cancel()`` may be called while the job is mid-loop.
    """

    def __init__(self, name: str = "job") -> None:
        self._event = threading.Event()
        self._lock = threading.RLock()
        self._children: list[subprocess.Popen[bytes] | subprocess.Popen[str]] = []
        self._callbacks: list[Callable[[], None]] = []
        self.name = name

    # -- state ----------------------------------------------------------------

    @property
    def cancelled(self) -> bool:
        return self._event.is_set()

    def wait(self, timeout: float | None = None) -> bool:
        """Block until cancelled or ``timeout`` elapses. Returns True when cancelled."""
        return self._event.wait(timeout)

    def raise_if_cancelled(self) -> None:
        if self._event.is_set():
            raise CancelledError()

    # -- cancellation ---------------------------------------------------------

    def cancel(self) -> None:
        """Signal cancellation and kill every registered child. Safe to call repeatedly."""
        already = self._event.is_set()
        self._event.set()

        if already:
            return

        _log.info("cancellation requested for %s", self.name)

        with self._lock:
            children = list(self._children)
            callbacks = list(self._callbacks)

        for child in children:
            kill_process(child)

        for callback in callbacks:
            try:
                callback()
            except Exception as exc:  # noqa: BLE001 - a bad callback must not block cancellation
                _log.warning("cancellation callback failed: %r", exc)

    def reset(self) -> None:
        """Return the token to the un-cancelled state and forget its children."""
        with self._lock:
            self._children.clear()
            self._callbacks.clear()
        self._event.clear()

    # -- registries -----------------------------------------------------------

    def register_process(self, process: subprocess.Popen) -> None:
        """Track a child so ``cancel()`` can kill it.

        If the token is already cancelled the child is killed immediately: without this there is a
        race where a process spawned microseconds after ``cancel()`` would survive.
        """
        with self._lock:
            self._children.append(process)

        if self._event.is_set():
            kill_process(process)

    def unregister_process(self, process: subprocess.Popen) -> None:
        with self._lock:
            try:
                self._children.remove(process)
            except ValueError:
                # Already removed by another path; nothing to do.
                pass

    def register_callback(self, callback: Callable[[], None]) -> None:
        """Run ``callback`` when cancellation fires (or immediately if it already has)."""
        run_now = False
        with self._lock:
            if self._event.is_set():
                run_now = True
            else:
                self._callbacks.append(callback)

        if run_now:
            try:
                callback()
            except Exception as exc:  # noqa: BLE001
                _log.warning("cancellation callback failed: %r", exc)

    def child(self, process: subprocess.Popen) -> "_ProcessScope":
        """Context manager that registers a child for the duration of a ``with`` block."""
        return _ProcessScope(self, process)


class _ProcessScope:
    def __init__(self, token: CancellationToken, process: subprocess.Popen) -> None:
        self._token = token
        self._process = process

    def __enter__(self) -> subprocess.Popen:
        self._token.register_process(self._process)
        return self._process

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> bool:
        self._token.unregister_process(self._process)
        return False


def kill_process(process: subprocess.Popen, grace: float = TERMINATE_GRACE_SECONDS) -> None:
    """Terminate then kill a child, never raising.

    ffmpeg handles SIGTERM cleanly and flushes; llama-server sometimes does not, hence the
    escalation to ``kill()``.
    """
    if process.poll() is not None:
        return

    try:
        process.terminate()
    except (OSError, ValueError) as exc:
        _log.debug("terminate failed (pid %s): %r", getattr(process, "pid", "?"), exc)

    try:
        process.wait(timeout=grace)
        return
    except subprocess.TimeoutExpired:
        _log.warning("child pid %s ignored terminate; killing", getattr(process, "pid", "?"))
    except (OSError, ValueError) as exc:
        _log.debug("wait failed (pid %s): %r", getattr(process, "pid", "?"), exc)

    try:
        process.kill()
        process.wait(timeout=grace)
    except (OSError, ValueError, subprocess.TimeoutExpired) as exc:
        _log.warning("could not kill child pid %s: %r", getattr(process, "pid", "?"), exc)


class ProcessRegistry:
    """Process-wide registry of long-lived children (llama-server) for shutdown cleanup.

    Distinct from :class:`CancellationToken`: entries here outlive a single job, and are torn
    down when the worker exits or a signal arrives.
    """

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._processes: list[subprocess.Popen] = []

    def add(self, process: subprocess.Popen) -> None:
        with self._lock:
            self._processes.append(process)

    def remove(self, process: subprocess.Popen) -> None:
        with self._lock:
            try:
                self._processes.remove(process)
            except ValueError:
                pass

    def terminate_all(self) -> int:
        """Kill everything registered. Returns how many children were still alive."""
        with self._lock:
            processes = list(self._processes)
            self._processes.clear()

        killed = 0
        for process in processes:
            if process.poll() is None:
                killed += 1
            kill_process(process)

        if killed:
            _log.info("terminated %d child process(es) on shutdown", killed)

        return killed


#: The registry used by production code paths.
GLOBAL_PROCESSES = ProcessRegistry()

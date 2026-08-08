"""Cancellation tokens and the child-process registry."""

from __future__ import annotations

import subprocess
import sys
import threading
import time

import pytest

from ksubmaker_worker import errors
from ksubmaker_worker.cancellation import (
    CancellationToken,
    ProcessRegistry,
    kill_process,
)


def _sleeper(seconds: int = 60) -> subprocess.Popen:
    return subprocess.Popen(  # noqa: S603 - list argv, shell=False
        [sys.executable, "-c", f"import time; time.sleep({seconds})"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


# ---------------------------------------------------------------------------
# token
# ---------------------------------------------------------------------------


def test_a_fresh_token_is_not_cancelled() -> None:
    token = CancellationToken("t")
    assert token.cancelled is False
    token.raise_if_cancelled()


def test_cancel_sets_the_flag_and_raises() -> None:
    token = CancellationToken("t")
    token.cancel()

    assert token.cancelled is True
    with pytest.raises(errors.CancelledError) as excinfo:
        token.raise_if_cancelled()

    assert excinfo.value.code == errors.OPERATION_CANCELLED


def test_cancel_is_idempotent() -> None:
    token = CancellationToken("t")
    calls: list[int] = []
    token.register_callback(lambda: calls.append(1))

    token.cancel()
    token.cancel()
    token.cancel()

    assert calls == [1]


def test_reset_clears_the_state() -> None:
    token = CancellationToken("t")
    token.cancel()
    token.reset()

    assert token.cancelled is False
    token.raise_if_cancelled()


def test_wait_returns_when_cancelled_from_another_thread() -> None:
    token = CancellationToken("t")
    threading.Timer(0.05, token.cancel).start()

    assert token.wait(5.0) is True


def test_wait_times_out_when_nothing_happens() -> None:
    assert CancellationToken("t").wait(0.05) is False


def test_a_callback_registered_after_cancellation_fires_immediately() -> None:
    token = CancellationToken("t")
    token.cancel()

    calls: list[int] = []
    token.register_callback(lambda: calls.append(1))

    assert calls == [1]


def test_a_throwing_callback_does_not_block_the_others() -> None:
    token = CancellationToken("t")
    calls: list[str] = []

    def bad() -> None:
        raise RuntimeError("callback exploded")

    token.register_callback(bad)
    token.register_callback(lambda: calls.append("good"))
    token.cancel()

    assert calls == ["good"]


# ---------------------------------------------------------------------------
# child processes
# ---------------------------------------------------------------------------


def test_cancel_kills_a_registered_child() -> None:
    token = CancellationToken("t")
    child = _sleeper()

    try:
        token.register_process(child)
        assert child.poll() is None

        token.cancel()

        child.wait(10)
        assert child.poll() is not None
    finally:
        kill_process(child)


def test_a_child_registered_after_cancellation_is_killed_immediately() -> None:
    token = CancellationToken("t")
    token.cancel()

    child = _sleeper()
    try:
        token.register_process(child)
        child.wait(10)
        assert child.poll() is not None
    finally:
        kill_process(child)


def test_an_unregistered_child_survives_cancellation() -> None:
    token = CancellationToken("t")
    child = _sleeper()

    try:
        token.register_process(child)
        token.unregister_process(child)
        token.cancel()

        time.sleep(0.2)
        assert child.poll() is None
    finally:
        kill_process(child)


def test_the_child_scope_registers_and_unregisters() -> None:
    token = CancellationToken("t")
    child = _sleeper()

    try:
        with token.child(child):
            assert child.poll() is None

        token.cancel()
        time.sleep(0.2)
        assert child.poll() is None
    finally:
        kill_process(child)


def test_kill_process_is_safe_on_an_exited_child() -> None:
    child = subprocess.Popen([sys.executable, "-c", "pass"])  # noqa: S603
    child.wait(10)
    kill_process(child)


def test_kill_process_escalates_to_kill() -> None:
    # A child that traps SIGTERM must still die.
    child = subprocess.Popen(  # noqa: S603
        [
            sys.executable,
            "-c",
            "import signal, time; signal.signal(signal.SIGTERM, lambda *a: None); time.sleep(60)",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    kill_process(child, grace=0.5)
    assert child.poll() is not None


# ---------------------------------------------------------------------------
# global registry
# ---------------------------------------------------------------------------


def test_registry_terminates_everything_it_holds() -> None:
    registry = ProcessRegistry()
    children = [_sleeper() for _ in range(3)]

    try:
        for child in children:
            registry.add(child)

        assert registry.terminate_all() == 3

        for child in children:
            child.wait(10)
            assert child.poll() is not None
    finally:
        for child in children:
            kill_process(child)


def test_registry_forgets_removed_children() -> None:
    registry = ProcessRegistry()
    child = _sleeper()

    try:
        registry.add(child)
        registry.remove(child)

        assert registry.terminate_all() == 0
        assert child.poll() is None
    finally:
        kill_process(child)


def test_terminate_all_on_an_empty_registry() -> None:
    assert ProcessRegistry().terminate_all() == 0

"""Logging that can never touch stdout.

Every handler installed here writes to stderr. The C# host captures stderr into a ring buffer and
copies it into the application log, so log lines are English and machine-greppable; only protocol
``message`` fields are Korean.
"""

from __future__ import annotations

import logging
import os
import sys
from typing import Final

LOGGER_NAME: Final = "ksubmaker_worker"

_configured = False


class _StderrHandler(logging.StreamHandler):
    """A StreamHandler pinned to the *current* ``sys.stderr``.

    ``logging.StreamHandler(sys.stderr)`` captures the stream object at construction time. The
    protocol guard reassigns ``sys.stdout`` (not stderr), but tests capture stderr by swapping the
    attribute, and pinning to the attribute keeps those captures working.
    """

    def __init__(self) -> None:
        super().__init__(stream=sys.stderr)

    @property
    def stream(self):  # type: ignore[override]
        return sys.stderr

    @stream.setter
    def stream(self, value) -> None:  # type: ignore[override]
        # Swallow the base class's attempt to pin a stream; the property above is authoritative.
        return


def configure(level: str | int | None = None) -> logging.Logger:
    """Install the stderr handler exactly once and return the package logger.

    The level comes from ``KSUBMAKER_WORKER_LOG_LEVEL`` when not passed explicitly, so a user can
    raise verbosity from the launcher without a rebuild.
    """
    global _configured

    logger = logging.getLogger(LOGGER_NAME)

    if level is None:
        level = os.environ.get("KSUBMAKER_WORKER_LOG_LEVEL", "INFO")

    resolved = logging.getLevelName(level.upper()) if isinstance(level, str) else level
    if not isinstance(resolved, int):
        resolved = logging.INFO

    logger.setLevel(resolved)

    if not _configured:
        handler = _StderrHandler()
        handler.setFormatter(
            logging.Formatter(
                fmt="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
                datefmt="%H:%M:%S",
            )
        )
        logger.addHandler(handler)
        # Never propagate: the root logger may have been given a stdout handler by a library.
        logger.propagate = False
        _configured = True

    return logger


def get_logger(name: str | None = None) -> logging.Logger:
    """Child logger under the package logger; configures on first use."""
    configure()
    return logging.getLogger(LOGGER_NAME if name is None else f"{LOGGER_NAME}.{name}")

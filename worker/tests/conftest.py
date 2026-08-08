"""Shared fixtures. Nothing here touches the network, a model or a GPU."""

from __future__ import annotations

import io
import json
from typing import Any, Iterator

import pytest

from ksubmaker_worker import protocol


class Channel(io.StringIO):
    """Captures the protocol channel and parses it back into events."""

    def lines(self) -> list[str]:
        return [line for line in self.getvalue().split("\n") if line]

    def events(self) -> list[dict[str, Any]]:
        return [json.loads(line) for line in self.lines()]

    def of_type(self, event_type: str) -> list[dict[str, Any]]:
        return [event for event in self.events() if event.get("type") == event_type]

    def first(self, event_type: str) -> dict[str, Any] | None:
        matches = self.of_type(event_type)
        return matches[0] if matches else None


@pytest.fixture
def channel() -> Iterator[Channel]:
    """Redirect protocol output into an in-memory buffer for the duration of a test."""
    original = protocol.get_channel()
    buffer = Channel()
    protocol.set_channel(buffer)
    try:
        yield buffer
    finally:
        protocol.set_channel(original)


@pytest.fixture
def segments() -> list[dict[str, Any]]:
    """A small, well-formed transcript."""
    return [
        {"id": 1, "start": 0.0, "end": 2.0, "text": "Hello there.", "words": []},
        {"id": 2, "start": 2.5, "end": 5.0, "text": "How are you today?", "words": []},
        {"id": 3, "start": 5.5, "end": 9.0, "text": "I am fine, thanks.", "words": []},
    ]


def make_segments(count: int, *, text: str = "line", seconds: float = 2.0) -> list[dict[str, Any]]:
    """``count`` segments of ``seconds`` each, ids 1..count."""
    return [
        {
            "id": i,
            "start": (i - 1) * seconds,
            "end": i * seconds,
            "text": f"{text} {i}",
            "words": [],
        }
        for i in range(1, count + 1)
    ]

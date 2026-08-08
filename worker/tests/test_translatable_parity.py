"""The Python half of the two-language contract for "is there anything to translate here?".

Replays ``tests/fixtures/translation/untranslatable-segments.json`` — the same file
``KSubMaker.UnitTests.Parity.TranslatableTextParityTests`` reads — through
:func:`~ksubmaker_worker.batching.has_translatable_content` and
:func:`~ksubmaker_worker.batching.is_mostly_untranslated`. Neither language may answer differently:
a cue the host passes through untouched and the worker sends to NLLB (or the reverse) is exactly the
divergence this file exists to catch.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from ksubmaker_worker.batching import has_translatable_content, is_mostly_untranslated

#: worker/tests/ -> worker/ -> repository root.
_FIXTURE = (
    Path(__file__).resolve().parents[2]
    / "tests"
    / "fixtures"
    / "translation"
    / "untranslatable-segments.json"
)


def _fixture() -> dict[str, Any]:
    assert _FIXTURE.is_file(), f"the shared parity fixture is missing: {_FIXTURE}"
    return json.loads(_FIXTURE.read_text(encoding="utf-8"))


_CASES = _fixture()


@pytest.mark.parametrize(
    ("text", "expected", "why"),
    [(c["text"], c["expected"], c["why"]) for c in _CASES["translatable"]],
)
def test_the_shared_untranslatable_cases_get_the_same_answer_here_as_in_csharp(
    text: str, expected: bool, why: str
) -> None:
    assert has_translatable_content(text) is expected, why


@pytest.mark.parametrize(
    ("unusable", "requested", "expected", "why"),
    [
        (c["unusable"], c["requested"], c["expected"], c["why"])
        for c in _CASES["mostlyUntranslated"]
    ],
)
def test_the_shared_mostly_untranslated_cases_get_the_same_answer_here_as_in_csharp(
    unusable: int, requested: int, expected: bool, why: str
) -> None:
    assert is_mostly_untranslated(unusable, requested) is expected, why


def test_the_fixture_actually_contains_cases_of_both_kinds() -> None:
    # A fixture that silently stopped loading would make every parametrised case above vanish
    # rather than fail.
    assert len(_CASES["translatable"]) > 20
    assert len(_CASES["mostlyUntranslated"]) > 6

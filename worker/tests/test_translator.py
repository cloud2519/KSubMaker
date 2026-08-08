"""NLLB engine: language mapping, style, glossary and the id contract.

No model is loaded anywhere here: the CTranslate2 translator and the HF tokenizer are faked.
"""

from __future__ import annotations

from typing import Any

import pytest

from conftest import make_segments
from ksubmaker_worker import errors
from ksubmaker_worker.batching import BatchOptions
from ksubmaker_worker.translator import (
    FALLBACK_LANGUAGE_CODE,
    TARGET_LANGUAGE_CODE,
    FakeTranslator,
    NllbTranslator,
    apply_glossary,
    apply_style,
    to_nllb_code,
)

# ---------------------------------------------------------------------------
# language mapping
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("language", "expected"),
    [
        ("en", "eng_Latn"),
        ("ja", "jpn_Jpan"),
        ("zh", "zho_Hans"),
        ("es", "spa_Latn"),
        ("fr", "fra_Latn"),
        ("de", "deu_Latn"),
        ("ru", "rus_Cyrl"),
        ("ar", "arb_Arab"),
        ("ko", "kor_Hang"),
        ("EN", "eng_Latn"),
        ("zh-TW", "zho_Hant"),
        ("en-US", "eng_Latn"),
        ("pt_BR", "por_Latn"),
    ],
)
def test_language_mapping(language: str, expected: str) -> None:
    assert to_nllb_code(language) == expected


@pytest.mark.parametrize("unknown", ["xx", "klingon", "", None, "zz-ZZ"])
def test_unknown_language_falls_back(unknown: str | None) -> None:
    assert to_nllb_code(unknown) == FALLBACK_LANGUAGE_CODE


def test_an_nllb_code_passes_through() -> None:
    assert to_nllb_code("nld_Latn") == "nld_Latn"


def test_the_target_is_always_korean() -> None:
    assert TARGET_LANGUAGE_CODE == "kor_Hang"


# ---------------------------------------------------------------------------
# style
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("style", ["natural", "literal", "preserve", "unknown-style"])
def test_untouched_styles_pass_text_through(style: str) -> None:
    assert apply_style("그는 학교에 간다.", style) == "그는 학교에 간다."


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("그는 학교에 간다. 나도 간다.", "그는 학교에 간다. 나도 간다."),
        ("그것은 사실이다.", "그것은 사실입니다."),
        ("문제가 있다.", "문제가 있습니다."),
        ("아무것도 없다.", "아무것도 없습니다."),
    ],
)
def test_polite_style_normalises_sentence_endings(text: str, expected: str) -> None:
    assert apply_style(text, "polite") == expected


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("그것은 사실입니다.", "그것은 사실이야."),
        ("문제가 있습니다.", "문제가 있어."),
        ("괜찮아요.", "괜찮아."),
    ],
)
def test_casual_style_normalises_sentence_endings(text: str, expected: str) -> None:
    assert apply_style(text, "casual") == expected


def test_style_leaves_empty_text_alone() -> None:
    assert apply_style("", "polite") == ""


# ---------------------------------------------------------------------------
# glossary
# ---------------------------------------------------------------------------


def test_glossary_substitutes_terms() -> None:
    assert apply_glossary("Kubernetes 클러스터", {"Kubernetes": "쿠버네티스"}) == "쿠버네티스 클러스터"


def test_glossary_prefers_the_longest_key() -> None:
    glossary = {"New York": "뉴욕", "New York City": "뉴욕시"}
    assert apply_glossary("I love New York City", glossary) == "I love 뉴욕시"


def test_ascii_keys_respect_word_boundaries() -> None:
    # "AI" must not fire inside "SAID".
    assert apply_glossary("SAID AI things", {"AI": "인공지능"}) == "SAID 인공지능 things"


def test_hangul_keys_substitute_directly() -> None:
    assert apply_glossary("깃허브 저장소", {"깃허브": "GitHub"}) == "GitHub 저장소"


def test_glossary_is_case_insensitive_for_ascii() -> None:
    assert apply_glossary("kubernetes 클러스터", {"Kubernetes": "쿠버네티스"}) == "쿠버네티스 클러스터"


def test_empty_glossary_is_a_no_op() -> None:
    assert apply_glossary("변경 없음", {}) == "변경 없음"
    assert apply_glossary("변경 없음", None) == "변경 없음"


def test_glossary_replacement_with_backslashes_is_literal() -> None:
    assert apply_glossary("path X here", {"X": r"C:\temp"}) == r"path C:\temp here"


# ---------------------------------------------------------------------------
# engine plumbing, with fakes
# ---------------------------------------------------------------------------


class FakeTokenizer:
    """Just enough of the HF tokenizer surface for the engine to run.

    It reproduces the one behaviour the source-language bug turned on: the real NLLB tokenizer
    prefixes each sequence with whatever ``src_lang`` currently holds, and starts at ``eng_Latn``
    because every CT2 conversion leaves ``src_lang`` null in tokenizer_config.json.
    """

    def __init__(self) -> None:
        self.src_lang = "eng_Latn"

    def encode(self, text: str) -> list[int]:
        return [ord(c) for c in text]

    def convert_ids_to_tokens(self, ids: list[int]) -> list[str]:
        return [self.src_lang, *(chr(i) for i in ids)]

    def convert_tokens_to_ids(self, tokens: list[str]) -> list[int]:
        return [ord(t[0]) if t else 0 for t in tokens]

    def decode(self, ids: list[int], skip_special_tokens: bool = True) -> str:  # noqa: FBT001,FBT002
        return "".join(chr(i) for i in ids)


class FakeResult:
    def __init__(self, tokens: list[str]) -> None:
        self.hypotheses = [tokens]


class FakeCt2Translator:
    """Records every call and echoes a marked translation."""

    def __init__(self) -> None:
        self.calls: list[dict[str, Any]] = []

    def translate_batch(self, sequences, **kwargs):  # noqa: ANN001, ANN201
        self.calls.append({"sequences": sequences, "kwargs": kwargs})
        return [FakeResult([TARGET_LANGUAGE_CODE, "번", "역"]) for _ in sequences]


def _engine() -> tuple[NllbTranslator, FakeCt2Translator]:
    engine = NllbTranslator()
    fake = FakeCt2Translator()
    engine._translator = fake  # noqa: SLF001 - test seam
    engine._tokenizer = FakeTokenizer()  # noqa: SLF001
    return engine, fake


def test_translate_items_returns_one_entry_per_id() -> None:
    engine, _ = _engine()
    items = [{"id": 3, "text": "one"}, {"id": 7, "text": "two"}, {"id": 9, "text": "three"}]

    result = engine.translate_items(items, source_language="en")

    assert [r["id"] for r in result] == [3, 7, 9]
    assert all(r["translation"] for r in result)


def test_each_cue_is_its_own_sequence() -> None:
    # NLLB is sentence-level: concatenating cues is the only way an id could ever drift.
    engine, fake = _engine()
    engine.translate_items(
        [{"id": 1, "text": "first"}, {"id": 2, "text": "second"}], source_language="en"
    )

    assert len(fake.calls) == 1
    assert len(fake.calls[0]["sequences"]) == 2


def test_the_target_prefix_forces_korean() -> None:
    engine, fake = _engine()
    engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert fake.calls[0]["kwargs"]["target_prefix"] == [[TARGET_LANGUAGE_CODE]]


def test_the_forced_language_token_is_stripped_from_the_output() -> None:
    engine, _ = _engine()
    result = engine.translate_items([{"id": 1, "text": "hello"}], source_language="en")

    assert TARGET_LANGUAGE_CODE not in result[0]["translation"]
    assert result[0]["translation"] == "번역"


def test_the_source_language_reaches_the_model() -> None:
    """The defect: to_nllb_code computed jpn_Jpan and nothing ever handed it to the tokenizer, so
    NllbTokenizer kept its eng_Latn default and every Japanese cue was translated as English —
    which comes back blank or as a copy of the source, and lands in the SRT still in Japanese."""
    engine, fake = _engine()

    engine.translate_items([{"id": 1, "text": "こんにちは"}], source_language="ja")

    assert engine._tokenizer.src_lang == "jpn_Jpan"  # noqa: SLF001 - test seam
    assert fake.calls[0]["sequences"][0][0] == "jpn_Jpan"


def test_the_source_language_is_refreshed_between_calls() -> None:
    # One loaded engine serves the whole queue, so a mixed-language folder must not inherit the
    # language of the file that happened to run first.
    engine, fake = _engine()

    engine.translate_items([{"id": 1, "text": "こんにちは"}], source_language="ja")
    engine.translate_items([{"id": 2, "text": "hello"}], source_language="en")

    assert fake.calls[0]["sequences"][0][0] == "jpn_Jpan"
    assert fake.calls[1]["sequences"][0][0] == "eng_Latn"


def test_blank_cues_never_reach_the_model() -> None:
    # NLLB happily hallucinates a whole sentence out of an empty string.
    engine, fake = _engine()
    result = engine.translate_items(
        [{"id": 1, "text": "  "}, {"id": 2, "text": "real"}], source_language="en"
    )

    assert len(fake.calls[0]["sequences"]) == 1
    assert result[0]["translation"] == ""
    assert result[1]["translation"] == "번역"


def test_style_and_glossary_are_applied_to_the_output() -> None:
    engine, _ = _engine()

    class GlossaryTokenizer(FakeTokenizer):
        def decode(self, ids, skip_special_tokens=True):  # noqa: ANN001, FBT002
            return "쿠버 클러스터입니다."

    engine._tokenizer = GlossaryTokenizer()  # noqa: SLF001
    result = engine.translate_items(
        [{"id": 1, "text": "k8s cluster"}],
        source_language="en",
        style="casual",
        glossary={"쿠버": "Kubernetes"},
    )

    assert result[0]["translation"] == "Kubernetes 클러스터야."


def test_translating_without_a_loaded_model_is_an_error() -> None:
    engine = NllbTranslator()
    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "x"}], source_language="en")

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND


def test_empty_input_short_circuits() -> None:
    engine, fake = _engine()
    assert engine.translate_items([], source_language="en") == []
    assert fake.calls == []


def test_cuda_oom_from_the_model_is_classified() -> None:
    engine, _ = _engine()

    class OomTranslator:
        def translate_batch(self, *_args, **_kwargs):  # noqa: ANN002, ANN003, ANN201
            raise RuntimeError("CUDA failed with error out of memory")

    engine._translator = OomTranslator()  # noqa: SLF001

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "x"}], source_language="en")

    assert excinfo.value.code == errors.CUDA_OUT_OF_MEMORY
    assert excinfo.value.recoverable is True


def test_a_missing_cuda_library_during_translation_is_classified() -> None:
    """Same defect as the ASR path: the translator loads CTranslate2 too, so it hits the same
    missing cublas64_12.dll and must not report it as a generic 번역 실패."""
    engine, _ = _engine()

    class BrokenTranslator:
        def translate_batch(self, *_args, **_kwargs):  # noqa: ANN002, ANN003, ANN201
            raise RuntimeError("Library cublas64_12.dll is not found or cannot be loaded")

    engine._translator = BrokenTranslator()  # noqa: SLF001

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "x"}], source_language="en")

    assert excinfo.value.code == errors.CUDA_LIBRARY_MISSING
    assert excinfo.value.recoverable is False
    assert "cublas64_12.dll" in excinfo.value.message


def test_a_missing_cuda_library_while_loading_the_model_is_classified(tmp_path) -> None:
    """load() must classify it too — a fresh worker fails there before it ever translates."""
    import sys
    import types

    directory = tmp_path / "nllb-200-distilled-600M"
    directory.mkdir()
    (directory / "model.bin").write_bytes(b"fake")

    fake_ct2 = types.ModuleType("ctranslate2")

    def explode(*_args, **_kwargs):  # noqa: ANN002, ANN003, ANN202
        raise RuntimeError("Library cudnn64_9.dll is not found or cannot be loaded")

    fake_ct2.Translator = explode  # type: ignore[attr-defined]
    sys.modules["ctranslate2"] = fake_ct2

    try:
        engine = NllbTranslator(tmp_path)
        with pytest.raises(errors.WorkerError) as excinfo:
            engine.load(model_id="nllb-200-distilled-600M", device="cpu")
    finally:
        sys.modules.pop("ctranslate2", None)

    assert excinfo.value.code == errors.CUDA_LIBRARY_MISSING
    assert excinfo.value.recoverable is False


def test_other_model_errors_become_translation_failed() -> None:
    engine, _ = _engine()

    class BrokenTranslator:
        def translate_batch(self, *_args, **_kwargs):  # noqa: ANN002, ANN003, ANN201
            raise RuntimeError("something else entirely")

    engine._translator = BrokenTranslator()  # noqa: SLF001

    with pytest.raises(errors.WorkerError) as excinfo:
        engine.translate_items([{"id": 1, "text": "x"}], source_language="en")

    assert excinfo.value.code == errors.TRANSLATION_FAILED


def test_missing_local_model_raises_translation_model_not_found(tmp_path) -> None:
    engine = NllbTranslator(tmp_path)
    with pytest.raises(errors.WorkerError) as excinfo:
        engine.load(model_id="nllb-200-distilled-600M")

    assert excinfo.value.code == errors.TRANSLATION_MODEL_NOT_FOUND


def test_unload_is_safe_when_nothing_is_loaded() -> None:
    NllbTranslator().unload()


def test_translate_segments_covers_every_id() -> None:
    engine, _ = _engine()
    segments = make_segments(25)

    result = engine.translate_segments(
        segments,
        source_language="en",
        options=BatchOptions(max_items=7),
    )

    assert set(result) == {s["id"] for s in segments}


def test_on_batch_done_is_called_per_batch() -> None:
    engine, _ = _engine()
    seen: list[int] = []

    engine.translate_segments(
        make_segments(10),
        source_language="en",
        options=BatchOptions(max_items=4),
        on_batch_done=lambda batch, _all: seen.append(batch.index),
    )

    assert seen == [0, 1, 2]


# ---------------------------------------------------------------------------
# the deterministic fake engine
# ---------------------------------------------------------------------------


def test_fake_translator_returns_one_entry_per_id() -> None:
    items = [{"id": 5, "text": "hello"}, {"id": 6, "text": ""}]
    result = FakeTranslator().translate_items(items)

    assert [r["id"] for r in result] == [5, 6]
    assert all(r["translation"].strip() for r in result)


def test_fake_translator_covers_a_whole_transcript() -> None:
    segments = make_segments(12)
    result = FakeTranslator().translate_segments(segments, options=BatchOptions(max_items=5))

    assert set(result) == {s["id"] for s in segments}

"""Batch splitting, response validation and the retry-only-missing-ids loop."""

from __future__ import annotations

from typing import Any

import pytest

from conftest import make_segments
from ksubmaker_worker import errors
from ksubmaker_worker.batching import (
    MOSTLY_UNTRANSLATED_MIN_CUES,
    MOSTLY_UNTRANSLATED_RATIO,
    Batch,
    BatchOptions,
    has_translatable_content,
    is_mostly_untranslated,
    split_batches,
    to_map,
    translate_with_retry,
    validate,
)
from ksubmaker_worker.cancellation import CancellationToken

# ---------------------------------------------------------------------------
# splitting
# ---------------------------------------------------------------------------


def test_splits_on_the_item_limit() -> None:
    batches = split_batches(make_segments(75), BatchOptions(max_items=30, max_chars=10**6, max_seconds=10**6))

    assert [len(b.segments) for b in batches] == [30, 30, 15]
    assert [b.index for b in batches] == [0, 1, 2]


def test_splits_on_the_character_limit() -> None:
    segments = [
        {"id": i, "start": 0.0, "end": 1.0, "text": "x" * 100, "words": []} for i in range(1, 11)
    ]
    batches = split_batches(
        segments, BatchOptions(max_items=1000, max_chars=250, max_seconds=10**6)
    )

    # 250 chars / 100 per item = two items per batch.
    assert [len(b.segments) for b in batches] == [2, 2, 2, 2, 2]


def test_splits_on_the_media_duration_limit() -> None:
    # 10 s per segment, 45 s budget -> the span from the batch's first start closes at 5 items.
    segments = make_segments(12, seconds=10.0)
    batches = split_batches(
        segments, BatchOptions(max_items=1000, max_chars=10**6, max_seconds=45.0)
    )

    assert len(batches) > 1
    for batch in batches:
        span = batch.segments[-1]["end"] - batch.segments[0]["start"]
        # A batch may only exceed the budget when it holds a single over-long segment.
        assert span <= 45.0 or len(batch.segments) == 1


def test_whichever_limit_comes_first_wins() -> None:
    segments = [
        {"id": i, "start": (i - 1) * 1.0, "end": i * 1.0, "text": "y" * 60, "words": []}
        for i in range(1, 21)
    ]
    # 5 items would be 300 chars, so the character limit (200) closes first at 3 items.
    batches = split_batches(
        segments, BatchOptions(max_items=5, max_chars=200, max_seconds=1000)
    )
    assert all(len(b.segments) <= 3 for b in batches)


def test_an_oversized_single_segment_still_gets_its_own_batch() -> None:
    segments = [{"id": 1, "start": 0.0, "end": 900.0, "text": "z" * 9000, "words": []}]
    batches = split_batches(segments, BatchOptions(max_items=30, max_chars=100, max_seconds=10))

    assert len(batches) == 1
    assert batches[0].ids == [1]


def test_context_is_the_previous_batch_tail() -> None:
    batches = split_batches(
        make_segments(9), BatchOptions(max_items=3, max_chars=10**6, max_seconds=10**6, context_items=2)
    )

    assert batches[0].context == []
    assert [c["id"] for c in batches[1].context] == [2, 3]
    assert [c["id"] for c in batches[2].context] == [5, 6]


def test_context_can_be_switched_off() -> None:
    batches = split_batches(
        make_segments(6), BatchOptions(max_items=3, context_items=0)
    )
    assert all(batch.context == [] for batch in batches)


def test_empty_input_produces_no_batches() -> None:
    assert split_batches([]) == []


def test_batch_items_carry_only_id_and_text() -> None:
    batch = split_batches(make_segments(2))[0]
    assert batch.items == [{"id": 1, "text": "line 1"}, {"id": 2, "text": "line 2"}]


def test_batch_options_from_settings_applies_floors_to_positive_values() -> None:
    options = BatchOptions.from_settings(
        {"batchMaxItems": 1, "batchMaxChars": 1, "batchMaxSeconds": 1, "contextLines": -4}
    )
    assert options.max_items == 1
    assert options.max_chars == 50
    assert options.max_seconds == 5.0
    assert options.context_items == 0


def test_batch_options_from_settings_clamps_zero_like_the_domain_batcher() -> None:
    # C# does Math.Max(1, MaxItems) on a value that is always present, so a wire 0 clamps rather
    # than reverting to the default.
    options = BatchOptions.from_settings(
        {"batchMaxItems": 0, "batchMaxChars": 0, "batchMaxSeconds": 0}
    )
    assert (options.max_items, options.max_chars, options.max_seconds) == (1, 50, 5.0)


def test_batch_options_from_settings_falls_back_when_a_key_is_absent() -> None:
    options = BatchOptions.from_settings({"batchMaxItems": None})
    assert options.max_items == 30


def test_batch_options_defaults_match_the_wire_contract() -> None:
    options = BatchOptions.from_settings({})
    assert (options.max_items, options.max_chars, options.max_seconds, options.context_items) == (
        30,
        2500,
        180.0,
        3,
    )


# ---------------------------------------------------------------------------
# validation
# ---------------------------------------------------------------------------

REQUESTED = [{"id": 1, "text": "a"}, {"id": 2, "text": "b"}, {"id": 3, "text": "c"}]


def test_valid_response() -> None:
    returned = [{"id": i, "translation": f"번역{i}"} for i in (1, 2, 3)]
    result = validate(REQUESTED, returned)

    assert result.is_valid
    assert result.describe() == "정상"


def test_reordering_is_allowed() -> None:
    returned = [{"id": 3, "translation": "다"}, {"id": 1, "translation": "가"}, {"id": 2, "translation": "나"}]
    assert validate(REQUESTED, returned).is_valid


def test_missing_id_is_rejected() -> None:
    result = validate(REQUESTED, [{"id": 1, "translation": "가"}, {"id": 3, "translation": "다"}])

    assert not result.is_valid
    assert result.missing_ids == (2,)
    assert "누락" in result.describe()


def test_duplicate_id_is_rejected() -> None:
    returned = [
        {"id": 1, "translation": "가"},
        {"id": 1, "translation": "가2"},
        {"id": 2, "translation": "나"},
        {"id": 3, "translation": "다"},
    ]
    result = validate(REQUESTED, returned)

    assert not result.is_valid
    assert result.duplicate_ids == (1,)
    assert "중복" in result.describe()


def test_unexpected_id_is_rejected() -> None:
    returned = [{"id": i, "translation": "가"} for i in (1, 2, 3)] + [
        {"id": 99, "translation": "몰라"}
    ]
    result = validate(REQUESTED, returned)

    assert not result.is_valid
    assert result.unexpected_ids == (99,)
    assert "알 수 없는 id" in result.describe()


@pytest.mark.parametrize("blank", ["", "   ", "\n\t "])
def test_empty_translation_is_rejected(blank: str) -> None:
    returned = [
        {"id": 1, "translation": "가"},
        {"id": 2, "translation": blank},
        {"id": 3, "translation": "다"},
    ]
    result = validate(REQUESTED, returned)

    assert not result.is_valid
    assert result.empty_ids == (2,)
    assert "빈 번역" in result.describe()


def test_non_string_translation_counts_as_empty() -> None:
    returned = [
        {"id": 1, "translation": "가"},
        {"id": 2, "translation": None},
        {"id": 3, "translation": 42},
    ]
    result = validate(REQUESTED, returned)
    assert set(result.empty_ids) == {2, 3}


def test_unparseable_id_counts_as_unexpected() -> None:
    result = validate(REQUESTED, [{"id": "not-a-number", "translation": "가"}])
    assert result.unexpected_ids == (-1,)


def test_to_map_drops_blanks_and_trims() -> None:
    mapped = to_map(
        [{"id": 1, "translation": "  가  "}, {"id": 2, "translation": "  "}, {"id": 3}]
    )
    assert mapped == {1: "가"}


def test_retryable_ids_are_the_union_of_the_broken_ones() -> None:
    result = validate(
        REQUESTED,
        [{"id": 1, "translation": ""}, {"id": 2, "translation": "나"}, {"id": 2, "translation": "나"}],
    )
    assert result.retryable_ids == (1, 2, 3)


# ---------------------------------------------------------------------------
# retry loop
# ---------------------------------------------------------------------------


def _batch(count: int = 3) -> Batch:
    return Batch(index=0, segments=make_segments(count))


def test_retry_asks_only_for_the_missing_ids() -> None:
    asked: list[list[int]] = []

    def engine(items: list[dict[str, Any]], _ctx: list[dict[str, Any]], attempt: int):
        asked.append([i["id"] for i in items])
        if attempt == 1:
            return [{"id": 1, "translation": "가"}]
        return [{"id": i["id"], "translation": f"번역{i['id']}"} for i in items]

    result = translate_with_retry(_batch(3), engine)

    assert asked == [[1, 2, 3], [2, 3]]
    assert result == {1: "가", 2: "번역2", 3: "번역3"}


def test_retry_narrows_progressively() -> None:
    asked: list[list[int]] = []
    responses = [
        [{"id": 1, "translation": "가"}],
        [{"id": 2, "translation": "나"}],
        [{"id": 3, "translation": "다"}],
    ]

    def engine(items: list[dict[str, Any]], _ctx: list[dict[str, Any]], attempt: int):
        asked.append([i["id"] for i in items])
        return responses[attempt - 1]

    result = translate_with_retry(_batch(3), engine)

    assert asked == [[1, 2, 3], [2, 3], [3]]
    assert result == {1: "가", 2: "나", 3: "다"}


def test_an_engine_that_keeps_answering_with_the_wrong_id_fails_hard_and_early() -> None:
    # The engine answers id 1 forever. Attempt 2 asks only for [2, 3], so its reply is an id nobody
    # requested: the id contract itself is broken and no amount of asking again will fix it.
    calls = {"n": 0}

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        calls["n"] += 1
        return [{"id": 1, "translation": "가"}]

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(_batch(3), engine)

    assert excinfo.value.code == errors.INVALID_TRANSLATION_RESPONSE
    assert excinfo.value.recoverable is True
    # Stopped at 2, not 3: the missing set did not shrink between attempts and both engines here
    # are deterministic, so a third identical request would waste the same time again.
    assert calls["n"] == 2


def test_blank_translation_is_retried_not_accepted() -> None:
    def engine(items, _ctx, attempt):  # noqa: ANN001
        if attempt == 1:
            return [
                {"id": 1, "translation": "가"},
                {"id": 2, "translation": "   "},
                {"id": 3, "translation": "다"},
            ]
        return [{"id": i["id"], "translation": "나"} for i in items]

    assert translate_with_retry(_batch(3), engine) == {1: "가", 2: "나", 3: "다"}


def test_unexpected_ids_never_reach_the_result() -> None:
    def engine(items, _ctx, _attempt):  # noqa: ANN001
        return [{"id": i["id"], "translation": "가"} for i in items] + [
            {"id": 999, "translation": "쓰레기"}
        ]

    assert set(translate_with_retry(_batch(3), engine)) == {1, 2, 3}


def test_context_lines_echoed_back_are_discarded() -> None:
    batch = Batch(index=1, segments=make_segments(2), context=[{"id": 90, "text": "prev"}])

    def engine(items, ctx, _attempt):  # noqa: ANN001
        assert ctx == [{"id": 90, "text": "prev"}]
        return [{"id": i["id"], "translation": "가"} for i in items] + [
            {"id": 90, "translation": "이전"}
        ]

    assert set(translate_with_retry(batch, engine)) == {1, 2}


def test_on_retry_callback_receives_the_validation_result() -> None:
    seen: list[tuple[int, tuple[int, ...]]] = []

    def engine(items, _ctx, attempt):  # noqa: ANN001
        if attempt == 1:
            return []
        return [{"id": i["id"], "translation": "가"} for i in items]

    translate_with_retry(
        _batch(2), engine, on_retry=lambda attempt, result: seen.append((attempt, result.missing_ids))
    )

    assert seen == [(1, (1, 2))]


def test_engine_exception_becomes_a_translation_failure() -> None:
    def engine(items, _ctx, _attempt):  # noqa: ANN001
        raise ValueError("engine exploded")

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(_batch(2), engine)

    assert excinfo.value.code == errors.TRANSLATION_FAILED


def test_worker_errors_from_the_engine_pass_through_unchanged() -> None:
    def engine(items, _ctx, _attempt):  # noqa: ANN001
        raise errors.WorkerError(errors.CUDA_OUT_OF_MEMORY)

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(_batch(2), engine)

    assert excinfo.value.code == errors.CUDA_OUT_OF_MEMORY


def test_cancellation_stops_the_retry_loop() -> None:
    token = CancellationToken("t")
    token.cancel()

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        raise AssertionError("engine should never be called after cancellation")

    with pytest.raises(errors.CancelledError):
        translate_with_retry(_batch(2), engine, token=token)


def test_empty_batch_short_circuits() -> None:
    def engine(items, _ctx, _attempt):  # noqa: ANN001
        raise AssertionError("engine should never be called for an empty batch")

    assert translate_with_retry(Batch(index=0, segments=[]), engine) == {}


# ---------------------------------------------------------------------------
# "is there anything to translate here?"
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "text",
    [
        "♪",
        "♪♪♪",
        "～",
        "…",
        "。",
        "！？",
        "＊",
        "（）",
        "「」",
        "♬ ～ ♬",
        "-",
        "—",
        "...",
        "!?",
        "[ ]",
        "  ",
        "",
        "★☆★",
        "🎵",
    ],
)
def test_symbol_and_punctuation_only_text_has_nothing_to_translate(text: str) -> None:
    assert has_translatable_content(text) is False


@pytest.mark.parametrize(
    "text",
    [
        "こんにちは",          # Japanese kana
        "東京",                # kanji
        "안녕하세요",           # Hangul
        "Привет",              # Cyrillic
        "Γειά",                # Greek
        "مرحبا",               # Arabic
        "hello",               # Latin
        "3",                   # a bare digit is still content
        "♪ 星が見える ♪",       # symbols around real text
        "（笑）",               # brackets around real text
        "第1話",
    ],
)
def test_real_text_in_any_script_is_translatable(text: str) -> None:
    assert has_translatable_content(text) is True


def test_none_has_nothing_to_translate() -> None:
    assert has_translatable_content(None) is False


def test_symbol_only_segments_never_reach_the_engine() -> None:
    asked: list[list[int]] = []

    segments = [
        {"id": 1, "start": 0.0, "end": 1.0, "text": "♪", "words": []},
        {"id": 2, "start": 1.0, "end": 2.0, "text": "こんにちは", "words": []},
        {"id": 3, "start": 2.0, "end": 3.0, "text": "！？", "words": []},
    ]

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        asked.append([i["id"] for i in items])
        return [{"id": i["id"], "translation": "안녕하세요"} for i in items]

    result = translate_with_retry(Batch(index=0, segments=segments), engine)

    assert asked == [[2]], "only the cue with actual words is sent"
    # The symbol cues keep their id and their source text, so the SRT still shows them in place.
    assert result == {1: "♪", 2: "안녕하세요", 3: "！？"}


def test_a_batch_of_nothing_but_symbols_skips_the_engine_entirely() -> None:
    segments = [
        {"id": 1, "start": 0.0, "end": 1.0, "text": "♪", "words": []},
        {"id": 2, "start": 1.0, "end": 2.0, "text": "…", "words": []},
    ]

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        raise AssertionError("engine should never be called when nothing is translatable")

    assert translate_with_retry(Batch(index=0, segments=segments), engine) == {1: "♪", 2: "…"}


# ---------------------------------------------------------------------------
# degrade instead of abort
# ---------------------------------------------------------------------------


def test_a_deterministically_blank_line_degrades_to_its_source_text() -> None:
    """The reported bug: NLLB returns "" for one cue in thirty, forever."""
    segments = make_segments(30)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        return [
            {"id": i["id"], "translation": "" if i["id"] == 3 else f"번역{i['id']}"} for i in items
        ]

    result = translate_with_retry(Batch(index=0, segments=segments), engine)

    assert len(result) == 30, "the batch still comes back complete"
    assert result[3] == "line 3", "the untranslatable cue keeps its source text"
    assert result[4] == "번역4"


def test_the_degraded_ids_are_reported_to_the_caller() -> None:
    seen: list[tuple[int, ...]] = []
    segments = make_segments(10)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        return [
            {"id": i["id"], "translation": "" if i["id"] in (2, 7) else "가"} for i in items
        ]

    translate_with_retry(
        Batch(index=0, segments=segments), engine, on_degraded=seen.append
    )

    assert seen == [(2, 7)]


def test_retrying_stops_as_soon_as_the_missing_set_stops_shrinking() -> None:
    calls = {"n": 0}
    segments = make_segments(10)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        calls["n"] += 1
        return [{"id": i["id"], "translation": "" if i["id"] == 5 else "가"} for i in items]

    result = translate_with_retry(Batch(index=0, segments=segments), engine)

    # Attempt 1 leaves {5}; attempt 2 re-asks for exactly {5} and gets exactly the same answer, so
    # the third attempt is not worth its wall-clock time.
    assert calls["n"] == 2
    assert result[5] == "line 5"


def test_a_shrinking_missing_set_is_given_the_full_attempt_budget() -> None:
    calls = {"n": 0}
    segments = make_segments(6)

    def engine(items, _ctx, attempt):  # noqa: ANN001
        calls["n"] += 1
        # One more line comes back on each attempt, so retrying is demonstrably still helping.
        blank = {1: (4, 5, 6), 2: (5, 6), 3: (6,)}[attempt]
        return [
            {"id": i["id"], "translation": "" if i["id"] in blank else "가"} for i in items
        ]

    result = translate_with_retry(Batch(index=0, segments=segments), engine)

    assert calls["n"] == 3
    assert result[6] == "line 6", "the last straggler degrades to source text"
    assert result[5] == "가"


# ---------------------------------------------------------------------------
# ...but genuine corruption still fails hard
# ---------------------------------------------------------------------------


def test_a_mostly_blank_batch_is_a_broken_engine_not_a_quirky_line() -> None:
    segments = make_segments(10)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        # Only id 1 ever survives: 9 of 10 blank is not a content quirk.
        return [{"id": i["id"], "translation": "가" if i["id"] == 1 else ""} for i in items]

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(Batch(index=0, segments=segments), engine)

    assert excinfo.value.code == errors.INVALID_TRANSLATION_RESPONSE
    assert excinfo.value.recoverable is True
    assert "mostly empty" in (excinfo.value.detail or "")


def test_a_duplicate_id_that_survives_the_retries_fails_hard() -> None:
    segments = make_segments(3)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        # id 1 twice, id 3 never: the response does not line up with the request.
        return [{"id": 1, "translation": "가"}, {"id": 1, "translation": "가2"}]

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(Batch(index=0, segments=segments), engine)

    assert excinfo.value.code == errors.INVALID_TRANSLATION_RESPONSE
    assert "corrupt response" in (excinfo.value.detail or "")


def test_an_unparseable_id_fails_hard() -> None:
    segments = make_segments(3)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        return [{"id": "셋", "translation": "가"}]

    with pytest.raises(errors.WorkerError) as excinfo:
        translate_with_retry(Batch(index=0, segments=segments), engine)

    assert excinfo.value.code == errors.INVALID_TRANSLATION_RESPONSE
    assert "corrupt response" in (excinfo.value.detail or "")


def test_a_single_cue_batch_degrades_rather_than_failing() -> None:
    # The OOM ladder halves batches until they hold one segment. 1-of-1 blank is 100% unusable but
    # is exactly the ordinary case this behaviour exists to survive.
    segments = make_segments(1)

    def engine(items, _ctx, _attempt):  # noqa: ANN001
        return [{"id": i["id"], "translation": ""} for i in items]

    assert translate_with_retry(Batch(index=0, segments=segments), engine) == {1: "line 1"}


@pytest.mark.parametrize(
    ("unusable", "requested", "expected"),
    [
        (1, 30, False),   # the reported bug: one quirky line in a full batch
        (2, 30, False),
        (3, 6, False),    # over the ratio but under the cue floor
        (4, 8, True),
        (4, 4, True),
        (9, 10, True),
        (1, 1, False),    # a halved-to-one batch is never "mostly" anything
        (3, 3, False),
        (0, 30, False),
        (4, 0, False),    # nothing was requested, so nothing can be broken
    ],
)
def test_the_mostly_untranslated_threshold(unusable: int, requested: int, expected: bool) -> None:
    assert is_mostly_untranslated(unusable, requested) is expected


def test_the_threshold_constants_match_the_domain_rule() -> None:
    # Mirrored by TranslationValidator.MostlyUntranslatedRatio / MostlyUntranslatedMinimumCues;
    # TranslatableTextParityTests replays the same table through the C# implementation.
    assert MOSTLY_UNTRANSLATED_RATIO == 0.5
    assert MOSTLY_UNTRANSLATED_MIN_CUES == 4

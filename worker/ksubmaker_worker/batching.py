"""Translation batching, response validation and the retry loop.

Shared by both engines, because the failure mode this guards against — a model that quietly drops
or merges cues — is identical whether the model is NLLB or a chat LLM, and it is the worst kind of
failure: the output file looks fine and is simply missing lines.

Ports ``KSubMaker.Domain.Subtitles.TranslationBatcher`` and ``TranslationValidator``.
"""

from __future__ import annotations

import unicodedata
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Mapping, Protocol, Sequence

from . import errors
from .cancellation import CancellationToken
from .errors import WorkerError
from .logging_setup import get_logger

_log = get_logger("batching")

MAX_ATTEMPTS = 3

#: Fraction of a batch that has to come back unusable before the response is treated as a broken
#: engine rather than a few untranslatable lines.
#:
#: Half, and why. Once :func:`has_translatable_content` has removed the cues that contain no words
#: at all, what is left is real dialogue, and a working engine translates essentially all of it —
#: the field report behind this was 1 blank cue in 30, twice, across two whole films. There is no
#: plausible content-shaped reason for half a batch of ordinary dialogue to come back empty; that
#: pattern means the wrong source-language code, a model that never finished loading, or an LLM that
#: has stopped following the output format. Degrading there would ship a file that is half
#: untranslated source text, which is worse for the user than an error they can act on. Below the
#: threshold the opposite holds: failing the job throws away every cue that *did* translate.
#:
#: Mirrors ``TranslationValidator.MostlyUntranslatedRatio``.
MOSTLY_UNTRANSLATED_RATIO = 0.5

#: Floor on the absolute number of unusable cues, so the ratio cannot fire on a tiny batch. The
#: CUDA-OOM ladder halves batches repeatedly and can hand the engine a single segment; one blank cue
#: out of one is 100% and would otherwise look like total failure when it is exactly the ordinary
#: case this whole change exists to survive.
#:
#: Mirrors ``TranslationValidator.MostlyUntranslatedMinimumCues``.
MOSTLY_UNTRANSLATED_MIN_CUES = 4


def has_translatable_content(text: str | None) -> bool:
    """Is there anything here for a translation engine to do?

    True when ``text`` holds at least one letter or decimal digit in any script. Symbols,
    punctuation, marks, separators and control characters do not count.

    Japanese subtitles are full of cues that carry no words at all — ``♪`` for a song sting, ``～``
    for a drawn-out vowel, ``…``, ``。``, ``！？``, ``＊``, a lone bracket pair. NLLB deterministically
    returns an empty string for those, :func:`validate` counts the blank as a corrupt response, and
    one such cue was enough to fail an entire job. They never reach the engine now.

    The test is Unicode-wide on purpose: an ASCII-only "has a letter" check would classify every
    Japanese, Korean, Cyrillic, Greek, Arabic or Thai line as untranslatable, which is the precise
    opposite of what is wanted. ``KSubMaker.Domain.Subtitles.TranslatableText`` is the C# half of the
    rule and ``TranslatableTextParityTests`` replays a shared fixture through both.
    """
    if not text:
        return False

    for character in text:
        category = unicodedata.category(character)
        # "L*" covers every letter category (Lu Ll Lt Lm Lo) and "Nd" the decimal digits, which is
        # exactly what System.Text.Rune.IsLetterOrDigit accepts on the C# side.
        if category[0] == "L" or category == "Nd":
            return True

    return False


def is_mostly_untranslated(unusable_count: int, requested_count: int) -> bool:
    """Is this response broken rather than merely incomplete?

    Both :data:`MOSTLY_UNTRANSLATED_RATIO` and :data:`MOSTLY_UNTRANSLATED_MIN_CUES` must be met.
    Mirrors ``TranslationValidator.IsMostlyUntranslated``.
    """
    if requested_count <= 0 or unusable_count < MOSTLY_UNTRANSLATED_MIN_CUES:
        return False

    return unusable_count >= requested_count * MOSTLY_UNTRANSLATED_RATIO


def _int_setting(settings: Mapping[str, Any], key: str, default: int) -> int:
    """Read an int setting; absent or null means "use the wire default"."""
    value = settings.get(key)
    if value is None:
        return default
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _float_setting(settings: Mapping[str, Any], key: str, default: float) -> float:
    value = settings.get(key)
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


@dataclass(frozen=True)
class BatchOptions:
    """Limits at which a batch closes. Whichever is hit first wins."""

    max_items: int = 30
    max_chars: int = 2500
    max_seconds: float = 180.0
    context_items: int = 3

    @classmethod
    def from_settings(cls, settings: Mapping[str, Any]) -> "BatchOptions":
        """Same clamps as ``TranslationBatcher.Split``.

        A key that is absent or null falls back to the wire default; a present value is clamped,
        so a host that sends 0 gets the smallest workable batch rather than a silent default.
        """
        return cls(
            max_items=max(1, _int_setting(settings, "batchMaxItems", 30)),
            max_chars=max(50, _int_setting(settings, "batchMaxChars", 2500)),
            max_seconds=max(5.0, _float_setting(settings, "batchMaxSeconds", 180)),
            context_items=max(0, _int_setting(settings, "contextLines", 3)),
        )


@dataclass
class Batch:
    """A unit of work for a translation engine, plus its read-only preceding context."""

    index: int
    segments: list[dict[str, Any]] = field(default_factory=list)
    context: list[dict[str, Any]] = field(default_factory=list)

    @property
    def items(self) -> list[dict[str, Any]]:
        return [{"id": s["id"], "text": s.get("text", "")} for s in self.segments]

    @property
    def context_items(self) -> list[dict[str, Any]]:
        return [{"id": s["id"], "text": s.get("text", "")} for s in self.context]

    @property
    def ids(self) -> list[int]:
        return [int(s["id"]) for s in self.segments]


def split_batches(
    segments: Sequence[dict[str, Any]],
    options: BatchOptions | None = None,
) -> list[Batch]:
    """Split a transcript into batches at the item / character / media-duration limits.

    A single segment always gets its own batch even when it exceeds a limit on its own: dropping
    it would be worse than sending an over-budget request.
    """
    opts = options or BatchOptions()
    max_items = max(1, opts.max_items)
    max_chars = max(50, opts.max_chars)
    max_seconds = max(5.0, opts.max_seconds)

    batches: list[Batch] = []
    current: list[dict[str, Any]] = []
    chars = 0

    def flush() -> None:
        nonlocal current, chars
        if not current:
            return

        context: list[dict[str, Any]] = []
        if batches and opts.context_items > 0:
            context = list(batches[-1].segments[-opts.context_items :])

        batches.append(Batch(index=len(batches), segments=list(current), context=context))
        current = []
        chars = 0

    for segment in segments:
        text = str(segment.get("text", "") or "")

        if current:
            would_exceed_items = len(current) + 1 > max_items
            would_exceed_chars = chars + len(text) > max_chars
            would_exceed_span = (
                float(segment.get("end", 0.0) or 0.0) - float(current[0].get("start", 0.0) or 0.0)
                > max_seconds
            )

            if would_exceed_items or would_exceed_chars or would_exceed_span:
                flush()

        current.append(segment)
        chars += len(text)

    flush()
    return batches


# ---------------------------------------------------------------------------
# validation
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ValidationResult:
    is_valid: bool
    missing_ids: tuple[int, ...] = ()
    duplicate_ids: tuple[int, ...] = ()
    unexpected_ids: tuple[int, ...] = ()
    empty_ids: tuple[int, ...] = ()

    def describe(self) -> str:
        """Korean summary, mirroring ``TranslationValidationResult.Describe``."""
        if self.is_valid:
            return "정상"

        parts: list[str] = []
        if self.missing_ids:
            head = ",".join(str(i) for i in self.missing_ids[:5])
            parts.append(f"누락 {len(self.missing_ids)}건({head}…)")
        if self.duplicate_ids:
            parts.append(f"중복 {len(self.duplicate_ids)}건")
        if self.unexpected_ids:
            parts.append(f"알 수 없는 id {len(self.unexpected_ids)}건")
        if self.empty_ids:
            parts.append(f"빈 번역 {len(self.empty_ids)}건")
        return ", ".join(parts)

    @property
    def retryable_ids(self) -> tuple[int, ...]:
        """Ids worth asking for again: everything that did not come back usable."""
        return tuple(sorted(set(self.missing_ids) | set(self.empty_ids) | set(self.duplicate_ids)))

    @property
    def is_corrupt(self) -> bool:
        """Did the response break the id contract itself, rather than just fail to translate?

        An id nobody asked for, the same id twice, or an id that could not be parsed at all (which
        :func:`validate` records as the sentinel ``-1`` in ``unexpected_ids``).

        This is the distinction that decides whether a batch degrades or fails: a blank translation
        is a quirky line, while a response whose ids do not line up is an engine that is not
        answering the question, and shipping its output would put the wrong Korean under the wrong
        timecode. Mirrors ``TranslationValidationResult.IsCorrupt``.
        """
        return bool(self.unexpected_ids or self.duplicate_ids)


def validate(
    requested: Sequence[dict[str, Any]],
    returned: Sequence[dict[str, Any]],
) -> ValidationResult:
    """Check a translation response against what was asked for.

    Reordering is allowed (the caller rejoins by id); inventing, dropping, duplicating or blanking
    an item is not.
    """
    requested_ids = {int(item["id"]) for item in requested}
    seen: set[int] = set()
    duplicates: list[int] = []
    unexpected: list[int] = []
    empty: list[int] = []

    for item in returned:
        raw_id = item.get("id")
        try:
            item_id = int(raw_id)
        except (TypeError, ValueError):
            # An unparseable id cannot be matched to anything, so it counts as unexpected.
            unexpected.append(-1)
            continue

        if item_id in seen:
            duplicates.append(item_id)
            continue
        seen.add(item_id)

        if item_id not in requested_ids:
            unexpected.append(item_id)
            continue

        translation = item.get("translation")
        if not isinstance(translation, str) or not translation.strip():
            empty.append(item_id)

    missing = tuple(sorted(requested_ids - seen))

    return ValidationResult(
        is_valid=not missing and not duplicates and not unexpected and not empty,
        missing_ids=missing,
        duplicate_ids=tuple(duplicates),
        unexpected_ids=tuple(unexpected),
        empty_ids=tuple(empty),
    )


def to_map(returned: Iterable[dict[str, Any]]) -> dict[int, str]:
    """Rejoin a response by id, dropping blanks. Mirrors ``TranslationValidator.ToMap``."""
    result: dict[int, str] = {}
    for item in returned:
        try:
            item_id = int(item["id"])
        except (KeyError, TypeError, ValueError):
            continue
        translation = item.get("translation")
        if isinstance(translation, str) and translation.strip():
            result[item_id] = translation.strip()
    return result


# ---------------------------------------------------------------------------
# retry loop
# ---------------------------------------------------------------------------


class TranslateCallable(Protocol):
    def __call__(
        self,
        items: list[dict[str, Any]],
        context: list[dict[str, Any]],
        attempt: int,
    ) -> list[dict[str, Any]]: ...


def translate_with_retry(
    batch: Batch,
    translate: TranslateCallable,
    *,
    max_attempts: int = MAX_ATTEMPTS,
    token: CancellationToken | None = None,
    on_retry: Callable[[int, ValidationResult], None] | None = None,
    on_degraded: Callable[[tuple[int, ...]], None] | None = None,
) -> dict[int, str]:
    """Translate one batch, retrying **only the still-missing ids**.

    Re-sending the whole batch would throw away good work and, with a nondeterministic LLM, could
    corrupt lines that were already correct.

    Three rules shape what happens when a line will not translate:

    1. **Cues with nothing to translate never reach the engine.** See
       :func:`has_translatable_content`. Their source text is carried through unchanged so the cue
       keeps its id and its timing and still appears in the SRT.
    2. **Retrying stops as soon as it stops helping.** Both engines here are deterministic, so an
       attempt that returns exactly the same missing ids as the previous one has proved that the
       remaining attempts would spend the same seconds reaching the same conclusion.
    3. **A residual blank degrades; a broken response fails.** Whatever is still missing at the end
       keeps its source text and the job finishes, with ``on_degraded`` told how many cues that was.
       ``INVALID_TRANSLATION_RESPONSE`` is reserved for genuine protocol corruption — unexpected or
       duplicate ids, unparseable output (:attr:`ValidationResult.is_corrupt`) — and for a batch
       that came back mostly blank (:func:`is_mostly_untranslated`).
    """
    if not batch.segments:
        return {}

    by_id = {int(s["id"]): s for s in batch.segments}

    collected: dict[int, str] = {}
    translatable: list[int] = []

    for item_id, segment in by_id.items():
        text = str(segment.get("text", "") or "")
        if has_translatable_content(text):
            translatable.append(item_id)
        elif text.strip():
            collected[item_id] = text.strip()

    if not translatable:
        _log.debug(
            "batch %d has nothing to translate; %d cue(s) passed through", batch.index, len(collected)
        )
        return collected

    pending = list(translatable)
    last: ValidationResult | None = None
    previously_missing: tuple[int, ...] | None = None

    for attempt in range(1, max(1, max_attempts) + 1):
        if token is not None:
            token.raise_if_cancelled()

        requested = [{"id": i, "text": by_id[i].get("text", "")} for i in pending]

        try:
            returned = translate(requested, batch.context_items, attempt)
        except WorkerError:
            raise
        except errors.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 - engine-specific failures
            raise WorkerError(
                errors.TRANSLATION_FAILED,
                "번역 중 오류가 발생했습니다.",
                detail=repr(exc),
            ) from exc

        result = validate(requested, returned)
        last = result

        collected.update(to_map(returned))
        # Anything that came back for an id we did not ask for in this attempt is discarded by
        # to_map's keying plus this filter, so an engine echoing context lines cannot pollute the
        # result.
        collected = {k: v for k, v in collected.items() if k in by_id}

        pending = [i for i in translatable if i not in collected]

        if not pending:
            if attempt > 1:
                _log.info("batch %d completed on attempt %d", batch.index, attempt)
            return collected

        _log.warning(
            "batch %d attempt %d incomplete: %s", batch.index, attempt, result.describe()
        )
        if on_retry is not None:
            on_retry(attempt, result)

        still_missing = tuple(pending)
        if still_missing == previously_missing:
            _log.warning(
                "batch %d stopping early after attempt %d: the missing ids stopped shrinking (%s)",
                batch.index,
                attempt,
                still_missing,
            )
            break
        previously_missing = still_missing

    return _degrade_or_reject(batch, by_id, translatable, collected, pending, last, on_degraded)


def _degrade_or_reject(
    batch: Batch,
    by_id: Mapping[int, dict[str, Any]],
    translatable: Sequence[int],
    collected: dict[int, str],
    pending: Sequence[int],
    last: ValidationResult | None,
    on_degraded: Callable[[tuple[int, ...]], None] | None,
) -> dict[int, str]:
    """Decide what a batch that never fully translated is worth.

    Either the source text for the stragglers (the batch is returned complete) or nothing at all
    (``INVALID_TRANSLATION_RESPONSE``). Failing the whole job over one stubbornly blank cue used to
    discard minutes of finished GPU work for a line that had no words in it to begin with.
    """
    residual = tuple(sorted(pending))
    detail = last.describe() if last is not None else "no response"

    corrupt = last is not None and last.is_corrupt
    mostly_empty = is_mostly_untranslated(len(residual), len(translatable))

    if corrupt or mostly_empty:
        reason = "corrupt response" if corrupt else "mostly empty response"
        raise WorkerError(
            errors.INVALID_TRANSLATION_RESPONSE,
            f"번역 결과가 올바르지 않습니다({detail}). 잠시 후 다시 시도하세요.",
            recoverable=True,
            detail=(
                f"batch {batch.index}: {len(residual)}/{len(translatable)} ids unusable "
                f"{residual} — {reason}"
            ),
        )

    for item_id in residual:
        source = str(by_id[item_id].get("text", "") or "").strip()
        if source:
            collected[item_id] = source

    _log.warning(
        "batch %d: %d cue(s) could not be translated and keep their source text %s",
        batch.index,
        len(residual),
        residual,
    )

    if on_degraded is not None:
        on_degraded(residual)

    return collected

"""The default translation engine: CTranslate2 + NLLB-200.

Contract (identical to the C# ``ITranslationEngine``): given ``[{"id":.., "text":..}]`` return
``[{"id":.., "translation":..}]`` — one entry per input id, no additions, no omissions, no
duplicates, no blanks. Callers rejoin by id, so order does not matter.

Each cue is translated as its **own sequence**. NLLB is a sentence-level model, so batching means
handing ``translate_batch`` several independent sequences at once — never concatenating cues into
one string, which is the only way an id could ever drift.
"""

from __future__ import annotations

import gc
import re
import sys
from pathlib import Path
from typing import Any, Sequence

from . import errors
from .batching import BatchOptions, split_batches, translate_with_retry
from .cancellation import CancellationToken
from .errors import WorkerError
from .logging_setup import get_logger
from .model_manager import find_local_model

_log = get_logger("mt")

TARGET_LANGUAGE_CODE = "kor_Hang"

#: ISO-639-1 (what Whisper reports) -> NLLB FLORES-200 code.
NLLB_LANGUAGE_CODES: dict[str, str] = {
    "en": "eng_Latn",
    "ja": "jpn_Jpan",
    "zh": "zho_Hans",
    "zh-cn": "zho_Hans",
    "zh-tw": "zho_Hant",
    "yue": "yue_Hant",
    "ko": "kor_Hang",
    "es": "spa_Latn",
    "fr": "fra_Latn",
    "de": "deu_Latn",
    "it": "ita_Latn",
    "pt": "por_Latn",
    "ru": "rus_Cyrl",
    "uk": "ukr_Cyrl",
    "pl": "pol_Latn",
    "nl": "nld_Latn",
    "sv": "swe_Latn",
    "da": "dan_Latn",
    "no": "nob_Latn",
    "nb": "nob_Latn",
    "fi": "fin_Latn",
    "cs": "ces_Latn",
    "sk": "slk_Latn",
    "hu": "hun_Latn",
    "ro": "ron_Latn",
    "bg": "bul_Cyrl",
    "el": "ell_Grek",
    "tr": "tur_Latn",
    "ar": "arb_Arab",
    "he": "heb_Hebr",
    "fa": "pes_Arab",
    "hi": "hin_Deva",
    "bn": "ben_Beng",
    "ta": "tam_Taml",
    "te": "tel_Telu",
    "ur": "urd_Arab",
    "th": "tha_Thai",
    "vi": "vie_Latn",
    "id": "ind_Latn",
    "ms": "zsm_Latn",
    "tl": "tgl_Latn",
    "sw": "swh_Latn",
    "ca": "cat_Latn",
    "hr": "hrv_Latn",
    "sr": "srp_Cyrl",
    "sl": "slv_Latn",
    "lt": "lit_Latn",
    "lv": "lvs_Latn",
    "et": "est_Latn",
    "is": "isl_Latn",
    "mn": "khk_Cyrl",
    "ne": "npi_Deva",
    "si": "sin_Sinh",
    "km": "khm_Khmr",
    "lo": "lao_Laoo",
    "my": "mya_Mymr",
    "af": "afr_Latn",
    "sq": "als_Latn",
    "az": "azj_Latn",
    "be": "bel_Cyrl",
    "eu": "eus_Latn",
    "gl": "glg_Latn",
    "hy": "hye_Armn",
    "ka": "kat_Geor",
    "kk": "kaz_Cyrl",
    "mk": "mkd_Cyrl",
    "mt": "mlt_Latn",
    "uz": "uzn_Latn",
    "am": "amh_Ethi",
    "yo": "yor_Latn",
    "zu": "zul_Latn",
}

#: Used when the detected language has no entry above. English is the least-wrong default: NLLB's
#: English pivot is its strongest direction and most media we see is English.
FALLBACK_LANGUAGE_CODE = "eng_Latn"


def to_nllb_code(language: str | None) -> str:
    """Map a Whisper language tag to an NLLB code, falling back to English."""
    if not language:
        return FALLBACK_LANGUAGE_CODE

    normalised = language.strip().lower().replace("_", "-")
    if normalised in NLLB_LANGUAGE_CODES:
        return NLLB_LANGUAGE_CODES[normalised]

    base = normalised.split("-")[0]
    if base in NLLB_LANGUAGE_CODES:
        return NLLB_LANGUAGE_CODES[base]

    if "_" in language and len(language) == 8:
        # Already an NLLB code (eng_Latn); trust it.
        return language

    _log.warning("no NLLB code for language %r; using %s", language, FALLBACK_LANGUAGE_CODE)
    return FALLBACK_LANGUAGE_CODE


# ---------------------------------------------------------------------------
# style + glossary post-processing
# ---------------------------------------------------------------------------

# NOTE ON STYLE CONTROL: NLLB has no instruction channel, so `polite`/`casual` cannot be requested
# of the model — they are approximated here by rewriting Korean sentence endings after the fact.
# That is genuinely approximate: it fixes the ending of a sentence, not its register throughout.
# The LLM engine (llm_translator.py) honours the style properly through the prompt; the settings
# screen says so. This function is deliberately conservative — a wrong ending is worse than none.

_POLITE_ENDINGS: tuple[tuple[str, str], ...] = (
    ("한다.", "합니다."),
    ("된다.", "됩니다."),
    ("이다.", "입니다."),
    ("있다.", "있습니다."),
    ("없다.", "없습니다."),
    ("같다.", "같습니다."),
    ("한다!", "합니다!"),
    ("했다.", "했습니다."),
    ("됐다.", "됐습니다."),
    ("였다.", "였습니다."),
    ("이었다.", "이었습니다."),
    ("갔다.", "갔습니다."),
    ("왔다.", "왔습니다."),
    ("봤다.", "봤습니다."),
    ("한가?", "한가요?"),
    ("인가?", "인가요?"),
    ("는가?", "는가요?"),
)

#: Verb stems whose 합쇼체 form has an unambiguous 해체 counterpart. Deliberately a short,
#: hand-checked list rather than a general "-습니다 -> -어" rule, which produces broken Korean for
#: most stems ("먹습니다" -> "먹어" is fine, "갑니다" -> "가" is not reachable by suffix surgery).
_CASUAL_ENDINGS: tuple[tuple[str, str], ...] = (
    ("합니다.", "해."),
    ("됩니다.", "돼."),
    ("있습니다.", "있어."),
    ("없습니다.", "없어."),
    ("했습니다.", "했어."),
    ("갔습니다.", "갔어."),
    ("왔습니다.", "왔어."),
    ("봤습니다.", "봤어."),
    ("합니까?", "해?"),
    ("이에요.", "이야."),
    ("예요.", "야."),
)

#: Vowel endings after which a bare "요" is just politeness and can be dropped:
#: "괜찮아요" -> "괜찮아", "해요" -> "해", "그래요" -> "그래".
_DROPPABLE_YO_STEMS = ("아", "어", "여", "해", "래", "게", "네")


def _has_batchim(syllable: str) -> bool:
    """True when a Hangul syllable ends in a final consonant (받침).

    This is what decides 이야 vs 야, 이에요 vs 예요 and so on; getting it wrong produces text a
    Korean reader immediately flags as broken.
    """
    if not syllable:
        return False
    code = ord(syllable[-1])
    if not 0xAC00 <= code <= 0xD7A3:
        # Not a Hangul syllable (a digit, a Latin word, punctuation): assume a final consonant,
        # which is the safer reading for loanwords like "PDF입니다".
        return True
    return (code - 0xAC00) % 28 != 0


def _casual_copula(text: str, tail: str, consonant: str, vowel: str) -> str | None:
    """Rewrite the copula ending ``tail`` (e.g. "입니다.") using the preceding syllable."""
    if not text.endswith(tail):
        return None
    stem = text[: -len(tail)]
    return stem + (consonant if _has_batchim(stem) else vowel)


def _to_casual(text: str) -> str:
    # The copula has to come first: "사실입니다." must become "사실이야.", not "사실야.".
    for tail, consonant, vowel in (
        ("입니다.", "이야.", "야."),
        ("입니까?", "이야?", "야?"),
    ):
        rewritten = _casual_copula(text, tail, consonant, vowel)
        if rewritten is not None:
            return rewritten

    for source, replacement in _CASUAL_ENDINGS:
        if text.endswith(source):
            return text[: -len(source)] + replacement

    # Bare politeness particle: "괜찮아요." -> "괜찮아."
    for punctuation in (".", "?", "!", ""):
        suffix = "요" + punctuation
        if text.endswith(suffix) and len(text) > len(suffix):
            stem = text[: -len(suffix)]
            if stem[-1] in _DROPPABLE_YO_STEMS:
                return stem + punctuation

    return text


def _to_polite(text: str) -> str:
    for source, replacement in _POLITE_ENDINGS:
        if text.endswith(source):
            return text[: -len(source)] + replacement
    return text


def apply_style(text: str, style: str) -> str:
    """Approximate ``polite`` / ``casual`` by normalising the sentence ending.

    ``natural``, ``literal`` and ``preserve`` are returned untouched: for the MT engine those are
    exactly what the model already produces.

    Only the *final* ending is rewritten. Rewriting every clause boundary was tried and produced
    worse Korean than leaving the sentence alone -- a wrong ending mid-sentence is more jarring
    than an inconsistent register.
    """
    if not text or style not in ("polite", "casual"):
        return text

    stripped = text.rstrip()
    trailing = text[len(stripped) :]

    rewritten = _to_polite(stripped) if style == "polite" else _to_casual(stripped)
    return rewritten + trailing


def apply_glossary(text: str, glossary: dict[str, str] | None) -> str:
    """Post-substitute glossary terms.

    Longest key first so "New York City" wins over "New York". Word boundaries are only applied
    to ASCII keys: ``\\b`` does not behave usefully next to Hangul or CJK.
    """
    if not text or not glossary:
        return text

    result = text
    for source in sorted(glossary, key=len, reverse=True):
        target = glossary[source]
        if not source:
            continue

        if source.isascii():
            result = re.sub(
                rf"\b{re.escape(source)}\b", target.replace("\\", "\\\\"), result, flags=re.IGNORECASE
            )
        else:
            result = result.replace(source, target)

    return result


# ---------------------------------------------------------------------------
# engine
# ---------------------------------------------------------------------------


class NllbTranslator:
    """CTranslate2 + NLLB-200 translation engine."""

    def __init__(self, models_dir: str | Path | None = None) -> None:
        self._models_dir = Path(models_dir) if models_dir is not None else None
        self._translator: Any = None
        self._tokenizer: Any = None
        self._key: tuple[str, str, str] | None = None
        self.loaded_model_id: str | None = None
        self.loaded_compute_type: str | None = None

    # -- lifecycle -------------------------------------------------------------

    def load(
        self,
        *,
        model_id: str = "nllb-200-distilled-600M",
        device: str = "auto",
        compute_type: str | None = None,
    ) -> None:
        resolved_device = _resolve_device(device)
        resolved_compute = compute_type or ("float16" if resolved_device == "cuda" else "int8")
        key = (model_id, resolved_device, resolved_compute)

        if self._translator is not None and self._key == key:
            return

        if self._translator is not None:
            self.unload()

        directory = find_local_model(model_id, self._models_dir)
        if directory is None:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                f"번역 모델을 찾을 수 없습니다: {model_id}. 모델 화면에서 먼저 내려받으세요.",
                detail=f"no local NLLB directory for {model_id}",
            )

        try:
            import ctranslate2  # noqa: PLC0415 - lazy
        except ImportError as exc:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "번역 구성 요소(CTranslate2)를 불러오지 못했습니다. 설치가 손상되었을 수 있습니다.",
                detail=repr(exc),
            ) from exc

        _log.info("loading NLLB from %s (device=%s, compute=%s)", directory, resolved_device, resolved_compute)

        saved_stdout = sys.stdout
        sys.stdout = sys.stderr
        try:
            self._translator = ctranslate2.Translator(
                str(directory), device=resolved_device, compute_type=resolved_compute
            )
            self._tokenizer = self._load_tokenizer(directory)
        except WorkerError:
            raise
        except Exception as exc:  # noqa: BLE001
            # noqa: PLC0415 - imported here to avoid a module-level cycle with transcriber.
            from .transcriber import cuda_library_error, is_cuda_oom  # noqa: PLC0415

            if is_cuda_oom(exc):
                raise WorkerError(
                    errors.CUDA_OUT_OF_MEMORY,
                    "GPU 메모리가 부족하여 번역 모델을 불러오지 못했습니다.",
                    recoverable=True,
                    detail=repr(exc),
                ) from exc
            library_error = cuda_library_error(exc)
            if library_error is not None:
                raise library_error from exc
            raise WorkerError(
                errors.TRANSLATION_FAILED,
                f"번역 모델을 불러오지 못했습니다: {model_id}",
                detail=repr(exc),
            ) from exc
        finally:
            sys.stdout = saved_stdout

        self._key = key
        self.loaded_model_id = model_id
        self.loaded_compute_type = resolved_compute

    @staticmethod
    def _load_tokenizer(directory: Path) -> Any:
        """HF ``AutoTokenizer`` for the NLLB sentencepiece vocabulary.

        The CTranslate2 conversion keeps the tokenizer files alongside the weights, so this is a
        purely local load; ``local_files_only`` makes that explicit rather than accidental.
        """
        try:
            from transformers import AutoTokenizer  # noqa: PLC0415 - lazy
        except ImportError as exc:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "번역 토크나이저 구성 요소(transformers)를 불러오지 못했습니다.",
                detail=repr(exc),
            ) from exc

        try:
            return AutoTokenizer.from_pretrained(str(directory), local_files_only=True)
        except Exception as exc:  # noqa: BLE001
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "번역 모델의 토크나이저 파일을 찾을 수 없습니다. 모델을 다시 내려받으세요.",
                detail=repr(exc),
            ) from exc

    def unload(self) -> None:
        if self._translator is None and self._tokenizer is None:
            return

        _log.info("unloading NLLB model %s", self.loaded_model_id)
        self._translator = None
        self._tokenizer = None
        self._key = None
        self.loaded_model_id = None
        self.loaded_compute_type = None

        gc.collect()

        torch = sys.modules.get("torch")
        if torch is not None:
            try:
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception as exc:  # noqa: BLE001
                _log.debug("torch.cuda cleanup failed: %r", exc)

    # -- translation -----------------------------------------------------------

    def translate_items(
        self,
        items: Sequence[dict[str, Any]],
        *,
        source_language: str,
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        beam_size: int = 4,
        max_batch_size: int = 16,
        token: CancellationToken | None = None,
    ) -> list[dict[str, Any]]:
        """Translate one list of ``{id, text}`` into ``{id, translation}``.

        Every cue is its own sequence in the ``translate_batch`` call, and results are zipped back
        onto the input ids positionally — CTranslate2 preserves order, and the surrounding
        validator catches it if that ever stops being true.
        """
        if not items:
            return []

        if self._translator is None or self._tokenizer is None:
            raise WorkerError(
                errors.TRANSLATION_MODEL_NOT_FOUND,
                "번역 모델이 준비되지 않았습니다.",
                detail="translate_items called before load()",
            )

        if token is not None:
            token.raise_if_cancelled()

        # NLLB reads the source language from the *first token of the sequence*, and the only thing
        # that emits that token is the tokenizer's `src_lang`. Every CT2 conversion we ship leaves
        # `src_lang` null in tokenizer_config.json, so NllbTokenizer falls back to its own default,
        # eng_Latn. Until this assignment existed the code computed the right FLORES code and then
        # threw it away, handing the model every Japanese cue labelled English — the "wrong
        # source-language code" failure that batching.MOSTLY_UNTRANSLATED_RATIO is written around.
        self._set_source_language(to_nllb_code(source_language))

        texts = [str(item.get("text", "") or "") for item in items]

        tokenized: list[list[str]] = []
        for text in texts:
            if not text.strip():
                tokenized.append([])
                continue
            tokenized.append(
                self._tokenizer.convert_ids_to_tokens(self._tokenizer.encode(text))
            )

        # Empty cues never reach the model: NLLB happily hallucinates a sentence from nothing.
        indices = [i for i, tokens in enumerate(tokenized) if tokens]
        results: list[str] = ["" for _ in texts]

        if indices:
            try:
                # `translate_batch` has no source-language parameter: for an NMT model CTranslate2
                # takes the language purely from the tokens it is given, which is why _set_source_-
                # language above is the whole mechanism.
                outputs = self._translator.translate_batch(
                    [tokenized[i] for i in indices],
                    target_prefix=[[TARGET_LANGUAGE_CODE] for _ in indices],
                    beam_size=max(1, int(beam_size)),
                    max_batch_size=max(1, int(max_batch_size)),
                )
            except Exception as exc:  # noqa: BLE001
                from .transcriber import cuda_library_error, is_cuda_oom  # noqa: PLC0415

                if is_cuda_oom(exc):
                    raise WorkerError(
                        errors.CUDA_OUT_OF_MEMORY,
                        "GPU 메모리가 부족하여 번역에 실패했습니다.",
                        recoverable=True,
                        detail=repr(exc),
                    ) from exc
                library_error = cuda_library_error(exc)
                if library_error is not None:
                    raise library_error from exc
                raise WorkerError(
                    errors.TRANSLATION_FAILED, "번역에 실패했습니다.", detail=repr(exc)
                ) from exc

            for position, result in zip(indices, outputs):
                results[position] = self._decode(result)

        output: list[dict[str, Any]] = []
        for item, translated in zip(items, results):
            text = apply_glossary(apply_style(translated.strip(), style), glossary)
            output.append({"id": int(item["id"]), "translation": text})

        return output

    def _set_source_language(self, source_code: str) -> None:
        """Point the tokenizer at ``source_code`` before encoding.

        Set on every call, not once at load: one loaded engine serves a whole queue, and a folder
        of mixed-language video would otherwise translate every file as the language of the first.
        """
        if getattr(self._tokenizer, "src_lang", None) == source_code:
            return

        try:
            self._tokenizer.src_lang = source_code
        except Exception as exc:  # noqa: BLE001 - an odd tokenizer must not fail the job outright
            _log.warning(
                "could not set the tokenizer source language to %s; translations will be "
                "decoded as %r: %r",
                source_code,
                getattr(self._tokenizer, "src_lang", "unknown"),
                exc,
            )
            return

        _log.info("tokenizer source language set to %s", source_code)

    def _decode(self, result: Any) -> str:
        hypotheses = getattr(result, "hypotheses", None) or []
        if not hypotheses:
            return ""

        tokens = list(hypotheses[0])
        # The forced target-language token is an artefact of target_prefix, not part of the text.
        if tokens and tokens[0] == TARGET_LANGUAGE_CODE:
            tokens = tokens[1:]

        try:
            return self._tokenizer.decode(
                self._tokenizer.convert_tokens_to_ids(tokens), skip_special_tokens=True
            )
        except Exception as exc:  # noqa: BLE001 - a decode failure must not kill the batch
            _log.warning("could not decode a hypothesis: %r", exc)
            return ""

    # -- batch orchestration ---------------------------------------------------

    def translate_segments(
        self,
        segments: Sequence[dict[str, Any]],
        *,
        source_language: str,
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        options: BatchOptions | None = None,
        token: CancellationToken | None = None,
        on_batch_done: Any = None,
    ) -> dict[int, str]:
        """Translate a whole transcript, batch by batch, with id validation and retries."""
        batches = split_batches(segments, options)
        translations: dict[int, str] = {}

        for batch in batches:
            if token is not None:
                token.raise_if_cancelled()

            def run(
                items: list[dict[str, Any]],
                _context: list[dict[str, Any]],
                _attempt: int,
            ) -> list[dict[str, Any]]:
                # NLLB has no context channel, so `_context` is unused here by design: the LLM
                # engine is the one that can act on preceding lines.
                return self.translate_items(
                    items,
                    source_language=source_language,
                    style=style,
                    glossary=glossary,
                    token=token,
                )

            translations.update(translate_with_retry(batch, run, token=token))

            if on_batch_done is not None:
                on_batch_done(batch, translations)

        return translations


def _resolve_device(device: str) -> str:
    if device and device != "auto":
        return device
    try:
        import ctranslate2  # noqa: PLC0415

        return "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
    except ImportError:
        return "cpu"
    except Exception as exc:  # noqa: BLE001
        _log.warning("CUDA probe failed; using CPU: %r", exc)
        return "cpu"


class FakeTranslator:
    """Deterministic engine for the "Fake AI" diagnostic mode and for tests.

    Produces an obviously-fake but *complete* result: every id in, every id out. That is what
    makes it useful for exercising the pipeline without a model.
    """

    def translate_items(
        self,
        items: Sequence[dict[str, Any]],
        *,
        source_language: str = "en",
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        token: CancellationToken | None = None,
        **_: Any,
    ) -> list[dict[str, Any]]:
        output: list[dict[str, Any]] = []
        for item in items:
            if token is not None:
                token.raise_if_cancelled()
            text = str(item.get("text", "") or "").strip() or "(무음)"
            translated = apply_glossary(apply_style(f"[번역] {text}", style), glossary)
            output.append({"id": int(item["id"]), "translation": translated})
        return output

    def translate_segments(
        self,
        segments: Sequence[dict[str, Any]],
        *,
        source_language: str = "en",
        style: str = "natural",
        glossary: dict[str, str] | None = None,
        options: BatchOptions | None = None,
        token: CancellationToken | None = None,
        on_batch_done: Any = None,
    ) -> dict[int, str]:
        translations: dict[int, str] = {}
        for batch in split_batches(segments, options):
            def run(
                items: list[dict[str, Any]],
                _context: list[dict[str, Any]],
                _attempt: int,
            ) -> list[dict[str, Any]]:
                return self.translate_items(
                    items,
                    source_language=source_language,
                    style=style,
                    glossary=glossary,
                    token=token,
                )

            translations.update(translate_with_retry(batch, run, token=token))
            if on_batch_done is not None:
                on_batch_done(batch, translations)
        return translations

    def unload(self) -> None:
        return None

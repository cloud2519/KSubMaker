# ksubmaker-worker

The Python AI worker for **KSubMaker**: video → faster-whisper transcript → Korean translation →
Korean SRT.

It is a long-lived child process of the C# host and speaks a JSON-lines protocol over stdio.
The wire contract is owned by the C# side — `src/KSubMaker.WorkerProtocol/` and
`src/KSubMaker.Domain/Errors/ErrorCodes.cs` are authoritative; `protocol.py` and `errors.py`
mirror them.

## The one rule

**stdout carries protocol JSON and nothing else.** One compact JSON object per line,
`ensure_ascii=False`, flushed immediately. Everything else — logs, tracebacks, library chatter —
goes to stderr. `protocol.install_stdout_guard()` runs before any heavy import and points
`sys.stdout` at stderr, so a stray `print` inside a model loader corrupts the log, never the
channel.

## Running it

```bash
# from the repository root
python -m pip install -e "worker[dev]"
python -m ksubmaker_worker
```

Without installing the package:

```bash
PYTHONPATH=worker python3 -m ksubmaker_worker
```

Smoke test:

```bash
printf '{"command":"hello","requestId":"r1","protocolVersion":"1.2"}\n{"command":"shutdown","requestId":"r2"}\n' \
  | PYTHONPATH=worker python3 -m ksubmaker_worker
```

It should print a `ready` line, an `ack`, a `goodbye`, and exit 0.

### Environment

| Variable | Purpose |
| --- | --- |
| `KSUBMAKER_MODELS_DIR` | Root of the models tree. Set by the host from `IAppPaths.ModelsDirectory`, so relocating the model folder in 설정 → 경로 follows through to here. Read in `__init__`, before any job. |
| `KSUBMAKER_TOOLS_DIR` | Where `ffmpeg/bin/` and `llama/` live. Set by the host; overrides discovery. |
| `HF_HOME` | Hugging Face cache root. The host points it inside the models tree so a hub fallback download does not land in the user profile. |
| `KSUBMAKER_WORKER_LOG_LEVEL` | `DEBUG` / `INFO` / `WARNING` / … (default `INFO`). |
| `HF_TOKEN` | Optional Hugging Face token for gated model downloads. |

The host's values win over anything already in the environment: the settings screen is the single
source of truth for these paths. When running the worker by hand, set them yourself.

ffmpeg and ffprobe are looked for under `tools/ffmpeg/bin`, then `tools/`, then the app directory
(frozen builds only), and **only then** on PATH. A PATH hit is logged as a warning: it means the
bundle is broken and we are running against an ffmpeg build nobody tested.

## Commands and events

Host → worker: `hello`, `detectHardware`, `probe`, `process`, `cancel`, `listModels`,
`downloadModel`, `cancelDownload`, `verifyModel`, `deleteModel`, `shutdown`.

Worker → host: `ready`, `ack`, `started`, `progress`, `languageDetected`, `stageCompleted`,
`completed`, `error`, `cancelled`, `log`, `hardware`, `probeResult`, `modelList`,
`downloadProgress`, `downloadCompleted`, `goodbye`.

stdin is read on the main thread; `process` / `downloadModel` / `verifyModel` run on a single
background thread, so `cancel` and `shutdown` are handled while a job is in flight. Only one job
runs at a time — two concurrent CUDA jobs would fight over the same VRAM and both would fail.

## Pipeline

```
probing(.02) → extractingAudio(.08) → transcribing(.55) → translating(.32) → writingSubtitle(.03)
```

Stage weights match `KSubMaker.Domain.Jobs.ProgressCalculator`, so the host's progress bar never
jumps when it recomputes overall progress locally.

`ProcessCommand.phase` selects how much of it runs:

* `full` — everything.
* `transcribe` — stop after ASR and checkpoint (strategy B's first pass).
* `translate` — resume from the transcription checkpoint and finish.

`sourceMode` is `audio` (ASR) or `embeddedSubtitle` (extract an existing subtitle track and
translate that instead).

### Checkpoints

Under the job's `checkpointDir`, all written temp-then-`os.replace`:

* `job.json` — stage reached plus the source file's size/mtime fingerprint
* `transcription.json` — the ASR result
* `translation.partial.json` — `{segment id: Korean text}`, saved as batches land
* `finalization.json` — what was written, and where

Resume rules: a transcription present skips ASR; partial translations mean only the missing ids
are translated. A changed source size or mtime invalidates everything, because a re-encode with
the same name has completely different timecodes.

### CUDA out-of-memory recovery

On `CUDA_OUT_OF_MEMORY` the worker tries, in this order, before failing:

1. unload the model, `gc.collect()`, `torch.cuda.empty_cache()`
2. halve the batch — a *split*, never a truncation, so no cue is lost
3. downgrade the compute type: `float16 → int8_float16 → int8`
4. emit a `log` advising a smaller model
5. retry **once**

A second OOM fails the job with `recoverable: true` and a Korean message naming the two settings
worth changing.

## Translation engines

| `translationEngine` | Engine | Notes |
| --- | --- | --- |
| `local-translation` (default) | CTranslate2 + NLLB-200 | Fast, deterministic. Each cue is its own sequence, so ids cannot drift. |
| `local-llm` | bundled `llama-server` | Better style control. No Ollama; the worker spawns the server itself on 127.0.0.1 and an ephemeral port. |
| `fake` | in-process stub | Diagnostic mode; complete and deterministic output with no model. |

Both engines go through the same batching, id validation and retry loop: batches close at 30
items / 2500 chars / 180 s of media (whichever comes first), responses are checked for missing,
duplicate, unexpected and blank ids, and **only the still-missing ids** are retried, up to three
attempts before `INVALID_TRANSLATION_RESPONSE`.

Style control differs by engine and this is deliberate: the LLM honours `translationStyle` through
the prompt, while the MT engine can only approximate `polite`/`casual` by normalising the final
sentence ending after the fact. That limitation is documented in `translator.py` next to the code
that implements it.

## Tests

```bash
python3 -m pytest worker/tests -q
# or, without installing the package:
PYTHONPATH=worker python3 -m pytest worker/tests -q
```

No network, no models, no GPU. ffmpeg tests use the real binary when one is on PATH (a synthetic
three-second clip) and skip otherwise; everything else uses fakes. Coverage:

```bash
python3 -m pytest worker/tests --cov=ksubmaker_worker --cov-report=term-missing
```

## Dependencies

`faster-whisper`, `ctranslate2`, `transformers`, `sentencepiece`, `huggingface-hub`, `requests`.

All of them are imported **lazily, inside the functions that need them**, so the module tree
imports and the whole test suite runs on a machine with none of them installed.

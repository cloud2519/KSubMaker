# AGENTS.md — 저장소 규칙

사람이든 코딩 에이전트든, 이 저장소에 코드를 쓰기 전에 읽어야 하는 문서입니다.
**규칙은 취향이 아니라 이미 겪은 실패에서 나왔습니다.** 각 항목에 이유를 함께 적었습니다.

---

## 0. 이 프로그램이 하는 일

폴더 하나를 스캔해서 그 안의 영상들에 한국어 자막(`*.ko.srt`)을 만듭니다. 음성 인식은
faster-whisper, 번역은 CTranslate2 + NLLB-200(기본) 또는 로컬 LLM. 대상 사용자는 개발자가
아니고, **모든 처리는 사용자의 기기 안에서만** 일어납니다.

구조 전반은 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), 결정 근거는
[`docs/DECISIONS.md`](docs/DECISIONS.md)를 보세요.

---

## 1. 계층 규칙

```
Domain ◀── Application ◀── Infrastructure ◀┐
   ▲            ▲                          │
   │            │                          │
WorkerProtocol ─┘          Worker ─────────┤
                              ▲            │
                              └──── App ───┘
```

허용된 참조 방향은 이것뿐입니다. **반대 방향은 금지입니다.**

| 프로젝트 | 참조해도 되는 것 | 절대 참조하면 안 되는 것 |
| --- | --- | --- |
| `KSubMaker.Domain` | (없음) | 전부. **NuGet 패키지도 0개를 유지하세요.** |
| `KSubMaker.WorkerProtocol` | (없음) | 전부 |
| `KSubMaker.Application` | Domain, WorkerProtocol | Infrastructure, Worker, App |
| `KSubMaker.Infrastructure` | Domain, Application, WorkerProtocol | Worker, App |
| `KSubMaker.Worker` | Domain, Application, WorkerProtocol | Infrastructure, App |
| `KSubMaker.App` | 전부 | — |

**왜.** `Domain`이 순수하기 때문에 상태 기계와 자막 규칙을 파일 시스템 없이 테스트할 수
있습니다. `Application`이 구현체를 모르기 때문에 Fake AI 파이프라인이 성립합니다.
`Infrastructure`와 `Worker`가 서로를 모르기 때문에 둘 다 `net10.0`으로 남아 Linux CI에서
빌드됩니다.

**부작용은 인터페이스 뒤로.** 파일, 프로세스, 네트워크, 레지스트리, 시계를 만지는 것은
`Application/Abstractions`의 인터페이스로 표현하고 `Infrastructure`에서 구현하세요.
시간이 필요하면 `TimeProvider`를 주입받으세요(`Job.TransitionTo`가 그렇게 합니다).

**TFM을 올리지 마세요.** `Infrastructure`와 `Worker`는 Windows 전용 코드를 갖고 있지만
**의도적으로** `net10.0`입니다. 모든 Windows 호출은 `OperatingSystem.IsWindows()` 가드 안에
있어야 합니다. `net10.0-windows`로 바꾸면 Linux CI 빌드가 통째로 깨집니다.

---

## 2. C# 규칙

### 필수

| 규칙 | 이유 |
| --- | --- |
| **nullable 참조 형식을 켠 채로 두세요.** `CS8600`, `CS8602`, `CS8603`, `CS8618`, `CS4014`는 **오류**입니다(`Directory.Build.props`). | `!` 남발로 경고를 지우지 마세요. null이 올 수 있으면 그렇게 모델링하세요. |
| **async는 끝까지.** `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` 금지. | WPF 디스패처에서 이것들은 데드락입니다. 유일한 예외는 `App.OnExit`인데, 그 시점에는 디스패처 루프가 이미 끝났고 코드에 그 이유가 주석으로 적혀 있습니다. |
| **`CancellationToken`을 끝까지 넘기세요.** 비동기 메서드는 토큰을 받고, 호출하는 모든 비동기 메서드에 전달해야 합니다. | 사용자가 "중지"를 눌렀을 때 실제로 멈춰야 합니다. 토큰을 삼키는 계층 하나가 전체 취소를 무력화합니다. |
| **`ConfigureAwait(false)`** — 비-UI 라이브러리 코드(Domain/Application/Infrastructure/Worker)에서. | UI 컨텍스트로 불필요하게 돌아가지 않게 합니다. `App` 프로젝트에서는 필요 없습니다. |
| **프로세스 실행은 `ArgumentList`로.** 문자열 연결로 명령줄을 만들지 마세요. | `C:\Program Files\...`의 공백에서 깨집니다. 인용 처리는 `ArgumentList`가 정확히 해 줍니다. |
| **경로를 하드코딩하지 마세요.** 모든 쓰기 경로는 `IAppPaths`에서 나와야 합니다. | 캐시·모델·로그 폴더를 설정에서 옮길 수 있는 것이 그 덕분입니다. |
| **사용자에게 보이는 문자열은 리소스로.** `KSubMaker.App`의 `Resources/Strings.resx`. | 화면 문자열이 코드에 흩어지면 일관성이 깨집니다. |
| **파일 I/O는 원자적으로.** 임시 파일에 쓰고 → 이동. | 정전이 나도 온전한 파일 아니면 이전 파일이지, 잘린 파일은 없습니다. |
| **예외는 좁게 잡으세요.** `catch (Exception)`은 필터(`when`)가 있거나, 스레드·이벤트 핸들러 경계처럼 이유가 분명한 최상위 경계일 때만. | 그 경계들은 코드에 왜 그런지 주석이 달려 있습니다. |
| **모델 이름을 코드에 박지 마세요.** `ModelIds` 상수와 `ModelCatalog`만 씁니다. | 모델 추가는 데이터 변경이지 파이프라인 코드 변경이 아닙니다. |

### 하지 말아야 할 것

* `Thread.Sleep` (테스트에서도). 시간이 필요하면 `Task.Delay`와 토큰을.
* `async void` — 프레임워크 오버라이드와 이벤트 핸들러 제외. 그 경우에도 본문 전체를
  try/catch로 감싸세요.
* `DateTime.Now` — `DateTime.UtcNow` 또는 주입된 `TimeProvider`를.
* 정적 가변 상태.
* UI에 스택 트레이스 노출. `UserFacingErrors.Describe`로 한국어 한 문장을 보여 주고, 기술적
  세부는 로그로만.
* `DbContext`를 오래 붙들기. 항상 `IDbContextFactory`로 짧게 쓰고 버리세요.

### 서식

`.editorconfig`가 없으므로 **주변 코드의 스타일을 따르세요.** 실제로 쓰이고 있는 형태:
파일 범위 네임스페이스, 최상위 `using`, 표현식 본문 멤버(짧을 때), 기본 생성자
(`class Foo(IBar bar)`), 컬렉션 식(`[]`), `sealed`가 기본, XML 문서 주석은 **왜**를 설명할 때.

---

## 3. Python 규칙 (`worker/`)

| 규칙 | 이유 |
| --- | --- |
| **타입 힌트를 붙이세요.** 모든 함수에. `from __future__ import annotations`가 모든 모듈 맨 위에 있습니다. | |
| **`shell=True` 금지.** `subprocess`는 언제나 argv 리스트로. | 파일명에 들어간 `&`나 `"` 하나가 임의 명령 실행이 됩니다. |
| **맨 `except:` 금지.** 구체적인 예외를 잡으세요. `except Exception`이 필요한 최상위 경계(작업 스레드, 명령 디스패치)에는 `# noqa: BLE001`과 이유 주석이 붙어 있습니다. | |
| **무거운 import는 지연시키세요.** `faster_whisper`, `ctranslate2`, `transformers`, `requests`는 **그것을 쓰는 함수 안에서** import합니다(`# noqa: PLC0415`). | 그래야 패키지 트리와 670개 테스트가 아무것도 설치되지 않은 기계에서 돕니다. |
| **stdout에 쓰지 마세요.** [§4](#4-stdout은-프로토콜-전용입니다) | |
| **사용자에게 보이는 메시지는 한국어, 기술적 세부는 영어.** `WorkerError(code, message, detail=...)`에서 `message`는 한국어이고 UI로 갑니다. `detail`은 로그로만 갑니다. | |
| **취소 지점을 넣으세요.** 오래 도는 루프에는 `token.raise_if_cancelled()`를. | 사용자가 "중지"를 눌렀을 때 실제로 멈춰야 합니다. |
| **자식 프로세스는 등록하세요.** `GLOBAL_PROCESSES.add(p)`와 `token.register_process(p)`. | 취소나 종료 시 함께 죽어야 합니다. |
| **원자적 쓰기.** 임시 파일 → `os.replace`. | |

### 오류 코드

`worker/ksubmaker_worker/errors.py`는 `src/KSubMaker.Domain/Errors/ErrorCodes.cs`의 **거울**입니다.
문자열 값이 정확히 같아야 하고 순서도 맞춰져 있습니다. **한쪽만 바꾸지 마세요.**

---

## 4. stdout은 프로토콜 전용입니다

이것은 협상 대상이 아닙니다.

* **stdout** — JSON Lines 프로토콜만. 한 줄에 컴팩트 JSON 객체 하나, 즉시 플러시.
* **stderr** — 로그, 트레이스백, 라이브러리 잡음, 진행 막대.

워커는 무거운 import를 하기 **전에** `protocol.install_stdout_guard()`를 호출해 진짜 stdout을
붙잡고 `sys.stdout`을 stderr로 바꿔치기합니다. 그래서 모델 로더 안의 `print` 한 줄이
채널을 깨는 대신 로그를 더럽힙니다. **이 가드를 제거하거나 우회하지 마세요.**

워커에서 무언가를 출력해야 한다면:

```python
protocol.emit_log("사용자에게 보일 한국어 메시지", "info", request_id=..., job_id=...)
# 또는 순수 진단용
_log.debug("technical detail")   # logging_setup.get_logger() → stderr
```

---

## 5. 자막 데이터는 기기 밖으로 나가지 않습니다

**영상, 오디오, 인식 결과, 번역 결과, 파일 경로를 원격지로 보내는 코드를 추가하지 마세요.**

* 클라우드 번역 API 금지.
* 텔레메트리, 사용 통계, 크래시 리포트 전송 금지.
* 예외 하나: **`https://huggingface.co`에서 모델 파일을 받는 것.** 코드는 HTTPS가 아닌 URL을
  거부합니다(`_require_https` / `BuildUri`).
* 로컬 LLM 서버는 `127.0.0.1`에만 바인딩합니다. `0.0.0.0`으로 바꾸지 마세요.

이 규칙은 README에 사용자와 한 약속입니다: "모든 처리는 로컬에서 이루어지며 모델 다운로드
외에는 인터넷 연결이 필요 없습니다."

---

## 6. 프로토콜 변경 절차

와이어 계약의 정본은 **`src/KSubMaker.WorkerProtocol/`** 입니다.
`worker/ksubmaker_worker/protocol.py`는 그 거울입니다.

프로토콜을 바꿀 때는 순서대로:

1. **`src/KSubMaker.WorkerProtocol/`을 먼저 고칩니다.**
2. **버전을 올립니다.** `ProtocolConstants.Version`.
   - 필드 삭제·이름 변경·타입 변경·의미 변경·필수로 승격 → **MAJOR**
   - 선택 필드 추가, 새 이벤트, 새 명령 → **MINOR**
3. **`worker/ksubmaker_worker/protocol.py`의 `PROTOCOL_VERSION`과 상수·이미터를 맞춥니다.**
4. **새 이벤트라면 `WorkerProtocolSerializer.ResolveEventType`에, 새 명령이라면
   `ResolveCommandType`에 항목을 추가합니다.** 빠뜨리면 `UnknownEvent`가 되어 조용히 무시됩니다.
5. **[`docs/WORKER_PROTOCOL.md`](docs/WORKER_PROTOCOL.md)를 갱신합니다.** 필드표와 예시 JSON까지.
6. **왕복 테스트를 추가합니다.** C#에서 직렬화한 것이 Python에서 파싱되고 그 반대도 되는지.
   Python 쪽 참고: `worker/tests/test_protocol.py`.

단계 가중치(`ProgressCalculator.Weights` ↔ `protocol.STAGE_WEIGHTS`)는 **반드시 양쪽을 동시에**
바꿔야 합니다. 한쪽만 바꾸면 진행률 막대가 튑니다.

---

## 7. 테스트

### 명령

```powershell
# .NET
dotnet restore
dotnet build KSubMaker.sln -c Release
dotnet test  KSubMaker.sln -c Release

# Python
python -m pip install -e "worker[dev]"
python -m pytest worker/tests

# 둘 다
.\scripts\run-tests.ps1
```

패키지를 설치하지 않고 파이썬 테스트만 돌리려면:

```bash
PYTHONPATH=worker python3 -m pytest worker/tests -q
```

### 현재 상태

* **파이썬:** `worker/tests` 17개 모듈, 670개 테스트. GPU·모델·네트워크 불필요.
* **.NET:** `tests/KSubMaker.UnitTests`(1,459건)와 `tests/KSubMaker.IntegrationTests`(140건).
  둘 다 솔루션에 등록되어 있습니다.

**"전부 통과"와 "전부 실행"은 다릅니다.** FFmpeg나 Python이 필요한 통합 테스트는 그 도구가
없으면 실패가 아니라 건너뜁니다(`ExternalTools.FfmpegSkipReason` / `PythonSkipReason`).
CI 로그에서 **skipped 개수를 확인하세요.** 조용히 0개만 돌고 초록불이 뜨는 것이 가장 나쁜
경우입니다.

**실제 GPU 경로는 어떤 자동 스위트로도 검증되지 않습니다.** 그것이 `scripts/smoke-gpu.ps1`이
따로 존재하는 이유이며, 릴리스 전에 사람이 직접 한 번 돌려야 합니다.

### 규칙: 핵심 상태 전이를 바꾸면 테스트가 필요합니다

아래 중 하나를 건드리는 변경은 **그 동작을 고정하는 테스트 없이 머지하지 마세요.**

| 대상 | 왜 |
| --- | --- |
| `JobStateMachine` 전이 표 | 잘못된 전이는 큐와 데이터베이스를 불일치시킵니다 |
| `Job.TransitionTo` / `MarkFailed` / `EnterStage` | 같은 이유 |
| `ProgressCalculator` 가중치·산식 | 파이썬 쪽과 어긋나면 진행률이 튑니다 |
| `TranslationValidator` / `batching.validate` | **자막이 조용히 사라지는 것**을 막는 유일한 방어선입니다 |
| `TranslationBatcher` / `split_batches` | |
| `SrtFormatter` / `subtitle_writer` | 두 구현이 바이트 단위로 같아야 합니다 |
| `OutputPathResolver` | 사용자 파일을 덮어쓸지 말지를 결정합니다 |
| `HardwareRecommendationPolicy` | 순수 함수라 테스트 비용이 거의 없습니다 |
| `ErrorCodes` ↔ `errors.py` | 패리티가 깨지면 오류가 UI에서 "알 수 없는 오류"가 됩니다 |
| CUDA OOM 사다리 (`_with_oom_recovery`) | 배치 분할이 **잘라내기**로 바뀌면 자막이 사라집니다 |
| 체크포인트 이어하기 규칙 | |
| 프로토콜 직렬화 | [§6](#6-프로토콜-변경-절차) |

.NET 테스트 프로젝트를 처음 추가하는 사람은 위 목록 위에서부터 만드세요.

### 테스트 원칙

* **네트워크 금지, GPU 금지, 모델 금지.** 테스트는 아무것도 없는 CI 컨테이너에서 돌아야 합니다.
* ffmpeg 테스트는 실제 바이너리가 PATH에 있으면 3초짜리 합성 클립으로 돌고, 없으면 건너뜁니다.
* 실패를 재현하는 테스트를 **먼저** 쓰고 고치세요.

---

## 8. 커밋하지 말아야 할 것

`.gitignore`가 대부분 막고 있지만, 규칙으로도 적어 둡니다.

**절대 커밋 금지:**

* `tools/ffmpeg/`, `tools/python/`, `tools/llama/` — 내려받는 바이너리
* `models/`, `*.gguf`, `*.bin`, `*.safetensors`, `*.pt`, `*.onnx` — 모델 가중치
* `artifacts/`, `publish/`, `installer/Output/` — 빌드 산출물
* `*.db`, `*.db-shm`, `*.db-wal`, `logs/`, `*.log` — 로컬 상태
* `bin/`, `obj/`, `__pycache__/`, `*.egg-info/`, `.pytest_cache/`, `.venv/`
* API 키, 토큰, 실제 사용자 파일 경로가 담긴 로그
* 테스트용 영상 파일 — 필요하면 `ffmpeg`로 합성 클립을 만드는 코드를 커밋하세요

**커밋해야 하는 것:**

* `src/KSubMaker.App/Resources/Strings.Designer.cs` — 빌드 에이전트에 Visual Studio가 없어
  단일 파일 생성기를 돌릴 수 없으므로 손으로 관리하며 체크인합니다.
* EF Core 마이그레이션 (`src/KSubMaker.Infrastructure/Persistence/Migrations/`).

---

## 9. 스크립트와 도구 경로

`scripts/*.ps1`이 만드는 경로는 `ToolLocator`(C#)와 `ffmpeg_service` / `llm_translator`(Python)의
탐색 순서와 **정확히** 일치해야 합니다.

| 스크립트 | 출력 위치 | 찾는 코드 |
| --- | --- | --- |
| `fetch-ffmpeg.ps1` | `tools/ffmpeg/bin/{ffmpeg,ffprobe}.exe` + DLL | `ToolLocator.Probe`, `ffmpeg_service.find_binary` |
| `build-worker.ps1` | `tools/python/python.exe` | `ToolLocator.ResolveWorkerCore` |
| `fetch-llama.ps1` | `tools/llama/llama-server.exe` | `llm_translator.find_llama_server` |

`IAppPaths.ToolsDirectory`는 `<앱 실행 파일 폴더>/tools`입니다. 사용자가 옮길 수 없습니다.

**PATH는 언제나 마지막 수단입니다.** 세 탐색기 모두 번들 → 앱 폴더 → PATH 순서이고, PATH에서
찾으면 경고 로그를 남깁니다. 프로덕션 로그에 그 경고가 보이면 배포가 깨진 것입니다.
이 순서를 바꾸지 마세요 — 사용자의 임의 FFmpeg 빌드를 쓰면 재현할 수 없는 실패가 납니다.

PowerShell 스크립트 규칙:

* **`.ps1`과 `.iss`는 반드시 UTF-8 BOM으로 저장하세요.** 타협 불가입니다. Windows PowerShell
  5.1은 BOM이 없으면 파일을 시스템 ANSI 코드 페이지(한국어 환경은 CP949)로 읽습니다. CP949는
  2바이트 인코딩이라 0x81–0xFE 선행 바이트가 **다음 한 바이트를 무조건 삼킵니다**. UTF-8 한글은
  3바이트이므로 한글이 이어지는 구간에서 정렬이 한 칸씩 밀리고, 결국 뒤따르는 ASCII 문자 —
  문자열 리터럴의 닫는 따옴표까지 — 를 먹어버립니다. 그러면 스크립트가 **실행이 아니라 파싱에서**
  실패하고, 오류는 진짜 원인과 한참 떨어진 줄을 가리킵니다. Inno Setup 6도 같은 규칙입니다.
  `pwsh`(PowerShell 7)는 BOM 없는 UTF-8을 잘 읽으므로 빌드 서버에서 파싱 검사를 해도 이 문제는
  잡히지 않습니다 — `ScriptEncodingTests`가 바이트를 직접 검사하는 이유입니다.
  새 스크립트를 추가하면 `tests/KSubMaker.UnitTests`의 glob이 자동으로 포함합니다.
* `#Requires -Version 5.1` — Windows 10에 기본 탑재된 것이 5.1입니다.
* `$ErrorActionPreference = 'Stop'` — 첫 줄에.
* 주석 기반 도움말(`.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER`, `.EXAMPLE`).
* 파괴적 동작에는 `[CmdletBinding(SupportsShouldProcess = $true)]`와 `$PSCmdlet.ShouldProcess`.
* 다운로드는 HTTPS만. SHA-256을 고정할 수 있게 매개변수를 두세요.
* 실패 시 0이 아닌 종료 코드.

---

## 10. 라이선스 주의

새 의존성을 추가하기 전에 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)를 읽고,
추가한 뒤에는 **반드시 그 문서를 갱신하세요.**

특히 두 가지:

1. **FFmpeg는 LGPL 공유 빌드여야 합니다.** GPL 빌드(`--enable-gpl`, x264/x265 포함)를 넣으면
   KSubMaker 전체의 배포 조건이 달라집니다. 앱은 FFmpeg를 링크하지 않고 **별도 프로세스로
   실행**하며, 이 사실이 라이선스 분석의 전제입니다.
2. **NLLB-200은 CC-BY-NC-4.0 — 비상업적 전용입니다.** 기본 번역 모델이므로 이 사실이
   모델 카탈로그의 `License` 필드, 모델 화면의 라이선스 열, README, THIRD_PARTY_NOTICES에
   모두 드러나 있어야 합니다. 지우지 마세요.

GPL/AGPL 라이브러리를 새로 링크하지 마세요.

---

## 11. 변경 전 체크리스트

- [ ] 계층 규칙을 지켰는가? ([§1](#1-계층-규칙))
- [ ] 새 경로가 `IAppPaths`를 거치는가?
- [ ] 새 비동기 코드가 `CancellationToken`을 끝까지 넘기는가?
- [ ] 새 프로세스 실행이 `ArgumentList`(또는 argv 리스트)를 쓰는가?
- [ ] 사용자에게 보이는 문자열이 한국어이고 리소스에 있는가?
- [ ] stdout에 아무것도 쓰지 않았는가? ([§4](#4-stdout은-프로토콜-전용입니다))
- [ ] 자막·경로·오디오를 기기 밖으로 보내지 않는가? ([§5](#5-자막-데이터는-기기-밖으로-나가지-않습니다))
- [ ] 프로토콜을 바꿨다면 6단계를 전부 밟았는가? ([§6](#6-프로토콜-변경-절차))
- [ ] 핵심 상태 전이를 바꿨다면 테스트를 추가했는가? ([§7](#7-테스트))
- [ ] 바이너리·모델·생성 산출물을 커밋하지 않았는가? ([§8](#8-커밋하지-말아야-할-것))
- [ ] 의존성을 추가했다면 `THIRD_PARTY_NOTICES.md`를 갱신했는가? ([§10](#10-라이선스-주의))
- [ ] `dotnet build KSubMaker.sln -c Release`와 `python -m pytest worker/tests`가 통과하는가?
- [ ] **문서가 구현보다 앞서 나가지 않는가?** 구현하지 않은 것은 "제한사항"에 적으세요.

마지막 항목이 이 저장소에서 특히 중요합니다. 정확한 제한사항 목록이 낙관적인 기능 목록보다
가치가 큽니다.

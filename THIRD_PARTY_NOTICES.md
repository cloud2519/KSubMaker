# 제3자 구성 요소 고지 / Third-Party Notices

KSubMaker 자체의 소스 코드는 MIT 라이선스입니다([`LICENSE`](LICENSE)). 이 문서는 KSubMaker와
**함께 배포되거나** 실행 중에 **사용자가 내려받는** 모든 제3자 구성 요소와 그 의무를
정리합니다.

이 문서는 저장소의 실제 내용을 읽어서 작성했습니다: `Directory.Packages.props`,
`worker/pyproject.toml`, `src/KSubMaker.Domain/Models/ModelCatalog.cs`, `scripts/*.ps1`.

> **먼저 알아야 할 두 가지**
>
> 1. **NLLB-200(기본 번역 모델)은 CC-BY-NC-4.0 — 비상업적 사용만 허용됩니다.** [§4.2](#42-번역-모델)
> 2. **FFmpeg는 LGPL 공유 빌드를 별도 프로세스로 실행합니다. GPL 빌드로 바꿔치기하면
>    KSubMaker 전체의 배포 조건이 달라집니다.** [§2](#2-ffmpeg--가장-중요한-항목)

---

## 1. 요약

| 계층 | 배포 형태 | 대표 라이선스 |
| --- | --- | --- |
| KSubMaker 소스 | 저장소 / 설치 프로그램 | **MIT** |
| .NET 런타임 + NuGet 패키지 | self-contained 게시본에 포함 | MIT / Apache-2.0 |
| FFmpeg | `tools/ffmpeg/bin`에 동봉, **별도 프로세스로 실행** | **LGPL-2.1-or-later** (공유 빌드) |
| CPython + 파이썬 패키지 | `tools/python`에 동봉 | PSF-2.0 / MIT / Apache-2.0 / BSD |
| NVIDIA CUDA 라이브러리 (cuBLAS 12, cuDNN 9) | `tools/python`에 동봉, **재배포 허용** | **NVIDIA 독점 EULA** ([§6.4](#64-nvidia-cuda-지원-라이브러리--재배포-조건이-있는-독점-라이선스)) |
| llama.cpp `llama-server` | **동봉 안 함.** 사용자가 선택적으로 설치 | MIT |
| Whisper 모델 가중치 | **동봉 안 함.** 사용자가 다운로드 | MIT |
| NLLB-200 모델 가중치 | **동봉 안 함.** 사용자가 다운로드 | **CC-BY-NC-4.0 (비상업)** |
| Qwen2.5 GGUF 가중치 | **동봉 안 함.** 사용자가 다운로드 | Apache-2.0 (단, [§4.3](#43-로컬-llm-모델) 확인 필요) |
| Inno Setup | 설치 프로그램을 만들 때만 사용. 산출물에 포함 안 됨 | Inno Setup License (수정 BSD 계열) |

---

## 2. FFmpeg — 가장 중요한 항목

| 항목 | 내용 |
| --- | --- |
| 구성 요소 | FFmpeg (`ffmpeg.exe`, `ffprobe.exe` + 공유 DLL) |
| 버전/출처 | `scripts/fetch-ffmpeg.ps1`이 받는 **Windows x64 LGPL 공유 빌드**. 정확한 버전은 배포본의 `tools/ffmpeg/bin` 안 라이선스 파일과 `ffmpeg -version` 출력으로 확인하세요 |
| 라이선스 | **LGPL-2.1-or-later** (LGPL 구성으로 빌드된 경우) |
| 포함 이유 | 컨테이너 메타데이터 읽기(`ffprobe`), 16kHz 모노 PCM WAV 추출, 내장 자막 트랙 추출 |
| 링크 방식 | **링크하지 않음.** `ProcessStartInfo`로 별도 프로세스를 실행하고 stdout/stderr만 읽습니다 |

### LGPL과 GPL의 차이, 그리고 KSubMaker가 LGPL을 쓰는 이유

FFmpeg는 **같은 소스에서 두 가지 라이선스로 빌드될 수 있습니다.**

| 빌드 구성 | 결과 라이선스 | 무엇이 들어가는가 |
| --- | --- | --- |
| 기본 (`--enable-gpl` 없음) | **LGPL-2.1-or-later** | 디먹서, 대부분의 디코더, PCM 인코더 등 |
| `--enable-gpl` 또는 GPL 전용 라이브러리(x264, x265, libsmbclient 등) 포함 | **GPL-2.0-or-later** | 위 + GPL 코덱 |
| `--enable-nonfree` | **재배포 불가** | 비자유 구성 요소 |

**GPL은 전염성이 있습니다.** GPL 빌드의 FFmpeg를 프로그램에 **링크**하면 그 프로그램 전체를
GPL로 배포해야 합니다.

KSubMaker는 두 겹으로 이 문제를 피합니다.

1. **LGPL 공유 빌드만 씁니다.** 오디오를 뽑고 컨테이너를 읽는 데는 GPL 코덱이 전혀 필요
   없습니다. x264/x265 같은 인코더는 쓰지 않습니다.
2. **링크하지 않고 별도 프로세스로 실행합니다.** `ffmpeg.exe`를 자식 프로세스로 띄우고 명령줄
   인자와 표준 스트림으로만 통신합니다. 이는 파생 저작물을 만들지 않는, 널리 통용되는
   경계입니다.

따라서 **KSubMaker에는 GPL 의무가 발생하지 않고, MIT로 배포할 수 있습니다.**

### ⚠️ 경고 — `tools/ffmpeg/bin`의 내용을 GPL 빌드로 바꾸지 마세요

`tools/ffmpeg/bin`의 실행 파일을 GPL 빌드(예: 인터넷에 흔한 "full" 빌드, x264 포함 빌드)로
교체하면:

* 위 분석이 **무효가 됩니다.**
* 그 배포본을 재배포하려면 GPL 조건을 검토해야 합니다.
* 순수한 프로세스 분리라 GPL 의무가 없다는 주장이 가능하더라도, 이는 다툼의 여지가 있는
  영역이며 **의도적으로 들어가지 않기로 한 영역**입니다.

`scripts/fetch-ffmpeg.ps1`은 LGPL 공유 빌드를 받도록 문서화되어 있고, 다운로드 URL과 SHA-256을
호출자가 고정할 수 있게 되어 있습니다. 다른 빌드를 넣으려면 이 문서도 함께 갱신하세요.

### LGPL 의무 (충족 방법)

| 의무 | KSubMaker의 충족 방법 |
| --- | --- |
| 라이선스 사본 제공 | FFmpeg 배포본에 포함된 `LICENSE`/`COPYING.LGPLv2.1` 파일을 `tools/ffmpeg/`에 그대로 둡니다 |
| LGPL 라이브러리 사용 사실 고지 | 이 문서 |
| 사용자가 라이브러리를 교체할 수 있을 것 | **공유(DLL) 빌드**를 그대로 배포하므로, 사용자가 `tools/ffmpeg/bin`의 DLL을 호환 버전으로 바꿀 수 있습니다 |
| 변경 사항 고지 | KSubMaker는 FFmpeg 소스를 수정하지 않습니다. 공식 빌드를 그대로 씁니다 |
| 소스 코드 제공 | 사용한 빌드에 해당하는 소스는 <https://ffmpeg.org/> 및 <https://git.ffmpeg.org/ffmpeg.git> 에서 얻을 수 있습니다. 요청 시 제공할 수 있도록 릴리스마다 사용한 빌드의 정확한 버전을 기록하세요 |

FFmpeg의 라이선스 설명 원문: <https://ffmpeg.org/legal.html>

---

## 3. 실행 파일 구성 요소

### 3.1 .NET 런타임

| 항목 | 내용 |
| --- | --- |
| 구성 요소 | .NET 10 런타임 (self-contained 게시본에 포함) |
| 라이선스 | **MIT** (일부 구성 요소는 Apache-2.0) |
| 포함 이유 | `dotnet publish --self-contained true`로 게시하므로 사용자가 .NET을 따로 설치할 필요가 없습니다 |
| 의무 | MIT 고지 유지. .NET 라이브러리 라이선스 전문은 <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> |

### 3.2 CPython (임베디드 런타임)

| 항목 | 내용 |
| --- | --- |
| 구성 요소 | CPython 3.11 Windows x64, [python-build-standalone](https://github.com/astral-sh/python-build-standalone) 배포판 |
| 라이선스 | **PSF License Agreement (Python Software Foundation License 2.0)**. python-build-standalone의 **빌드 스크립트**는 MPL-2.0이며, 배포판에 포함된 각 구성 요소(OpenSSL, SQLite, libffi, zlib, bzip2, xz 등)는 각자의 라이선스를 따릅니다 |
| 포함 이유 | 사용자가 Python을 설치하지 않아도 AI 워커가 돌아야 합니다. [ADR-013](docs/DECISIONS.md#adr-013--임베디드-파이썬은-python-build-standalone) |
| 의무 | PSF 라이선스 사본과 저작권 고지를 배포에 포함. python-build-standalone 배포판에 들어 있는 `python/licenses/` 디렉터리를 삭제하지 마세요 |

`scripts/build-worker.ps1`이 이 배포판을 받아 `tools/python/`에 풉니다.

### 3.3 llama.cpp (`llama-server`) — 선택 사항, 기본 미포함

| 항목 | 내용 |
| --- | --- |
| 구성 요소 | `llama-server.exe` + ggml/CUDA DLL |
| 저장소 | <https://github.com/ggml-org/llama.cpp> |
| 라이선스 | **MIT** |
| 포함 이유 | 번역 엔진을 "로컬 LLM"으로 골랐을 때의 추론 서버. Ollama를 쓰지 않은 이유는 [ADR-016](docs/DECISIONS.md#adr-016--로컬-llm은-ollama가-아니라-llamacpp-llama-server) |
| 배포 형태 | **기본 배포에 포함되지 않습니다.** `scripts/fetch-llama.ps1`로 받거나 사용자가 직접 설치합니다 |
| 의무 | MIT 고지 유지. 동봉해서 재배포한다면 llama.cpp의 `LICENSE` 파일을 `tools/llama/`에 함께 두세요 |

llama.cpp의 CUDA 빌드는 NVIDIA CUDA 런타임 라이브러리를 동봉할 수 있습니다. 그 라이브러리는
NVIDIA의 EULA(재배포 조항 포함)를 따릅니다 — CUDA 툴킷 EULA의 재배포 부속서를 확인하세요.

---

## 4. 모델 가중치 — **전부 동봉하지 않으며 사용자가 내려받습니다**

모델은 KSubMaker 배포물에 들어 있지 않습니다. 모델 화면에서 사용자가 명시적으로 선택해
`https://huggingface.co`에서 받습니다. **모델을 내려받는 순간, 사용자는 해당 모델의 라이선스에
동의하는 것입니다.** 각 모델의 라이선스는 모델 화면의 "라이선스" 열에 표시됩니다
(`ModelDescriptor.License`).

### 4.1 음성 인식 모델

| 구성 요소 | 저장소 | 라이선스 | 포함 이유 | 의무 |
| --- | --- | --- | --- | --- |
| Whisper base/small/medium/large-v3 (CTranslate2 변환본) | `Systran/faster-whisper-{base,small,medium,large-v3}` | **MIT** (원 가중치: OpenAI Whisper, MIT) | 음성 인식 | MIT 고지 |
| Whisper large-v3-turbo (CTranslate2 변환본) | `deepdml/faster-whisper-large-v3-turbo-ct2` | **MIT** (원 가중치: OpenAI Whisper, MIT) | VRAM 8GB 환경의 기본 모델 | MIT 고지 |

OpenAI Whisper 모델과 코드: <https://github.com/openai/whisper> (MIT). CTranslate2 변환본은
원 가중치의 라이선스를 그대로 승계합니다.

### 4.2 번역 모델

| 구성 요소 | 저장소 | 라이선스 | 포함 이유 | 의무 |
| --- | --- | --- | --- | --- |
| NLLB-200 distilled 600M (CTranslate2 변환본) | `entai2965/nllb-200-distilled-600B-ctranslate2` | **CC-BY-NC-4.0** | **기본 번역 엔진.** 200개 언어 → 한국어. 빠르고 VRAM이 적게 듭니다 | 아래 참조 |
| NLLB-200 distilled 1.3B (CTranslate2 변환본) | `entai2965/nllb-200-distilled-1.3B-ctranslate2` | **CC-BY-NC-4.0** | VRAM 12GB 이상에서의 기본값 | 아래 참조 |

> ## ⚠️ NLLB-200은 비상업적(Non-Commercial) 라이선스입니다
>
> **NLLB-200 가중치는 Creative Commons Attribution-NonCommercial 4.0 International
> (CC-BY-NC-4.0)으로 배포됩니다. 이것이 KSubMaker의 기본 번역 모델입니다.**
>
> 무엇을 뜻하는가:
>
> * **개인적·비상업적 용도로는 자유롭게 쓸 수 있습니다.** 개인 소장 영상에 자막을 다는 것,
>   연구, 학습, 비영리 용도.
> * **상업적 용도로는 이 모델을 쓸 수 없습니다.** 판매·광고·유료 서비스 등 상업적 이익을
>   주로 목적으로 하는 방식으로 이 모델을 사용해 자막을 만들면 라이선스 위반입니다.
> * 이 제약은 **KSubMaker 프로그램이 아니라 NLLB-200 모델 가중치**에 붙습니다. KSubMaker
>   소프트웨어 자체는 MIT입니다.
>
> **상업적으로 써야 한다면:**
>
> * 번역 엔진을 **로컬 LLM(Qwen2.5)** 으로 바꾸세요. 설정 → 번역 → 번역 엔진.
>   ([§4.3](#43-로컬-llm-모델)의 확인 사항을 먼저 읽으세요.)
> * 또는 상업적 사용이 허용된 다른 번역 모델을 `ModelCatalog`에 추가하세요. 모델 추가는
>   데이터 변경이라 파이프라인 코드를 고칠 필요가 없습니다.
> * `whisper-*` 모델(MIT)은 상업적 사용에 제약이 없습니다. **제약은 번역 단계에만
>   있습니다.**
>
> 원문 라이선스: <https://creativecommons.org/licenses/by-nc/4.0/>
> 원 모델: <https://huggingface.co/facebook/nllb-200-distilled-600M>
>
> CC-BY-NC-4.0은 **저작자 표시(BY)** 도 요구합니다. NLLB-200으로 만든 자막을 배포한다면
> 적절한 방식으로 출처를 밝히세요.

### 4.3 로컬 LLM 모델

| 구성 요소 | 저장소 | 카탈로그에 적힌 라이선스 | 포함 이유 |
| --- | --- | --- | --- |
| Gemma 3 4B Instruct (GGUF Q4_K_M) | `unsloth/gemma-3-4b-it-GGUF` | **Gemma Terms of Use** | 일→한 자막용 기본 LLM |
| Gemma 3 12B Instruct (GGUF Q4_K_M) | `unsloth/gemma-3-12b-it-GGUF` | **Gemma Terms of Use** | 품질 우선, VRAM 16GB 이상 |
| Qwen2.5 7B Instruct (GGUF Q4_K_M) | `Qwen/Qwen2.5-7B-Instruct-GGUF` | Apache-2.0 | 권장하지 않음(아래 참고) |
| Qwen2.5 3B Instruct (GGUF Q4_K_M) | `Qwen/Qwen2.5-3B-Instruct-GGUF` | Apache-2.0 | 권장하지 않음(아래 참고) |

> ⚠️ **Gemma 3는 Apache-2.0이 아닙니다.**
> [Gemma Terms of Use](https://ai.google.dev/gemma/terms)와 [사용 금지 정책](https://ai.google.dev/gemma/prohibited_use_policy)이
> 적용됩니다. Apache나 MIT 같은 순수 오픈소스 라이선스와 달리 **용도 제한 조항**이 있고,
> 모델이나 그 파생물을 재배포할 때 **같은 조건과 사용 금지 정책을 전달할 의무**가 있습니다.
> 상업적 사용 자체는 막혀 있지 않지만 금지 용도 목록을 반드시 읽으세요.
>
> KSubMaker는 가중치를 **동봉하지 않고** 사용자가 Hugging Face에서 직접 내려받으므로 배포자에게
> 재배포 의무가 생기지는 않습니다. 다만 카탈로그가 이 모델을 **권장**하고 앱이 다운로드를
> 대행하므로 여기에 고지합니다.
>
> 저장소가 `google/…`이 아니라 `unsloth/…`인 이유: Google 공식 GGUF 저장소는
> `gated: "manual"`이라 접근 승인과 토큰이 필요하고, 앱의 다운로더에는 토큰이 없어 401이
> 납니다. unsloth 미러는 같은 양자화를 게이팅 없이 공개하며 `license: gemma`로 표기합니다 —
> `deepdml`·`entai2965` 변환본을 쓰는 것과 같은 이유입니다.

> ⚠️ **배포 전에 반드시 직접 확인하세요.**
> `ModelCatalog.cs`는 두 모델 모두 Apache-2.0으로 표기하고 있습니다. 그러나 **Qwen2.5
> 계열은 파라미터 크기에 따라 라이선스가 다릅니다** — 일부 변형(특히 3B와 72B)은 Apache-2.0이
> 아니라 별도의 Qwen 계열 라이선스(연구용/추가 조건 포함)로 배포된 것으로 알려져 있습니다.
> 상업적 사용을 계획한다면 **각 모델의 Hugging Face 모델 카드에 표시된 라이선스를 직접
> 확인**하고, 다르면 `ModelCatalog.cs`의 `License` 문자열과 이 문서를 함께 고치세요.
> 모델 화면의 라이선스 열은 그 문자열을 그대로 보여 주므로, 값이 틀리면 사용자에게 잘못된
> 정보가 전달됩니다.

Apache-2.0의 의무: 라이선스 사본과 저작권 고지 유지, `NOTICE` 파일이 있으면 함께 배포,
변경한 파일에 변경 사실 표시.

> **Qwen2.5를 권장하지 않는 이유는 라이선스가 아니라 품질입니다.** 일본어 자막을 한국어가 아니라
> 중국어로 옮기는 것이 실측으로 확인됐습니다(출력 273줄 중 113줄이 간체자). 카탈로그에는 이미
> 내려받은 사용자를 위해 남겨 두었지만 하드웨어 권장에서는 선택되지 않습니다. 상세는
> `docs/MODEL_MANAGEMENT.md` §1.3.

---

## 5. NuGet 패키지 (`Directory.Packages.props`)

버전은 중앙 관리되며 아래 표가 그 파일의 실제 내용입니다.

### 5.1 애플리케이션에 배포되는 패키지

| 구성 요소 | 버전 | 라이선스 | 포함 이유 | 의무 |
| --- | --- | --- | --- | --- |
| `Microsoft.Extensions.DependencyInjection` | 10.0.0 | MIT | DI 컨테이너 | MIT 고지 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.0 | MIT | 계층 간 DI 추상화 | MIT 고지 |
| `Microsoft.Extensions.Hosting` | 10.0.0 | MIT | 앱 수명 관리(`Host.CreateApplicationBuilder`) | MIT 고지 |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.0 | MIT | 위의 추상화 | MIT 고지 |
| `Microsoft.Extensions.Logging` | 10.0.0 | MIT | 로깅 파사드 | MIT 고지 |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 | MIT | `ILogger<T>` — 코드 전체가 이것만 씁니다 | MIT 고지 |
| `Microsoft.Extensions.Options` | 10.0.0 | MIT | `IOptions<WorkerOptions>` | MIT 고지 |
| `Microsoft.Extensions.Configuration` | 10.0.0 | MIT | 설정 바인딩 | MIT 고지 |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.0 | MIT | 위와 동일 | MIT 고지 |
| `Microsoft.Extensions.Http` | 10.0.0 | MIT | 모델 다운로더의 `IHttpClientFactory` | MIT 고지 |
| `Microsoft.EntityFrameworkCore` | 10.0.0 | MIT | ORM | MIT 고지 |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.0 | MIT | SQLite 공급자. **SQLite 엔진 자체는 퍼블릭 도메인**이며 `SQLitePCLRaw`(Apache-2.0)를 통해 번들됩니다 | MIT / Apache-2.0 고지 |
| `Serilog` | 4.2.0 | Apache-2.0 | 파일 로그 싱크 구현 | Apache-2.0 고지, `NOTICE` 동봉 |
| `Serilog.Extensions.Logging` | 9.0.0 | Apache-2.0 | MEL ↔ Serilog 어댑터 | 위와 동일 |
| `Serilog.Sinks.File` | 6.0.0 | Apache-2.0 | 롤링 파일 로그 | 위와 동일 |
| `Serilog.Sinks.Console` | 6.0.0 | Apache-2.0 | 진단용 콘솔 출력 | 위와 동일 |
| `CommunityToolkit.Mvvm` | 8.4.0 | MIT | MVVM 소스 제너레이터(`ObservableObject`, `RelayCommand`) | MIT 고지 |

### 5.2 개발/설계 시점에만 쓰이는 패키지 (배포물에 포함되지 않음)

| 구성 요소 | 버전 | 라이선스 | 비고 |
| --- | --- | --- | --- |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.0 | MIT | `PrivateAssets="all"`. `dotnet ef migrations`용 |
| `Microsoft.Win32.Registry` | 5.0.0 | MIT | **중앙 목록에는 있지만 어느 프로젝트도 참조하지 않습니다.** `net10.0`에는 레지스트리 API가 이미 들어 있고, 이 호환 패키지를 참조하면 취약한 `System.Security.Cryptography.Xml`(NU1903)을 끌어옵니다. `KSubMaker.Infrastructure.csproj`에 그 이유가 주석으로 적혀 있습니다 |

### 5.3 테스트 패키지 (버전만 준비되어 있고 현재 사용처 없음)

`tests/` 디렉터리가 비어 있어 아래 패키지들은 **현재 어느 프로젝트도 참조하지 않습니다.**
테스트 프로젝트를 추가하면 사용됩니다.

| 구성 요소 | 버전 | 라이선스 |
| --- | --- | --- |
| `Microsoft.NET.Test.Sdk` | 17.12.0 | MIT |
| `xunit` | 2.9.2 | Apache-2.0 |
| `xunit.runner.visualstudio` | 2.8.2 | MIT |
| `coverlet.collector` | 6.0.2 | MIT |
| `FluentAssertions` | 7.0.0 | Apache-2.0 |
| `Microsoft.EntityFrameworkCore.InMemory` | 10.0.0 | MIT |

> **`FluentAssertions` 버전 고정에 대한 주의.** 7.x는 Apache-2.0입니다. **8.0 이후 버전은
> 라이선스가 변경되어 상업적 사용에 유료 라이선스가 필요합니다.**
> `Directory.Packages.props`가 7.0.0으로 고정한 것은 의도된 것이며, **아무 생각 없이 8.x로
> 올리지 마세요.**

---

## 6. 파이썬 패키지 (`worker/pyproject.toml`)

`ksubmaker-worker` 패키지 자체는 **MIT**입니다(`pyproject.toml`의 `license` 필드).

### 6.1 런타임 의존성 (`tools/python`에 설치되어 배포됨)

| 구성 요소 | 버전 제약 | 라이선스 | 포함 이유 | 의무 |
| --- | --- | --- | --- | --- |
| `faster-whisper` | `>=1.1` | **MIT** | 음성 인식 엔진. CTranslate2 기반 Whisper 구현 | MIT 고지 |
| `ctranslate2` | `>=4.5` | **MIT** | 추론 런타임. Whisper와 NLLB **양쪽**이 씁니다 | MIT 고지 |
| `transformers` | `>=4.44` | **Apache-2.0** | 토크나이저 로딩 | Apache-2.0 고지, `NOTICE` 동봉 |
| `sentencepiece` | (제약 없음) | **Apache-2.0** | NLLB의 `sentencepiece.bpe.model` 처리 | 위와 동일 |
| `huggingface-hub` | `>=0.26` | **Apache-2.0** | 모델 다운로드 보조 | 위와 동일 |
| `requests` | (제약 없음) | **Apache-2.0** | HTTP. 모델 다운로드와 `llama-server` 호출 | 위와 동일 |

프로젝트 홈:
faster-whisper <https://github.com/SYSTRAN/faster-whisper> ·
CTranslate2 <https://github.com/OpenNMT/CTranslate2>

### 6.2 개발 의존성 (배포물에 포함되지 않음)

| 구성 요소 | 버전 제약 | 라이선스 |
| --- | --- | --- |
| `pytest` | `>=8.0` | MIT |
| `pytest-cov` | `>=5.0` | MIT |

### 6.4 NVIDIA CUDA 지원 라이브러리 — 재배포 조건이 있는 독점 라이선스

**`worker/pyproject.toml`의 의존성이 아닙니다.** `scripts/build-worker.ps1`이 배포본을 만들 때만
`tools/python`에 설치합니다(플랫폼 전용 휠이라 Linux CI와 개발자 기계를 깨뜨리기 때문입니다).
그래도 **배포물에는 들어가므로** 고지 대상입니다.

| 구성 요소 | 버전 제약 | 라이선스 | 포함 이유 | 의무 |
| --- | --- | --- | --- | --- |
| `nvidia-cublas-cu12` | `>=12.9,<13` | **NVIDIA 독점 EULA** (재배포 허용) | `ctranslate2 >= 4.5`가 `cublas64_12.dll`을 요구합니다. NVIDIA **드라이버**는 이것을 제공하지 않습니다 | EULA 원문 동봉, NVIDIA 저작권 표시 유지, 리버스 엔지니어링 금지 |
| `nvidia-cudnn-cu12` | `>=9.24,<10` | **NVIDIA 독점 EULA** (재배포 허용) | 같은 이유로 `cudnn64_9.dll` | 위와 동일 |

* cuBLAS는 **CUDA Toolkit EULA**의 재배포 가능(redistributable) 구성 요소입니다.
  <https://docs.nvidia.com/cuda/eula/>
* cuDNN은 **NVIDIA cuDNN Software License Agreement**를 따릅니다. 애플리케이션과 함께
  재배포하는 것이 명시적으로 허용됩니다. <https://docs.nvidia.com/deeplearning/cudnn/sla/>

**GPL/LGPL이 아니므로 KSubMaker 전체의 배포 조건에는 영향이 없습니다.** FFmpeg와 달리 이
라이브러리들은 별도 프로세스가 아니라 CTranslate2에 **동적 링크**되지만, 라이선스가 독점
바이럴이 아닌 재배포 허용형이라 문제가 되지 않습니다.

두 휠은 각각 EULA 원문을
`site-packages/nvidia_{cublas,cudnn}_cu12-<버전>.dist-info/licenses/License.txt`에 함께 설치합니다
(2026-08 확인). **그 폴더를 정리 단계에서 지우지 마세요.**

실측 크기(2026-08, `nvidia-cublas-cu12` 12.9.2.10 / `nvidia-cudnn-cu12` 9.24.0.43, win_amd64):
휠 528 + 703 MiB, 설치 후 736 + 1,071 MiB = **약 1.8 GiB**.

주 버전 상한(`<13`, `<10`)은 라이선스가 아니라 기술적 제약입니다 —
[ADR-030](docs/DECISIONS.md#adr-030--cuda-런타임-라이브러리를-임베디드-파이썬에-함께-넣는다) 참고.

### 6.3 전이 의존성

위 6개 패키지는 상당수의 전이 의존성을 끌어옵니다. 자유 라이선스가 대부분이지만
**릴리스 전에 실제로 설치된 목록을 확인하세요.** 배포물에 무엇이 들어가는지가 곧 고지해야 할
목록입니다.

```powershell
# tools\python 을 만든 뒤
.\tools\python\python.exe -m pip install pip-licenses
.\tools\python\python.exe -m piplicenses --format=markdown --with-urls --with-license-file `
    --output-file THIRD_PARTY_PYTHON.md
```

자주 나타나는 것들과 알려진 라이선스(참고용 — 반드시 위 명령으로 실제 값을 확인하세요):

| 구성 요소 | 어디서 오는가 | 알려진 라이선스 |
| --- | --- | --- |
| `tokenizers` | transformers | Apache-2.0 |
| `safetensors` | transformers | Apache-2.0 |
| `regex` | transformers | Apache-2.0 |
| `numpy` | faster-whisper / ctranslate2 | BSD-3-Clause |
| `onnxruntime` | faster-whisper (VAD) | MIT |
| `av` (PyAV) | faster-whisper | BSD-3-Clause (FFmpeg 라이브러리를 링크합니다 — 아래 주의) |
| `tqdm` | huggingface-hub | MPL-2.0 + MIT |
| `filelock` | huggingface-hub | Unlicense |
| `fsspec` | huggingface-hub | BSD-3-Clause |
| `PyYAML` | transformers | MIT |
| `packaging` | 다수 | Apache-2.0 / BSD-2-Clause |
| `typing-extensions` | 다수 | PSF-2.0 |
| `urllib3` | requests | MIT |
| `certifi` | requests | MPL-2.0 |
| `charset-normalizer` | requests | MIT |
| `idna` | requests | BSD-3-Clause |

> ⚠️ **`av`(PyAV) 주의.** PyAV 휠에는 FFmpeg 라이브러리가 함께 들어 있으며 이것들은
> **링크**됩니다(별도 프로세스가 아닙니다). PyPI의 PyAV 휠은 LGPL 구성으로 빌드된 FFmpeg를
> 담는 것으로 알려져 있지만, [§2](#2-ffmpeg--가장-중요한-항목)의 논리(프로세스 분리)가 여기에는
> 적용되지 않습니다. 배포 전에 설치된 `av` 휠에 포함된 라이선스 파일을 확인하고, 필요하면 이
> 문서에 별도 항목으로 추가하세요.
>
> 참고로 KSubMaker의 워커는 **PyAV를 직접 쓰지 않습니다.** 오디오 추출은 번들된 `ffmpeg.exe`를
> 별도 프로세스로 실행해서 합니다(`ffmpeg_service.py`). PyAV는 faster-whisper의 전이 의존성으로
> 따라 들어올 뿐입니다.

---

## 7. 빌드 도구 (배포물에 포함되지 않음)

| 구성 요소 | 라이선스 | 용도 | 의무 |
| --- | --- | --- | --- |
| Inno Setup 6 | Inno Setup License (수정 BSD 계열 — 배포와 상업적 사용 허용) | `installer/KSubMaker.iss`를 컴파일해 설치 프로그램을 만듭니다 | 설치 프로그램 산출물에 Inno Setup 저작권 고지가 자동으로 포함됩니다. 라이선스 전문: <https://jrsoftware.org/files/is/license.txt> |
| .NET SDK 10 | MIT | 빌드 | — |
| PowerShell 5.1 / 7+ | MIT (7+) / Windows 구성 요소 (5.1) | 패키징 스크립트 | — |

---

## 8. 릴리스 전 라이선스 체크리스트

- [ ] `tools/ffmpeg/bin`이 **LGPL 공유 빌드**인지 확인했다. `ffmpeg -version` 출력에
      `--enable-gpl`이 **없어야** 합니다.
- [ ] FFmpeg 배포본의 라이선스 파일이 `tools/ffmpeg/`에 그대로 있다.
- [ ] python-build-standalone 배포판의 라이선스 디렉터리가 `tools/python/`에 그대로 있다.
- [ ] `pip-licenses`로 실제 설치된 파이썬 패키지 목록과 라이선스를 확인했다([§6.3](#63-전이-의존성)).
- [ ] `av`(PyAV) 휠에 포함된 FFmpeg 라이브러리의 라이선스를 확인했다.
- [ ] `nvidia_cublas_cu12-*.dist-info` / `nvidia_cudnn_cu12-*.dist-info`의 `licenses/License.txt`가
      `tools/python/Lib/site-packages`에 그대로 남아 있다([§6.4](#64-nvidia-cuda-지원-라이브러리--재배포-조건이-있는-독점-라이선스)).
      `-SkipCudaLibraries`로 만든 CPU 전용 배포본에는 해당하지 않습니다.
- [ ] `llama-server`를 동봉한다면 llama.cpp의 `LICENSE`를 `tools/llama/`에 함께 넣었다.
- [ ] `ModelCatalog.cs`의 `License` 문자열이 각 모델 카드의 실제 라이선스와 일치한다
      (특히 [§4.3](#43-로컬-llm-모델)의 Qwen2.5).
- [ ] `LICENSE`와 이 파일이 설치 프로그램·포터블 zip 양쪽에 들어간다.
- [ ] `FluentAssertions`가 여전히 7.x다([§5.3](#53-테스트-패키지-버전만-준비되어-있고-현재-사용처-없음)).
- [ ] NLLB-200의 비상업 제약이 README, 모델 화면, 이 문서에 모두 표시되어 있다.

---

## 9. 정정 요청

이 문서의 내용에 오류가 있거나 누락된 구성 요소가 있다면 이슈로 알려 주세요. 라이선스 정보는
상류에서 바뀔 수 있으므로, **릴리스마다 실제 배포물의 내용을 기준으로 다시 확인하는 것**이
이 문서를 신뢰할 수 있게 유지하는 유일한 방법입니다.

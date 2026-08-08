# 결정 기록 (ADR)

코드에 실제로 남아 있는, 자명하지 않은 선택들을 기록합니다. 각 항목은 **배경 → 결정 → 결과**
순서이며, 결과에는 좋은 쪽과 나쁜 쪽을 함께 적었습니다.

새 결정을 추가할 때는 번호를 이어 붙이고, 기존 항목은 지우지 말고 "대체됨(→ ADR-NNN)"으로
표시하세요.

---

## ADR-001 — .NET 10 + WPF

**배경.** Windows 전용 데스크톱 앱이고, 대상 사용자는 개발자가 아닙니다. 대량 파일 목록을
실시간 진행률과 함께 보여 줘야 하며, 배포는 "설치 프로그램 하나" 또는 "압축 풀고 실행"이어야
합니다.

**결정.** `net10.0-windows` + WPF. MVVM은 `CommunityToolkit.Mvvm` 소스 제너레이터.
호스팅/DI/로깅은 `Microsoft.Extensions.*`.

**결과.**
- WPF의 `DataGrid` 가상화 덕분에 수천 행 큐도 UI 스레드를 막지 않습니다. 진행률 갱신은
  `BulkObservableCollection`으로 묶어 통지 폭풍을 막습니다.
- `dotnet publish --self-contained`로 .NET 런타임 설치가 필요 없는 배포가 됩니다(ADR-016).
- WinUI 3/MAUI 대비 배포가 단순하고(MSIX 필수 아님) 성숙한 반면, 최신 UI 스타일은 아닙니다.
- WPF 프로젝트 하나만 Windows 전용 TFM이라 나머지 5개 프로젝트는 Linux CI에서 그대로
  빌드됩니다(ADR-005).

---

## ADR-002 — EF Core + SQLite, 열거형은 문자열로 저장

**배경.** 작업 큐(수천 건 가능), 설정, 모델 설치 기록을 로컬에 저장해야 합니다. 서버는
없습니다. 사용자가 프로그램을 껐다 켜도 큐가 남아 있어야 합니다.

**결정.** 단일 SQLite 파일(`%LOCALAPPDATA%\KSubMaker\database\ksubmaker.db`) + EF Core 10.
`DbContext`는 `IDbContextFactory`로만 만들고(큐 펌프·UI·다운로더가 동시에 접근),
`PRAGMA journal_mode=WAL`을 켭니다. **모든 열거형은 `HasConversion<string>()`로 이름 저장.**

**결과.**
- 열거형 멤버를 중간에 삽입하거나 순서를 바꿔도 기존 행이 다른 값으로 재해석되지 않습니다.
  서수 저장이었다면 조용한 데이터 손상이 됩니다.
- WAL 덕분에 UI가 목록을 읽는 동안 펌프가 진행률을 써도 "database is locked"가 나지 않습니다.
  WAL을 거부하는 파일 시스템(일부 네트워크 드라이브)에서는 경고만 남기고 기본 저널 모드로
  계속합니다.
- 열 크기가 살짝 늘고 인덱스 비교가 문자열 비교가 되지만, 이 규모에서는 측정되지 않습니다.
- `Job.VideoPath` 열은 `NOCASE` 콜레이션을 씁니다. Windows 파일 시스템 의미론과 메모리 상의
  `OrdinalIgnoreCase` 조회를 일치시키기 위해서입니다.
- `Job.EstimatedTimeRemaining`은 **일부러 매핑하지 않습니다.** 재시작 후에는 의미가 없는 값이라
  저장하면 실행 중이 아닌 작업에 "3분 남음"이 남습니다.

---

## ADR-003 — 설정은 평평한 key/value 행

**배경.** `AppSettings`에는 40개가 넘는 속성이 있고, 개발 중에 계속 늘어납니다. 설정 하나
추가할 때마다 스키마 마이그레이션을 만드는 것은 비용이 큽니다.

**결정.** `AppSettings` 테이블은 `(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)` 두 열뿐입니다
(`SettingRecord`). 용어집처럼 구조가 있는 값은 키 하나에 JSON 블롭으로 넣습니다.

**결과.**
- 설정 속성 추가 = C# 속성 하나 추가. 마이그레이션 없음.
- 값이 없는 키는 코드의 기본값이 이깁니다. 그래서 구버전 DB도 신버전에서 그대로 열립니다.
- 대가: 데이터베이스만 봐서는 타입을 알 수 없고, SQL로 설정을 질의하기 불편합니다. 설정을
  질의할 일이 없으므로 받아들였습니다.

---

## ADR-004 — 도메인 계층은 NuGet 의존성 0개

**배경.** 상태 기계, 진행률 가중치, 하드웨어 권장 규칙, 한국어 줄바꿈, SRT 직렬화는 이
프로그램의 "규칙"입니다. 이 규칙들이 EF Core나 로깅 라이브러리와 얽히면 테스트가 무거워집니다.

**결정.** `KSubMaker.Domain.csproj`에는 `PackageReference`가 하나도 없습니다. 부작용이 있는
것(파일, 프로세스, 네트워크)은 전부 `Application/Abstractions`의 인터페이스로 밀어냅니다.
예를 들어 `OutputPathResolver.Resolve`는 파일 존재 확인을 `Func<string, bool>`로 주입받습니다.

**결과.**
- 규칙을 순수 함수로 단위 테스트할 수 있습니다(테스트 프로젝트가 추가되면 — ADR-020 참고).
- `Domain`을 참조하는 쪽은 무엇을 끌어올지 걱정할 필요가 없습니다.
- 대가: 인터페이스가 늘어납니다. `IFileSystem`처럼 얇은 래퍼가 필요합니다.

---

## ADR-005 — `EnableWindowsTargeting`으로 비-Windows CI 빌드 허용

**배경.** 검증 빌드는 Linux 컨테이너에서 돕니다. WPF 프로젝트는 `net10.0-windows`를 대상으로
하는데, 기본값으로는 비-Windows 호스트에서 restore가 거부됩니다. 그렇다고 CI에서 WPF 프로젝트만
빼면, 컴파일 오류가 릴리스 직전에야 발견됩니다.

**결정.** `Directory.Build.props`에 `<EnableWindowsTargeting>true</EnableWindowsTargeting>`.
동시에 **Windows 전용 코드를 가진 어셈블리는 일부러 `net10.0`으로 둡니다** —
`KSubMaker.Infrastructure`(레지스트리, `GlobalMemoryStatusEx`)와
`KSubMaker.Worker`(Job Object interop)는 전부 `OperatingSystem.IsWindows()` 가드 안에 있습니다.

**결과.**
- 솔루션 전체가 Linux에서 `dotnet build`로 컴파일됩니다. WPF 앱을 **실행**하는 데는 여전히
  Windows가 필요합니다.
- P/Invoke 호출부마다 가드가 필요하고, 가드를 빠뜨리면 Linux에서 런타임에 터집니다. 대신
  `Directory.Build.props`가 `CS8600;CS8602;CS8603;CS8618;CS4014`를 오류로 승격시켜
  실수 대부분을 컴파일 단계에서 잡습니다.
- Windows 참조 어셈블리 팩을 CI에서 내려받아야 하므로 최초 restore가 조금 느립니다.

---

## ADR-006 — GPU 감지는 WMI/NVML이 아니라 nvidia-smi

**배경.** VRAM 용량과 여유량을 알아야 모델·정밀도·처리 방식을 고를 수 있습니다. 후보는
셋이었습니다: WMI `Win32_VideoController`, NVML(`nvml.dll` P/Invoke), `nvidia-smi` 실행.

**결정.** `nvidia-smi --query-gpu=index,name,memory.total,memory.free,driver_version,compute_cap
--format=csv,noheader,nounits`를 실행해 CSV를 파싱합니다(`WindowsHardwareDetector`).

**결과.**
- WMI를 안 쓰므로 `System.Management` 의존이 없고, 따라서 `net10.0-windows`로 올라갈 필요가
  없습니다(ADR-005와 맞물립니다).
- 더 중요하게, `Win32_VideoController.AdapterRAM`은 32비트라 **4GB에서 값이 감깁니다.** 이
  프로그램이 관심 갖는 카드가 정확히 그 이상입니다. 쓸 수 없는 값입니다.
- NVML은 정확하지만 `nvml.dll` 로드 경로와 버전 호환을 직접 다뤄야 합니다. `nvidia-smi`는
  드라이버와 함께 반드시 설치되고, System32에 있어 PATH에서 바로 찾힙니다.
- 대가: 프로세스 실행 비용(10초 타임아웃)이 듭니다. 그래서 `HardwareService`가 결과를
  캐시하고, 시작 시 한 번과 명시적 새로고침에서만 실행합니다.
- **nvidia-smi로도 알 수 없는 것이 하나 있습니다: CUDA를 실제로 쓸 수 있는가.** 드라이버가
  깔려 있고 카드가 보여도 CTranslate2가 디바이스를 못 열 수 있습니다(CUDA 주 버전 불일치,
  cuDNN 누락, 패스스루 없는 WSL). 그 답은 모델을 올릴 프로세스만 할 수 있으므로
  [ADR-028](#adr-028--cuda-사용-가능-여부는-워커가-정본이다)에서 다룹니다.
- 폴백 경로도 있습니다. PATH에 없으면
  `%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe`와 `%SystemRoot%\System32`를
  차례로 봅니다. 전부 실패하면 GPU 없음으로 간주하고 경고를 프로필에 담습니다.

---

## ADR-007 — `[JsonPolymorphic]` 대신 손으로 짠 판별자 디스패치

**배경.** 이벤트는 16종, 명령은 11종입니다. `System.Text.Json`에는 `[JsonPolymorphic]` +
`[JsonDerivedType]`이라는 기성 기능이 있습니다.

**결정.** 쓰지 않습니다. `WorkerProtocolSerializer`가 `JsonDocument`로 `type`(이벤트) 또는
`command`(명령) 문자열을 읽고, `switch` 식으로 CLR 타입을 고른 뒤 그 타입으로 역직렬화합니다.

**이유.** `System.Text.Json`의 다형 역직렬화는 **판별자 속성이 JSON 객체의 첫 번째 속성일
것**을 요구합니다. 반대편은 Python이고, `dict`의 삽입 순서에 프로토콜의 정확성을 걸 수는
없습니다. 순서가 어긋나는 순간 모든 이벤트가 실패합니다.

**결과.**
- 필드 순서가 무엇이든 파싱됩니다.
- `DeserializeEvent`가 **절대 예외를 던지지 않습니다.** 알 수 없는 타입, 깨진 JSON, JSON이
  아닌 줄, 빈 줄은 전부 `UnknownEvent`(사유 포함)가 되어 경고 로그 한 줄로 끝납니다. 한 줄
  때문에 파이프라인이 멈추지 않습니다.
- 대가: 이벤트/명령을 추가할 때 `ResolveEventType`/`ResolveCommandType`에 한 줄씩 손으로
  추가해야 합니다. 빠뜨리면 `UnknownEvent`가 되므로 조용히 틀리지는 않습니다.
- 옵션은 `JsonSerializerDefaults.Web`(camelCase) + `WhenWritingNull` +
  `AllowReadingFromString`(파이썬이 숫자를 문자열로 보내도 견딤) +
  `UnsafeRelaxedJsonEscaping`(한글이 `\uXXXX`로 부풀지 않게).

---

## ADR-008 — SRT는 UTF-8 **BOM 포함** + CRLF

**배경.** 결과 자막은 사용자의 기존 플레이어에서 열립니다. 대상은 PotPlayer, GOM, KMPlayer,
곰녹음기 같은 국내에서 흔한 Windows 플레이어입니다.

**결정.** `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`로 BOM을 붙이고,
`SrtFormatter.ToWindowsLineEndings`로 모든 줄 끝을 CRLF로 만듭니다. Python 쪽
`subtitle_writer.py`도 같은 규칙(`UTF8_BOM` 상수, `newline=""`)을 씁니다.

**이유.** BOM이 없는 UTF-8 SRT를 만나면 상당수의 Windows 플레이어가 시스템 ANSI 코드
페이지(한국어 Windows에서는 cp949)로 폴백합니다. 그러면 한글이 전부 깨져 보입니다. BOM 3바이트가
그 문제를 없앱니다.

**결과.**
- 국내 플레이어에서 인코딩을 수동으로 바꿀 필요가 없습니다.
- 일부 Unix 계열 도구는 BOM을 첫 자막 텍스트의 일부로 봅니다(예: `grep`으로 첫 줄 매칭).
  Windows 플레이어 호환이 우선이라 감수합니다.
- 자막 인덱스는 출력 순서대로 1부터 다시 매깁니다. 파이프라인이 위에서 무엇을 했든 파일은
  항상 연속 번호를 갖습니다.
- 타임스탬프는 정수 밀리초로 계산합니다(`3.9999999`가 `00:00:03,999`가 되는 문제 방지).
  Python 쪽은 `_round_half_away`로 .NET의 `MidpointRounding.AwayFromZero`를 흉내 내어 두
  구현이 **바이트 단위로** 같은 파일을 만듭니다.

---

## ADR-009 — 출력 충돌 기본 정책은 "건너뛰기"

**배경.** 결과 파일은 사용자의 영상 폴더에 직접 쓰입니다. 그 폴더에는 사용자가 손으로 만들었거나
다른 곳에서 받은 자막이 이미 있을 수 있습니다.

**결정.** `AppSettings.OutputConflictPolicy`의 기본값은 `OutputConflictPolicy.Skip`.
`{영상 이름}.ko.srt`가 이미 있으면 쓰지 않고 작업을 완료로 표시합니다. `Overwrite`와
`CreateNumberedCopy`(`movie.ko (2).srt`)는 사용자가 명시적으로 골라야 합니다.

**이유.** 200개 파일을 일괄 처리하다가 사용자가 직접 번역한 자막을 덮어쓰는 것은 되돌릴 수
없는 손실입니다. 반대로 건너뛰어서 생기는 손해는 "다시 돌려야 한다"뿐입니다. 비대칭이 명확합니다.

**결과.**
- 재실행이 안전합니다. 같은 폴더를 다시 스캔해서 시작해도 기존 결과를 망가뜨리지 않습니다.
- `AtomicSubtitleWriter`는 정책을 **가장 먼저** 적용하고, 그다음 디스크 여유 공간(50MB
  미만이면 거부)을 확인하고, 같은 디렉터리의 임시 파일에 쓴 뒤 이동합니다. 어떤 실패도 기존
  파일을 파괴하지 않습니다.
- 정책은 프로토콜 1.1의 `settings.outputConflictPolicy`(`skip`/`overwrite`/`numbered`)로
  워커에도 전달되며, 워커의 `subtitle_writer`가 같은 순서로 같은 판단을 합니다. 와이어 값이
  알 수 없는 문자열이면 양쪽 모두 `skip`으로 떨어집니다 — 새 정책을 한쪽에만 추가해도 사용자
  파일을 덮어쓰는 사고는 나지 않습니다.
- 워커가 쓰지 않기로 했을 때는 `completed.skipped=true`가 오고, 호스트는 그것을
  `JobExecutionResult.Skipped`로 옮깁니다. 작업은 성공으로 끝나되 파일은 그대로입니다.

---

## ADR-010 — `condition_on_previous_text`는 기본 꺼짐

**배경.** faster-whisper의 `condition_on_previous_text`는 이전 구간의 텍스트를 다음 디코딩의
프롬프트로 넘깁니다. 켜면 문맥 일관성이 좋아지지만, 한 번 잘못된 반복이 생기면 그 반복이
프롬프트를 통해 스스로를 강화합니다.

**결정.** `AppSettings.ConditionOnPreviousText`의 기본값은 `false`. 설정 화면에서 켤 수 있습니다.

**이유.** 긴 영상에서 같은 문장이 수십 줄 반복되는 폭주는 이 옵션이 켜져 있을 때 압도적으로
많이 발생합니다. 자막 품질의 하한을 지키는 것이 상한을 올리는 것보다 중요합니다 — 폭주한 자막은
쓸모가 없지만, 문맥이 약간 부족한 자막은 여전히 쓸 만합니다.

**결과.**
- 대명사 지시나 화자 연속성이 약간 나빠질 수 있습니다.
- 문맥 보완은 **번역 단계**에서 합니다. `TranslationBatcher`가 앞 배치의 마지막 3줄을
  읽기 전용 문맥으로 넘기며, 그 3줄은 결과에 다시 포함되지 않습니다.
- `AppSettings.BeamSize` 기본값 5, `VadFilter` 기본 켜짐, `WordTimestamps` 기본 켜짐도 같은
  "안정성 우선" 기조입니다. 단어 타임스탬프는 특히 중요한데, `SegmentSplitter`가 번역 전에
  긴 세그먼트를 자를 때 그 정보를 씁니다.

---

## ADR-011 — 자식 프로세스 정리는 Windows Job Object

**배경.** 프로세스 트리가 3단입니다: `KSubMaker.App.exe` → `python.exe` → `ffmpeg.exe` /
`llama-server.exe`. 사용자가 작업 관리자에서 앱을 강제 종료하면, 관리 코드는 한 줄도 실행되지
않습니다. 손자 프로세스들이 살아남아 영상 파일 핸들과 VRAM을 계속 잡습니다.

**결정.** 워커를 시작한 **직후** `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`로 만든 Job Object에
배정합니다(`WindowsJobObject`). 정상 경로에서는 추가로 `shutdown` 명령 → 타임아웃 →
`ProcessTree.KillTree(entireProcessTree: true)` 순으로 정중하게 내려갑니다.

**이유.** Job Object는 **커널이** 보증합니다. 마지막 핸들이 닫히는 순간(호스트 프로세스가
어떤 이유로 죽든) 커널이 job 안의 모든 프로세스를 종료합니다. 사용자 코드가 실행될 필요가
없다는 점이 다른 모든 방법과의 결정적 차이입니다.

**결과.**
- "UI 종료 시 Python/FFmpeg 자식 프로세스가 남지 않는다"가 실제로 보장됩니다.
- 비-Windows에서는 `WindowsJobObject`가 무해한 no-op이라 같은 코드가 Linux에서 컴파일·실행
  됩니다.
- `Start()`와 `TryAssign()` 사이의 마이크로초 단위 창에서만 고아가 생길 수 있습니다.
  코드 주석에도 그렇게 적혀 있습니다.
- Python 쪽에도 대칭 안전망이 있습니다. `cancellation.GLOBAL_PROCESSES`가 워커가 띄운 모든
  자식(ffmpeg, llama-server)을 추적하고 종료 시 정리합니다.

---

## ADR-012 — WiX/MSIX가 아니라 Inno Setup

**배경.** 배포물은 self-contained .NET 게시본 + 임베디드 CPython + FFmpeg 바이너리로 수백 MB
규모이고, 설치 UI는 한국어여야 하며, **제거 시 사용자가 내려받은 모델(수 GB)을 지우면 안
됩니다.**

**결정.** Inno Setup 6 (`installer/KSubMaker.iss`).

**이유.**
| 후보 | 판단 |
| --- | --- |
| **Inno Setup** | 스크립트 하나로 끝. 한국어 UI 내장(`Languages: korean`). `[Code]` 파스칼 스크립트로 "NVIDIA GPU 없음" 경고 같은 조건 로직을 쉽게 넣습니다. 무료·오픈소스이며 사인되지 않은 앱도 문제없이 다룹니다. |
| **WiX** | MSI는 강력하지만 XML 분량이 훨씬 많고, 수천 개 파일을 담은 게시 디렉터리를 다루려면 `heat` 하베스팅 파이프라인이 필요합니다. 조건부 경고 하나 넣는 데 커스텀 액션 DLL 논의가 시작됩니다. |
| **MSIX** | 코드 서명이 사실상 필수이고, 앱 컨테이너 제약이 있습니다. 자식 프로세스를 띄우고 사용자 폴더에 파일을 쓰는 이 앱과 궁합이 나쁩니다. |

**결과.**
- 제거 시 `%LOCALAPPDATA%\KSubMaker`를 **건드리지 않습니다.** 대신 제거 후 확인 대화상자로
  "모델과 설정도 지울까요?"를 묻습니다. 기본은 "아니오"입니다.
- ISCC(Inno Setup 컴파일러)가 빌드 머신에 있어야 합니다. `scripts/build-installer.ps1`이
  없으면 설치 링크와 함께 명확한 오류를 냅니다.
- 코드 서명을 하지 않으면 SmartScreen 경고가 뜹니다. 서명 인증서가 준비되면 `SignTool`
  지시문을 추가하면 됩니다.

---

## ADR-013 — 임베디드 파이썬은 python-build-standalone

**배경.** 사용자에게 Python 설치를 요구할 수 없습니다. 워커는 CPython 3.11 이상과
faster-whisper·ctranslate2·transformers 등이 설치된 환경을 필요로 합니다.

**결정.** [python-build-standalone](https://github.com/astral-sh/python-build-standalone)의
CPython 3.11 Windows x64 배포판을 `tools/python/`에 풀고, 거기에 `worker/`를 `pip install`
합니다(`scripts/build-worker.ps1`).

**이유.**
- python.org의 **embeddable zip**은 기본적으로 `pip`과 `site-packages` 처리가 잘려 있어
  네이티브 확장이 많은 패키지를 넣기가 번거롭습니다.
- python-build-standalone은 완전한 표준 라이브러리와 정상 동작하는 `pip`을 포함한
  재배치 가능한 빌드이며, 라이선스가 명확합니다(CPython은 PSF, 빌드 스크립트는 MPL-2.0).
- PyInstaller/Nuitka로 얼린 단일 exe도 대안이고 `ToolLocator`가 그 경로(`worker/ksubmaker-worker.exe`)를
  **가장 먼저** 찾도록 되어 있습니다. 다만 CUDA DLL을 끌고 다니는 얼린 빌드는 재현성이 나빠서
  현재 기본은 임베디드 인터프리터입니다.

**결과.**
- `ToolLocator`의 탐색 순서가 이 결정을 그대로 반영합니다:
  ① `<앱>/worker/ksubmaker-worker.exe`(얼린 빌드) → ② `<앱>/tools/python/python.exe -m ksubmaker_worker`
  → ③ `KSUBMAKER_WORKER_PYTHON` 환경 변수 → ④ PATH의 `python3`/`python`(개발용 폴백).
- 배포 크기가 큽니다. CUDA용 ctranslate2까지 넣으면 수백 MB입니다.
- ④번 경로로 실행되면 로그에 경고가 남습니다. 프로덕션에서 그 줄이 보이면 배포가 깨진 것입니다.

---

## ADR-014 — GPL이 아니라 LGPL FFmpeg 빌드

**배경.** FFmpeg는 빌드 옵션에 따라 LGPL-2.1+ 또는 GPL-2.0+로 배포됩니다. `--enable-gpl`이나
GPL 전용 라이브러리(x264, x265 등)를 넣으면 결과물 전체가 GPL이 됩니다.

**결정.** **LGPL 공유 라이브러리 빌드**를 받아 `tools/ffmpeg/bin/`에 넣고, 앱은 그것을
`ProcessStartInfo`로 **별도 프로세스로 실행**합니다. 링크하지 않습니다.
`scripts/fetch-ffmpeg.ps1`이 이 제약을 문서화하고 SHA-256 고정을 지원합니다.

**이유.**
- 우리는 오디오를 뽑고 컨테이너를 읽기만 합니다. 인코더가 필요 없으므로 GPL 빌드를 쓸 이유가
  전혀 없습니다.
- LGPL 공유 빌드를 **별도 프로세스로 실행**하는 것은 링크가 아니므로 파생 저작물이 생기지
  않습니다. LGPL의 재링크 의무도 동적 라이브러리를 그대로 배포하므로 충족됩니다.
- GPL 빌드를 넣는 순간 KSubMaker 전체를 GPL로 배포해야 하는 논의가 시작됩니다. 그럴 이유가
  없습니다.

**결과.**
- **누구든 `tools/ffmpeg/bin`의 내용을 GPL 빌드로 바꿔치기하면 이 분석이 무효가 됩니다.**
  `THIRD_PARTY_NOTICES.md`와 `scripts/fetch-ffmpeg.ps1` 양쪽에 경고를 남겼습니다.
- LGPL 의무(라이선스 전문 동봉, 변경 사항 고지, 라이브러리 교체 가능성)는 공유 DLL을 그대로
  배포하고 고지를 포함하는 것으로 충족합니다.

---

## ADR-015 — 기본 번역 엔진 = CTranslate2 + NLLB-200

**배경.** 번역 백엔드 후보: (a) 전용 신경망 기계번역(NLLB, M2M-100, MADLAD), (b) 로컬 LLM,
(c) 클라우드 API.

**결정.** 기본값은 (a). 구체적으로 CTranslate2로 변환된 NLLB-200 distilled 600M(VRAM 12GB
이상이면 1.3B). (c)는 아예 구현하지 않습니다 — 자막 텍스트를 기기 밖으로 내보내지 않는다는
것이 이 프로그램의 원칙입니다.

**이유.** [ARCHITECTURE.md §5.3](ARCHITECTURE.md#53-왜-기본-번역-엔진이-ctranslate2--nllb-200인가)에
표로 정리했습니다. 요약: faster-whisper 덕분에 CTranslate2가 **이미 의존성에 있고**, GPU 가속과
낮은 VRAM 사용량을 공짜로 얻으며, 빔 서치라 **결정적**이고, 문장 단위 모델이라 **자막 id가
흔들릴 수 없습니다.**

**결과.**
- 같은 영상 두 번 처리 → 같은 자막.
- 문체 지시(`translationStyle`)를 진짜로는 못 따릅니다. `polite`/`casual`은 사후 어미 정규화로
  근사할 뿐이고, 이 한계는 `translator.py`의 구현 바로 옆에 적혀 있습니다.
- **NLLB-200 가중치는 CC-BY-NC-4.0 — 비상업적 사용만 허용됩니다.** 상업적으로 쓰려면 번역
  엔진을 바꿔야 합니다. 이 사실은 모델 카탈로그(`License = "CC-BY-NC-4.0 (비상업적 사용)"`),
  모델 화면의 라이선스 열, `THIRD_PARTY_NOTICES.md`에 모두 표시됩니다.

---

## ADR-016 — 로컬 LLM은 Ollama가 아니라 llama.cpp `llama-server`

**배경.** 문체 제어가 필요한 사용자를 위한 대안 엔진이 필요합니다. 후보: Ollama, LM Studio 서버,
llama-cpp-python 바인딩, llama.cpp `llama-server` 바이너리 직접 실행.

**결정.** `tools/llama/llama-server.exe`를 워커가 직접 `subprocess.Popen`으로 띄우고,
127.0.0.1의 **OS가 준 임시 포트**에 바인딩한 뒤 `/health`를 폴링하고, OpenAI 호환
`/v1/chat/completions`로 통신합니다(`llm_translator.py`).

**이유.**
1. **재배포 가능.** llama.cpp는 MIT라 바이너리를 그대로 동봉할 수 있습니다. Ollama는 사용자가
   별도로 설치해야 하고 우리가 통제할 수 없습니다.
2. **외부 설치 불필요.** 사용자는 아무것도 따로 깔지 않습니다.
3. **상주 서비스 없음.** 작업이 시작될 때 뜨고 끝나면 죽습니다. Job Object에도 묶여 있습니다.
   자막 프로그램 하나 때문에 부팅 때부터 VRAM을 잡는 데몬이 생기지 않습니다.
4. **GGUF 양자화.** Qwen2.5 7B Instruct Q4_K_M이 약 4.6GB라 소비자용 카드에서 실용적입니다.
   `choose_gpu_layers`가 여유 VRAM에 맞춰 오프로드 레이어 수를 정하고, 3GB 미만이면 아예
   CPU로 갑니다(일부만 올려 스필하면 안 올린 것보다 느리기 때문).
5. **OpenAI 호환 엔드포인트.** 표준 형태라 클라이언트 코드가 단순합니다.

**결과.**
- `llama-server`는 **기본 배포에 포함되지 않는 선택 구성 요소**입니다. `scripts/fetch-llama.ps1`로
  받습니다. 없으면 `TRANSLATION_MODEL_NOT_FOUND`와 함께 "모델 화면에서 로컬 LLM 구성 요소를
  설치하세요"라는 한국어 메시지가 나갑니다.
- LLM은 샘플링하므로 **결정적이지 않습니다.** 같은 영상을 두 번 돌리면 문장이 조금 다를 수
  있습니다.
- LLM은 항목을 합치거나 빠뜨릴 수 있으므로, id 검증(`batching.validate`)과 **빠진 id만**
  재요청하는 루프(최대 3회)가 필수입니다. 이미 맞게 온 줄을 다시 요청하지 않는 것이 요점입니다.
- 포트 획득에는 이론적 경합이 있습니다(바인드-해제-재바인드). 고정 포트로 하면 인스턴스 두 개가
  동시에 못 뜨므로 이쪽을 택했고, 코드 주석에 그렇게 적혀 있습니다.

---

## ADR-017 — 워커 프로세스는 하나를 오래 쓰고, 작업은 한 번에 하나

**배경.** 작업마다 워커를 새로 띄우면 CPython 시작 + Whisper 모델 로드에 수십 초가 듭니다.
200개 파일이면 그것만으로 몇 시간입니다.

**결정.** `IWorkerClient`는 싱글턴이고 워커 프로세스는 앱 수명 동안 살아 있습니다. 워커 안에서는
**stdin을 메인 스레드에서 읽고, 작업은 백그라운드 스레드 하나**에서 돌립니다. 동시에 도는 작업은
언제나 하나뿐입니다.

**이유.**
- 모델 로드 비용을 파일 수만큼 곱하지 않습니다.
- stdin 읽기와 작업 실행을 분리해야 **작업이 도는 중에도 `cancel`과 `shutdown`이 처리**됩니다.
  인라인으로 돌리면 취소 명령이 파이프에 쌓인 채, 취소하려던 그 작업이 끝날 때까지 읽히지
  않습니다.
- CUDA 작업 두 개를 동시에 돌리면 같은 VRAM을 놓고 싸우다 둘 다 실패합니다. 이미 작업이
  돌고 있는데 `process`가 또 오면 `PROTOCOL_ERROR`로 거절합니다.

**결과.**
- 감시견이 필요합니다. `WorkerOptions.IdleTimeout`(기본 15분) 동안 stdout에 아무것도 안 오면
  워커가 걸린 것으로 보고 죽입니다. 15분은 어떤 정상적인 침묵보다도 깁니다 — CPU에서 도는
  large-v3조차 그보다 훨씬 자주 진행률을 보냅니다.
- 걸린 워커를 죽이면 진행 중이던 작업은 `WORKER_CRASHED`로 실패하지만, 다음 작업은 새 워커에서
  깨끗하게 시작합니다.
- `RequestAsync`는 `requestId`로 응답을 짝지웁니다. 채널이 완전히 비동기·교차 배치라 도착 순서로
  짝지을 수 없기 때문이며, `TaskCompletionSource`는 명령을 쓰기 **전에** 등록합니다.

---

## ADR-018 — Fake AI 모드는 인프로세스 파이프라인으로 구현

**배경.** GPU도 모델도 없는 기계에서 "폴더 스캔 → 큐 → 진행률 → SRT 저장"의 전 경로를 확인할 수
있어야 합니다.

**결정.** `AppSettings.FakeAiMode`(또는 `TranslationEngine == Fake`)이면
`JobProcessorSelector`가 `InProcessJobProcessor`를 고릅니다. 이 프로세서는 **가짜 엔진 두 개**
(`FakeTranscriber`, `FakeTranslationEngine`)만 가짜이고, 나머지(오디오 추출, 체크포인트, 검증,
SRT 쓰기)는 전부 진짜 코드입니다.

**중요한 세부.** 가짜 엔진들은 DI 컨테이너에서 **해석하지 않고** `new`로 직접 만듭니다
(`Infrastructure/DependencyInjection.AddInProcessPipeline`). 컨테이너에서 해석하면, 워커 계층이
진짜 `ITranscriber`를 등록한 순간 "가짜 모드"가 조용히 진짜 실행으로 바뀝니다. 그러면 사용자는
"명백히 가짜"여야 할 모드에서 표시 없는 결과물을 받게 됩니다.

**결과.**
- 파이프라인 회귀를 GPU 없이 잡을 수 있습니다.
- `JobProcessorSelector`는 `IServiceProvider`에서 지연 해석합니다. 인프로세스 파이프라인의
  의존성 그래프를 실제로 고를 때만 만들기 위해서입니다.

---

## ADR-019 — 도구 탐색에서 PATH는 언제나 마지막

**배경.** 사용자 PC에는 우리가 모르는 FFmpeg가 PATH에 있을 수 있습니다. 버전도, 빌드 옵션도,
라이선스도 다릅니다.

**결정.** `ToolLocator.Probe`의 순서는 **`tools/ffmpeg/bin` → `tools` → 앱 기준 디렉터리 →
PATH**입니다. 앞의 셋은 전부 "우리 것"이고, PATH는 최후의 수단입니다. PATH에서 찾으면
**경고 로그를 남깁니다.** Python 쪽 `ffmpeg_service.find_binary`가 같은 순서를 씁니다.

**이유.** 사용자의 임의 FFmpeg 빌드를 쓰면 재현할 수 없는 방식으로 오디오 추출이 실패합니다.
동시에, 개발자와 Linux CI는 번들 디렉터리 없이 돌 수 있어야 하므로 PATH 폴백 자체는 남깁니다.

**결과.**
- 프로덕션 로그에 "PATH의 …을(를) 사용합니다" 경고가 보이면 배포가 깨진 것입니다.
- `scripts/*.ps1`이 만드는 출력 경로는 이 탐색 순서와 정확히 일치해야 합니다.
  `fetch-ffmpeg.ps1`이 `tools/ffmpeg/bin`에, `build-worker.ps1`이 `tools/python`에,
  `fetch-llama.ps1`이 `tools/llama`에 넣는 이유입니다.

---

## ADR-020 — 테스트는 GPU·모델·네트워크 없이 돌아야 한다

**배경.** 이 프로그램의 값비싼 부분(음성 인식, 번역)은 GPU와 수 GB의 모델을 필요로 합니다.
그것을 테스트의 전제로 삼으면 CI에서 테스트가 돌지 않고, 결국 아무도 돌리지 않게 됩니다.

**결정.** 두 스위트 모두 **아무것도 설치되지 않은 컨테이너에서 도는 것**을 기준으로 만듭니다.

* **.NET** — `tests/KSubMaker.UnitTests`(도메인 규칙, 프로토콜 직렬화, 애플리케이션 서비스,
  `ErrorCodes` ↔ `errors.py` 패리티)와 `tests/KSubMaker.IntegrationTests`(퍼시스턴스 왕복,
  파이프라인, 체크포인트 재개, 워커 핸드셰이크). 두 프로젝트 모두 `KSubMaker.sln`에
  등록되어 있습니다. 규모는 단위 1,091건, 통합 94건입니다.
* **Python** — `worker/tests`의 15개 모듈, 504개 테스트. 무거운 라이브러리는 전부 함수 안에서
  지연 import 되므로 faster-whisper 하나 없이도 전부 돕니다.

외부 도구가 필요한 통합 테스트는 **실패시키지 않고 건너뜁니다.**
`ExternalTools.FfmpegSkipReason` / `PythonSkipReason`이 그 판단을 한곳에 모아 두고, ffmpeg나
Python이 없으면 해당 테스트에 skip 사유를 붙입니다. 파이썬 쪽 ffmpeg 테스트도 같은 방식입니다.

패리티 테스트(`ErrorCodeParityTests`)는 특별합니다. `errors.py`를 **실행하지 않고 정규식으로
파싱**하므로 Python이 전혀 없는 기계에서도 C#/Python 오류 코드 목록의 일치를 강제합니다.
`.csproj`가 그 파일을 테스트 어셈블리 옆으로 복사해 작업 디렉터리에 의존하지 않게 합니다.

**결과.**
- "전부 통과"와 "전부 실행"은 다릅니다. `dotnet test` 결과의 **skipped 개수를 함께** 보세요.
  ffmpeg가 없는 CI에서는 미디어 관련 통합 테스트가 조용히 빠집니다.
- 진짜 GPU 경로는 어떤 자동 스위트로도 검증되지 않습니다. 그것이
  `scripts/smoke-gpu.ps1`이 **별도로 직접 실행하는** 스크립트로 존재하는 이유입니다.
- `scripts/run-tests.ps1`은 두 스위트를 모두 호출하고, 방어적으로 "솔루션에 테스트 프로젝트가
  하나도 없으면" 경고합니다(`-RequireDotnetTests`로 실패로 승격 가능).

---

## ADR-021 — 로깅은 MEL 인터페이스 + Serilog 싱크

**배경.** 파일 로그가 필요하고(사용자가 "로그 보기"로 열 수 있어야 함), 로그 수준을 재시작 없이
바꿀 수 있어야 하며, 로깅 라이브러리가 코드 전체에 번지면 안 됩니다.

**결정.** 코드 전체는 `Microsoft.Extensions.Logging`의 `ILogger<T>`만 씁니다. Serilog는
**싱크 구현으로만** 존재하며 `SerilogSetup`이 유일한 접점입니다. 수준 변경은
`LoggingLevelSwitch` 하나를 돌려서 합니다 — 로거를 다시 만들면 열려 있는 파일 핸들이 닫히고
버퍼가 날아갑니다.

**결과.**
- 하루 단위 롤링, 파일당 20MB, 14개 보관, 2초마다 디스크 플러시(크래시 직전 줄이 가장 중요한
  줄이므로).
- EF Core는 기본적으로 모든 SQL을 Information으로 떠드므로 `Microsoft.EntityFrameworkCore`를
  Warning으로 눌러 둡니다.
- `AppSettings.MaskPathsInLogs`를 켜면 `PathMaskingEnricher`가 디렉터리 성분을 `***`로 바꿉니다.
  로그를 공유할 때를 위한 것입니다.

---

## ADR-022 — 모델 무결성은 SHA-256 매니페스트로, 검증은 오프라인

**배경.** 모델은 수 GB이고 다운로드가 중단될 수 있습니다. 손상된 모델은 로드 실패나 이상한
결과로 나타나는데, 원인을 사용자가 알기 어렵습니다.

**결정.** 파일마다 `.part`에 Range 요청으로 받고, SHA-256이 맞을 때만 최종 이름으로 옮깁니다.
완료 후 모델 디렉터리에 `.ksubmaker-manifest.json`을 씁니다(파일별 상대 경로·크기·SHA-256).
**검증(`VerifyAsync`)은 매니페스트와 디스크만 읽습니다 — 네트워크가 전혀 필요 없습니다.**

**이유.**
- `.part` 파일을 일부러 남깁니다. 그것이 다음 시도를 "이어받기"로 만드는 유일한 근거입니다.
- Hugging Face 트리 API는 **LFS 항목에만** 진짜 SHA-256을 공개합니다. 일반 git blob의 `oid`는
  git 오브젝트 헤더까지 포함한 SHA-1이라 파일 해시와 비교할 수 없습니다. 그래서 작은
  설정/토크나이저 파일은 원격 비교 없이 **로컬에서 해시해 매니페스트에 기록**합니다. 이것은
  실수가 아니라 알려진 한계이며, 검증할 가치가 있는 큰 가중치 파일은 전부 LFS입니다.
- 오프라인 검증이 가능해야 "인터넷 없는 PC에 수동 설치"라는 시나리오가 성립합니다.

**결과.**
- 원격 digest가 없는 `.part`를 이어받는 것은 위험합니다(원격 내용이 바뀌었을 수 있음).
  그런 경우 코드가 이어받기를 포기하고 처음부터 받습니다.
- 자세한 절차는 [`MODEL_MANAGEMENT.md`](MODEL_MANAGEMENT.md)에 있습니다.

---

## ADR-023 — 단일 인스턴스는 `Global\` 뮤텍스, 단 실패하면 열어 준다

**배경.** 인스턴스가 둘이면 같은 SQLite 파일과 같은 모델 디렉터리를 놓고 싸웁니다.

**결정.** `new Mutex(true, @"Global\KSubMaker", out var createdNew)`. `createdNew`가 false면
한국어 안내 후 종료합니다. **다만 `UnauthorizedAccessException`이나 다른 예외가 나면 가드가
"열림"으로 실패합니다** — 두 번째 인스턴스가 뜨는 것보다 앱이 아예 안 뜨는 쪽이 더 나쁩니다.

**이유.** `Global\` 네임스페이스를 쓴 것은 터미널 서비스 세션이 여러 개인 환경까지 막기
위해서입니다. 잠긴 정책의 환경에서는 `Global\` 접근이 거부될 수 있는데, 그때 앱이 안 뜨면
사용자는 원인을 알 방법이 없습니다.

**결과.**
- 정상 환경에서 두 번째 실행은 안내 메시지 후 종료 코드 1로 끝납니다.
- 예외적인 환경에서는 인스턴스가 둘 뜰 수 있습니다. SQLite의 `busy_timeout`(30초)과 WAL이
  최악의 경우를 완화합니다.

---

## ADR-024 — 번역 전에 세그먼트를 자른다

**배경.** Whisper는 30초에 가까운 긴 세그먼트를 만들 수 있습니다. 자막 한 큐로는 너무 깁니다.

**결정.** `SegmentSplitter.Split`(C#) / `subtitle_postprocessor.split_segments`(Python)이
**번역 이전에** 90자·최대 큐 길이 기준으로 자릅니다. 자를 때 Whisper의 **단어 타임스탬프**를
써서 각 조각에 실제 음성에서 유도된 시각을 줍니다. 자른 뒤 id를 1부터 다시 매깁니다.

**이유.** 한국어로 번역된 뒤에는 단어 정렬 정보가 없습니다. 그때 자르면 시간을 문자 수 비율로
보간할 수밖에 없고, 싱크가 미묘하게 어긋납니다. 단어 타임스탬프를 켜 두는 것(ADR-010)이
값어치를 하는 지점이 정확히 여기입니다.

**결과.**
- 문장 끝 구두점 뒤에서 자르기를 선호하는 휴리스틱이 들어 있어(예산의 절반을 넘겼을 때)
  기계적 길이 절단보다 훨씬 자연스러운 큐가 나옵니다.
- 단어 타임스탬프가 없으면(옵션을 껐거나 모델이 안 주면) 비례 배분으로 폴백합니다.

---

## ADR-025 — 한국어 줄바꿈은 조사로 줄을 시작하지 않는다

**배경.** 자막 한 줄은 22자(기본값)로 제한되고 큐당 최대 2줄입니다. 단순히 글자 수로 자르면
"학교에서\n는 만났다" 같은 줄이 나옵니다. 글자 수 예산은 지켰지만 읽을 수 없는 한국어입니다.

**결정.** `KoreanLineBreaker`는 후보 분리점을 **점수화**합니다. 조사(은/는/이/가/을/를/에서/
에게/까지/부터 …)와 의존명사(것/거/수/때/중/등 …)로 줄이 시작되는 것을 금지하고, 문장 부호
뒤 분리를 선호하며, 여는 부호 앞에서는 자르지 않습니다.

**결과.**
- 줄바꿈 품질이 눈에 띄게 좋아집니다.
- 목록이 완전하지 않습니다. 새 사례가 나오면 `BadLineStarts`에 추가하면 되고, 순수 함수라
  테스트가 쉽습니다.

---

## ADR-026 — 오류 코드는 두 언어에 걸친 하나의 목록

**배경.** 실패는 Python에서 나고 UI에서 보여야 합니다. 예외 메시지를 그대로 보여 주면
사용자는 아무것도 할 수 없습니다.

**결정.** 22개의 안정된 문자열 코드를 `KSubMaker.Domain/Errors/ErrorCodes.cs`가 정의하고
`worker/ksubmaker_worker/errors.py`가 **같은 값으로** 거울처럼 갖습니다. 각 코드는
`UserFacingErrors.Describe`(C#)와 `errors.DEFAULT_MESSAGES`(Python)에서 **행동 가능한 한국어
한 문장**으로 번역됩니다. 기술적 세부는 `detail` 필드에 담겨 로그 파일로만 갑니다.

**결과.**
- UI에는 스택 트레이스가 절대 나오지 않습니다. "로그 보기" 버튼이 파일을 엽니다.
- 자동 재시도 가능 여부(`IsAutoRetryable` / `RECOVERABLE`)도 같은 집합을 공유하므로 호스트와
  워커가 재시도 가치를 두고 의견이 갈리지 않습니다.
- 두 목록이 어긋나면 안 됩니다. `tests/KSubMaker.UnitTests/Parity/ErrorCodeParityTests.cs`가
  이를 강제합니다. `errors.py`를 실행하지 않고 정규식으로 파싱하므로 Python이 없는 기계에서도
  동작합니다. **코드를 추가할 때는 반드시 양쪽을 함께 고치세요** — 한쪽만 고치면 이 테스트가
  빌드를 세웁니다.

---

## ADR-027 — 자막 파일 쓰기는 원자적으로, 그리고 여유 공간을 먼저 본다

**배경.** 마지막 단계는 한 시간 걸린 작업의 결과를 **사용자의 미디어 폴더**에 씁니다. 실패가
기존 파일을 파괴하면 안 됩니다.

**결정.** `AtomicSubtitleWriter`의 순서: ① 충돌 정책 적용 → ② 여유 공간 확인(50MB 미만이면
`DISK_SPACE_LOW`로 거부) → ③ **같은 디렉터리**의 임시 파일에 쓰기 → ④ 이동.

**이유.** 같은 디렉터리에 임시 파일을 두는 것이 중요합니다. 다른 볼륨이면 이동이 복사가 되어
원자성이 사라집니다. 여유 공간을 먼저 보는 것은, SRT가 작더라도 그렇게 꽉 찬 볼륨은 임시 쓰기도
중간에 실패하기 때문이며, 원시 디스크 풀 오류보다 명확한 한국어 메시지가 낫습니다.

**결과.** 어떤 실패 시나리오에서도 기존 자막이 손상되지 않습니다. Python 쪽
`subtitle_writer.write_subtitle_file`도 같은 순서(temp → `os.replace`)를 씁니다.

---

## ADR-028 — CUDA 사용 가능 여부는 워커가 정본이다 (단, 워커를 미리 띄우지는 않는다)

**배경.** `WindowsHardwareDetector`는 `nvcuda.dll`을 로드할 수 있는지와 `CUDA_PATH`가 있는지로
CUDA를 판정합니다. 그것은 **드라이버가 있다**는 증명이지, **CTranslate2가 이 기계에서 디바이스를
열 수 있다**는 증명이 아닙니다. 그 둘이 갈리는 기계는 드물지 않습니다(툴킷과 드라이버 주 버전
불일치, cuDNN 누락, GPU 패스스루가 없는 WSL). 그런 기계에서는 GPU 모델을 권하고, 사용자는 한
시간 뒤에 실패를 봅니다.

반대편에는 대가가 있습니다. 정확한 답을 얻으려면 워커 프로세스를 띄우고 `ctranslate2`와 `torch`를
import해야 하는데, 그것은 콜드 스타트에서 수 초입니다. 프로그램을 켜기만 한 사용자에게 그 비용을
물릴 수는 없습니다.

**결정.** 로컬 판정을 먼저 하고, 워커의 답으로 **나중에 덮어씁니다.**

* 로컬 감지(`IHardwareDetector`)는 언제나 즉시 돕니다. 시작할 때 워커는 뜨지 않습니다.
* `IWorkerHardwareProbe`가 `detectHardware`를 보내는 시점은 둘뿐입니다.
  ① 워커가 **다른 이유로** 막 떠 있을 때 — 첫 작업이 워커를 띄운 직후, `process`를 보내기 전.
  ② 설정 화면의 **새로 고침**을 눌렀을 때. 이때만 워커를 새로 띄웁니다.
* 답이 오면 `HardwareProfile.MergeWorkerReport`가 접어 넣고, 권장 설정을 다시 계산하고,
  `HardwareService.ProfileChanged`를 한 번 더 발생시킵니다.

**이유.** 병합 규칙은 "각 값을 가장 잘 아는 쪽이 이긴다"입니다. `cudaAvailable`과 GPU별 여유
VRAM은 모델을 올릴 프로세스가 정본이고, GPU 이름·총 VRAM·CPU·RAM·디스크는 호스트가 이미
정확히 알고 있습니다. 워커가 CUDA를 쓸 수 있다고 답하면 로컬이 남긴 CUDA 경고
(`HardwareWarnings.RetractedWhenCudaWorks`)는 **철회**합니다 — 방금 뒤집힌 판정을 설정 화면에
남겨 두면 자기모순입니다.

`detectHardware`를 작업과 **동시에** 보내지 않는 것도 의도적입니다. 워커는 stdin을 메인
스레드에서 읽으므로, torch import에 10초가 걸리는 `detectHardware`가 그 10초 동안 `cancel`을
읽지 못하게 만듭니다. 작업 앞에 순서대로 두면 벽시계 비용은 같고 취소 응답성은 지켜집니다.

**결과.**
- 시작 직후 상태 표시줄의 CUDA 표시는 **추정치**입니다. 첫 작업이 시작되면 확정값으로 바뀝니다.
  이것은 문서화된 동작이며 README의 제한사항에도 적혀 있습니다.
- 워커가 답하지 못해도(다운, 타임아웃, 오류) 아무것도 실패하지 않습니다. 로컬 프로필을 그대로
  씁니다. 하드웨어 정보는 보강이지 전제 조건이 아닙니다.
- 병합은 순수 함수라서 워커 없이 단위 테스트됩니다(`HardwareProfileMergeTests`).

> **보강 (→ [ADR-030](#adr-030--cuda-런타임-라이브러리를-임베디드-파이썬에-함께-넣는다)).**
> 이 ADR이 정한 "워커가 정본"은 그대로입니다. 다만 워커가 원래 던지던 질문
> (`ctranslate2.get_cuda_device_count() > 0`)이 **틀린 질문**이었습니다. 그것은 드라이버만
> 있으면 참이고, 모델 로드에 필요한 cuBLAS 12 / cuDNN 9의 존재는 증명하지 못합니다. 프로토콜
> 1.2부터 워커는 디바이스 존재와 지원 라이브러리 로드를 따로 확인하고 그 논리곱을
> `cudaAvailable`로 보고합니다.

---

## ADR-029 — 자막 원본은 파일 단위로 덮어쓸 수 있다

**배경.** 컨테이너의 자막 트랙 언어 메타데이터는 자주 비어 있거나(`und`) 틀립니다. 전역 설정
하나(`ExistingSubtitlePolicy`)로 "내장 자막을 쓴다"를 켜면, 어떤 파일에서는 맞고 어떤 파일에서는
일본어 트랙을 영어로 번역합니다. `AskPerFile`이라는 선택지는 원래부터 있었지만 아무것도 묻지
않았습니다.

**결정.** `Job`에 파일 단위 override를 저장합니다: `SourceOverride`
(`None`/`Audio`/`EmbeddedSubtitle`), `SelectedAudioTrackIndex`, `SelectedSubtitleTrackIndex`,
`SelectedSubtitleLanguage`. 마이그레이션은 `AddJobSourceOverride`. 기본값은 `None`이며 그것이
MVP 핵심 경로(영상 음성 → Whisper → 번역 → ko.srt)입니다.

**이유.**
- **override가 전역 정책을 이깁니다.** 사용자는 실제 트랙 목록을 보고 골랐습니다. 컨테이너
  태그나 전역 설정보다 나은 정보입니다.
- **DB에 저장합니다.** 재시작 후에도 유지되어야 하고, 그리드가 매 파일을 다시 프로브하지 않고
  현재 선택을 보여 줄 수 있어야 합니다.
- **언어를 따로 저장합니다.** 컨테이너 태그를 그대로 믿을 수 없다는 것이 이 기능의 존재
  이유이므로, 사용자가 확인한 값을 별도 열에 둡니다. `und`와 빈 문자열은 null로 정규화합니다 —
  워커가 "und"를 언어로 취급하면 안 됩니다.
- **트랙 목록은 저장하지 않습니다.** 파일당 열 몇 개의 문자열을 5,000건 큐에 저장해서 대화상자
  하나를 여는 것은 나쁜 거래입니다. 로컬 파일 ffprobe는 밀리초 단위이므로 필요할 때 다시
  프로브합니다.

**결과.**
- `ProcessCommand.subtitleLanguage`(프로토콜 1.1)가 추가되었습니다. 없으면 워커는 영어로
  가정합니다 — 그 폴백은 그대로 남겨 두었습니다.
- 실행 중인 작업은 override 변경을 거부합니다. 워커에 이미 옛 값으로 만든 `process` 명령이
  가 있으므로, 바꾸면 그리드와 실제 산출물이 어긋납니다.
- 인프로세스(Fake AI) 파이프라인은 override를 무시하고 언제나 오디오를 씁니다. 문서화된
  제한사항입니다.
---

## ADR-030 — CUDA 런타임 라이브러리를 임베디드 파이썬에 함께 넣는다

**배경.** 실제 사용자 로그(RTX 3080 Ti, 드라이버 CUDA 13.1, Windows):

```
[INF] WorkerHardwareProbe: worker 하드웨어 확인: CUDA=true (13.1), GPU 1개
[ERR] WorkerJobProcessor: worker 오류: TRANSCRIPTION_FAILED 음성 인식을 시작하지 못했습니다.
      RuntimeError('Library cublas64_12.dll is not found or cannot be loaded')
```

`ctranslate2 >= 4.5`는 **cuBLAS(CUDA 12)** 와 **cuDNN 9** 에 링크되어 있습니다. 두 라이브러리는

* `ctranslate2` 휠에 들어 있지 않고,
* NVIDIA **드라이버**도 제공하지 않습니다. CUDA *툴킷* 구성 요소이기 때문입니다.

드라이버가 "CUDA 13.1"을 보고한다는 사실은 `cublas64_12.dll`의 존재와 아무 관계가 없습니다.
드라이버 버전과 툴킷 라이브러리는 서로 다른 배포물입니다. 그래서 최신 드라이버를 깔아 둔 최신
GPU 기계에서 GPU 작업이 100% 실패했고, 실패 시점은 사용자가 폴더를 고르고 큐를 돌리기 시작한
**한 시간 뒤**였습니다.

선택지는 셋이었습니다.

| 안 | 내용 | 문제 |
| --- | --- | --- |
| A | 사용자에게 **CUDA 툴킷 설치**를 요구 | 3 GB 설치 프로그램, 관리자 권한, 버전 선택. 대상 사용자는 개발자가 아닙니다. README의 "설치할 것이 없습니다"라는 약속과 정면으로 충돌합니다 |
| B | GPU를 포기하고 **CPU 전용**으로 배포 | 영상 길이의 5~15배. 이 프로그램의 존재 이유가 사라집니다 |
| C | **PyPI 휠을 배포본에 함께 넣기** | 디스크 약 1.8 GB 증가 (다운로드 약 1.2 GB) |

**결정. C입니다.** `scripts/build-worker.ps1`이 워커를 설치한 직후
`nvidia-cublas-cu12` 와 `nvidia-cudnn-cu12` 를 같은 임베디드 런타임에 설치합니다.
`-SkipCudaLibraries`로 CPU 전용 배포본을 만들 수 있고, 스크립트는 늘어난 용량을 보고합니다.

**주 버전을 고정합니다** (`>=12.9,<13`, `>=9.24,<10`). 파일 이름에 주 버전이 박혀 있기
때문입니다 — cuDNN 10 휠은 **깨끗하게 설치된 다음 로드에서만** 실패하고, 그것이 가장
진단하기 어려운 형태의 고장입니다.

**설치만으로는 부족합니다.** pip는 DLL을 `site-packages/nvidia/<구성요소>/bin`에 두는데, 그
폴더는 Windows DLL 검색 경로가 아닙니다. CPython 3.8부터 인터프리터가
`SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)`를 호출하므로 `PATH`에 넣어도
소용이 없습니다. 유일한 정식 경로가 `os.add_dll_directory()`이며,
`worker/ksubmaker_worker/cuda_setup.py`가 워커 진입점에서 — **`ctranslate2`를 import하는
어떤 코드보다 먼저** — 이를 수행합니다. 의존 DLL 해석은 확장 모듈이 로드되는 순간 한 번
일어나고 실패해도 다시 시도되지 않으므로, "나중에 등록"은 "등록하지 않음"과 같습니다.

**왜 `worker/pyproject.toml`의 의존성이 아닌가.** 두 휠은 플랫폼 전용입니다. 의존성으로 넣으면
개발자 기계와 Linux CI의 `pip install -e "worker[dev]"`가 깨지거나 불필요하게 1 GB를 받습니다.
배포본을 만드는 스크립트만 설치하는 것이 맞습니다.

**결과.**
- 배포본이 약 1.8 GB 커집니다(cuBLAS 736 MiB + cuDNN 1,071 MiB, 2026-08 기준 실측).
  대부분은 `cublasLt64_12.dll`(638 MiB)과 `cudnn_engines_precompiled64_9.dll`(522 MiB)입니다.
  README 요구 사항 표의 디스크 항목에 반영되어 있습니다.
- 두 휠은 NVIDIA 독점 EULA(재배포 허용)입니다. `THIRD_PARTY_NOTICES.md`에 항목이 있습니다.
- 하드웨어 감지가 정직해졌습니다. `cudaAvailable`은 이제 "디바이스가 있다 **그리고**
  라이브러리가 로드된다"이고, 디바이스만 있는 상태는 `cudaDeviceDetected=true,
  cudaLibrariesAvailable=false`로 구분해 보고합니다(프로토콜 1.2). ADR-028의 "워커가 정본"
  원칙은 그대로이며, **워커가 답하는 질문이 더 정확해진 것**입니다.
- 로드 검사는 Windows 전용입니다. 막으려는 고장이 Windows DLL 검색 경로 문제이고, Linux 휠은
  `RPATH`로 같은 라이브러리를 찾습니다. 다른 플랫폼에서 검사를 돌리면 **거짓 음성만** 만들 수
  있습니다.
- 새 오류 코드 `CUDA_LIBRARY_MISSING`이 생겼습니다. **자동 재시도 대상이 아닙니다** — 재시도는
  같은 폴더에서 같은 없는 DLL을 다시 찾을 뿐이고, 그 대가로 모델 로드 한 번을 통째로 씁니다.
- 실제 GPU에서 DLL이 로드되는지는 **자동 테스트로 검증할 수 없습니다.** Linux CI에서 검증되는
  것은 디렉터리 탐색, 등록 호출, 비-Windows no-op, 오류 문자열 분류까지입니다. 나머지는
  `scripts/smoke-gpu.ps1`과 사람의 몫입니다(ADR-020과 같은 이유).

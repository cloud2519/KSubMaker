<#
.SYNOPSIS
    실제 NVIDIA GPU 에서 KSubMaker AI 워커를 끝까지 돌려 보는 스모크 테스트입니다.
    CUDA GPU 가 없으면 실행을 거부합니다.

.DESCRIPTION
    자동화된 테스트 스위트(run-tests.ps1)와 **별도로 직접 실행하는** 검증입니다.
    단위 테스트는 GPU 없이 도는 것이 목표이므로 CUDA 경로를 전혀 건드리지 않습니다.
    이 스크립트는 그 빈 곳을 메웁니다.

    수행 순서:

      0. nvidia-smi 로 NVIDIA GPU 존재를 확인합니다. 없으면 **즉시 거부하고 종료**합니다.
      1. 워커를 띄워 detectHardware 명령을 보내고 hardware 이벤트를 받습니다.
         cudaAvailable 이 false 면 거부합니다 (-AllowCpu 로 무시할 수 있습니다).
      2. 짧은 시험용 클립을 ffmpeg 로 만듭니다 (-SampleVideo 로 실제 영상을 줄 수 있습니다).
      3. 실제 Whisper 모델로 process 명령을 실행하고 이벤트를 지켜봅니다.
      4. 단계별 소요 시간, 인식 속도(미디어 초/실시간 초), 총 시간을 보고합니다.

    합성 클립에 대한 정직한 안내:
      ffmpeg 로 만드는 클립은 사인파 톤이며 사람의 말이 아닙니다. Whisper 가 세그먼트를
      하나도 만들지 못하면 워커는 TRANSCRIPTION_FAILED 로 끝납니다. 그 경우에도 **모델 로드와
      CUDA 실행 경로는 검증된 것**이므로 이 스크립트는 경고로 처리하고 측정한 시간을
      보고합니다. 의미 있는 정확도/속도 측정을 원하면 -SampleVideo 로 사람 말이 들어간
      짧은 영상을 지정하세요.

.PARAMETER SampleVideo
    사용할 영상 파일. 생략하면 ffmpeg 로 합성 클립을 만듭니다.

.PARAMETER DurationSeconds
    합성 클립 길이(초). 기본값 30.

.PARAMETER WhisperModel
    사용할 Whisper 모델 id. 기본값 'whisper-small'. 미리 내려받아 두어야 합니다.

.PARAMETER ComputeType
    연산 정밀도. 기본값 'float16'. float32 / float16 / bfloat16 / int8_float16 / int8.

.PARAMETER TranslationEngine
    'fake'(기본) 는 번역 모델 없이 인식 성능만 봅니다.
    'local-translation' 은 NLLB 모델까지 실제로 사용해 전 구간을 측정합니다.

.PARAMETER TranslationModel
    -TranslationEngine 이 local-translation 일 때 쓸 모델 id. 기본값 'auto'.

.PARAMETER BeamSize
    빔 크기. 기본값 5.

.PARAMETER VadFilter
    VAD 필터를 켭니다. 합성 클립에서는 모든 구간이 걸러지므로 기본값은 꺼짐입니다.

.PARAMETER ModelsDirectory
    모델 루트. 생략하면 %LOCALAPPDATA%\KSubMaker\models.

.PARAMETER PythonExe
    Python 실행 파일. 생략하면 tools\python\python.exe -> %KSUBMAKER_WORKER_PYTHON% -> PATH.

.PARAMETER WorkDirectory
    작업 파일을 둘 폴더. 생략하면 임시 폴더를 만들고 끝나면 지웁니다.

.PARAMETER KeepArtifacts
    작업 폴더(클립, 체크포인트, 결과 SRT)를 남깁니다.

.PARAMETER TimeoutSeconds
    process 명령 전체 제한 시간. 기본값 1800(30분).

.PARAMETER AllowCpu
    CUDA 를 쓸 수 없어도 계속 진행합니다. **GPU 스모크 테스트의 목적에는 어긋납니다.**
    파이프라인 자체를 확인할 때만 쓰세요.

.EXAMPLE
    .\scripts\smoke-gpu.ps1

    합성 클립으로 GPU 경로를 확인합니다.

.EXAMPLE
    .\scripts\smoke-gpu.ps1 -SampleVideo 'D:\clips\interview-30s.mp4' -WhisperModel whisper-large-v3-turbo

    실제 영상으로 의미 있는 속도를 측정합니다.

.EXAMPLE
    .\scripts\smoke-gpu.ps1 -SampleVideo 'D:\clips\interview-30s.mp4' -TranslationEngine local-translation -KeepArtifacts

    번역까지 포함한 전 구간을 측정하고 결과 SRT 를 남깁니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
    -WhatIf 는 제공하지 않습니다. 이 스크립트의 목적 자체가 실제로 실행해 보는 것입니다.
    (작업 파일은 임시 폴더에만 쓰고 원본은 건드리지 않습니다.)
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $SampleVideo,
    [int]    $DurationSeconds = 30,
    [string] $WhisperModel = 'whisper-small',
    [ValidateSet('float32', 'float16', 'bfloat16', 'int8_float16', 'int8')]
    [string] $ComputeType = 'float16',
    [ValidateSet('fake', 'local-translation', 'local-llm')]
    [string] $TranslationEngine = 'fake',
    [string] $TranslationModel = 'auto',
    [int]    $BeamSize = 5,
    [switch] $VadFilter,
    [string] $ModelsDirectory,
    [string] $PythonExe,
    [string] $WorkDirectory,
    [switch] $KeepArtifacts,
    [int]    $TimeoutSeconds = 1800,
    [switch] $AllowCpu
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

# ---------------------------------------------------------------------------
# 워커와 대화하는 최소한의 클라이언트
# ---------------------------------------------------------------------------

function Start-KsmWorker {
    <#
    .SYNOPSIS
        워커 프로세스를 띄우고 ready 이벤트까지 기다립니다.
    .OUTPUTS
        PSCustomObject: Process, StdErrTask, Ready
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $PythonExe,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [hashtable] $Environment,
        [int] $ReadyTimeoutSeconds = 120
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $PythonExe
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $WorkingDirectory

    # 호스트(WorkerProcessClient)와 동일하게 BOM 없는 UTF-8 을 씁니다.
    # StandardInputEncoding 은 .NET Framework 에 없으므로 헬퍼가 알아서 건너뜁니다.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    Set-KsmStandardInputEncoding -StartInfo $startInfo -Encoding $utf8NoBom
    $startInfo.StandardOutputEncoding = $utf8NoBom
    $startInfo.StandardErrorEncoding  = $utf8NoBom

    Set-KsmProcessArguments -StartInfo $startInfo -ArgumentList @('-m', 'ksubmaker_worker')

    if ($Environment) {
        foreach ($key in $Environment.Keys) {
            $startInfo.Environment[[string] $key] = [string] $Environment[$key]
        }
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void] $process.Start()

    # stderr 를 계속 비워 주지 않으면 파이프가 가득 차서 워커가 멈춥니다.
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $ready = Read-KsmEvent -Process $process -TimeoutSeconds $ReadyTimeoutSeconds

    if ($null -eq $ready -or $ready.type -ne 'ready') {
        # 여기서 stdout 만 보고하면 진단이 불가능합니다. 워커가 부팅에 실패하면 원인은 거의
        # 항상 stderr 의 파이썬 트레이스백이거나 종료 코드입니다. 둘 다 보여 줍니다.
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
        try { [void] $process.WaitForExit(5000) } catch { }

        $stderrText = ''
        try {
            if ($stderrTask.Wait(5000)) { $stderrText = [string] $stderrTask.Result }
        }
        catch {
            $stderrText = "(stderr 를 읽지 못했습니다: $($_.Exception.Message))"
        }

        $exitCode = '실행 중'
        try { if ($process.HasExited) { $exitCode = [string] $process.ExitCode } } catch { }

        $lines = New-Object System.Collections.Generic.List[string]
        [void] $lines.Add('워커가 ready 이벤트를 보내지 않았습니다.')
        [void] $lines.Add("  실행 파일 : $PythonExe")
        [void] $lines.Add("  종료 코드 : $exitCode")

        if ($null -ne $ready) {
            [void] $lines.Add("  받은 stdout: $($ready | ConvertTo-Json -Compress)")
        }
        else {
            [void] $lines.Add('  stdout    : (아무것도 오지 않았습니다)')
        }

        if ([string]::IsNullOrWhiteSpace($stderrText)) {
            [void] $lines.Add('  stderr    : (비어 있음)')
            [void] $lines.Add('')
            [void] $lines.Add('stdout 과 stderr 가 모두 비어 있고 즉시 종료했다면, 그 실행 파일은 실제 Python 이')
            [void] $lines.Add('아닐 가능성이 높습니다. Microsoft Store 앱 실행 별칭 스텁이 대표적입니다.')
            [void] $lines.Add('.\scripts\build-worker.ps1 로 임베디드 런타임을 만드는 것이 가장 확실합니다.')
        }
        else {
            $tail = @($stderrText -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 30)
            [void] $lines.Add('  stderr (마지막 30줄):')
            foreach ($line in $tail) { [void] $lines.Add("    $line") }
        }

        throw ($lines -join [Environment]::NewLine)
    }

    return [pscustomobject]@{
        Process    = $process
        StdErrTask = $stderrTask
        Ready      = $ready
    }
}

function Send-KsmCommand {
    <#
    .SYNOPSIS
        워커 stdin 에 명령 한 줄을 씁니다.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [Parameter(Mandatory)][hashtable] $Command
    )

    $json = $Command | ConvertTo-Json -Depth 10 -Compress
    $Process.StandardInput.Write($json + "`n")
    $Process.StandardInput.Flush()
}

function Read-KsmEvent {
    <#
    .SYNOPSIS
        워커 stdout 에서 이벤트 하나를 읽습니다. 제한 시간 안에 오지 않으면 $null.
    .DESCRIPTION
        JSON 이 아닌 줄은 경고만 남기고 건너뜁니다. 호스트의
        WorkerProtocolSerializer.DeserializeEvent 와 같은 태도입니다.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [int] $TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $task = $Process.StandardOutput.ReadLineAsync()

        $remainingMs = [int][math]::Max(250, ($deadline - (Get-Date)).TotalMilliseconds)
        if (-not $task.Wait($remainingMs)) {
            if ($Process.HasExited) {
                return $null
            }
            continue
        }

        $line = $task.Result
        if ($null -eq $line) {
            return $null   # stdout 이 닫혔습니다 (프로세스 종료).
        }

        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) { continue }

        if ($trimmed[0] -ne '{') {
            Write-KsmWarn "stdout 에 JSON 이 아닌 줄이 있습니다 (무시): $trimmed"
            continue
        }

        try {
            return ($trimmed | ConvertFrom-Json)
        }
        catch {
            Write-KsmWarn "stdout 의 줄을 해석하지 못했습니다 (무시): $trimmed"
            continue
        }
    }

    return $null
}

function Stop-KsmWorker {
    <#
    .SYNOPSIS
        shutdown 명령을 보내고, 안 끝나면 프로세스 트리를 죽입니다.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Worker,
        [int] $TimeoutSeconds = 30
    )

    $process = $Worker.Process

    try {
        if (-not $process.HasExited) {
            Send-KsmCommand -Process $process -Command @{
                command         = 'shutdown'
                requestId       = 'smoke-shutdown'
                protocolVersion = '1.2'
            }
            $process.StandardInput.Close()
            [void] $process.WaitForExit($TimeoutSeconds * 1000)
        }
    }
    catch {
        Write-KsmWarn "정상 종료에 실패했습니다: $($_.Exception.Message)"
    }
    finally {
        try {
            if (-not $process.HasExited) {
                Write-KsmWarn '워커가 응답하지 않아 강제 종료합니다.'
                $process.Kill()
                [void] $process.WaitForExit(5000)
            }
        }
        catch { }
    }
}

function Get-KsmWorkerStdErr {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Worker)

    try {
        if ($Worker.StdErrTask.Wait(3000)) {
            return $Worker.StdErrTask.Result
        }
    }
    catch { }
    return ''
}

# ---------------------------------------------------------------------------
# 본문
# ---------------------------------------------------------------------------

$worker = $null
$createdWorkDir = $false

try {
    $repoRoot = Get-KsmRepoRoot
    $toolsDir = Join-Path $repoRoot 'tools'

    Write-KsmStep 'KSubMaker GPU 스모크 테스트'
    Write-KsmNote '이 스크립트는 run-tests.ps1 과 별도로 직접 실행하는 실제 GPU 검증입니다.'

    # -----------------------------------------------------------------------
    # 0. GPU 존재 확인 — 없으면 거부
    # -----------------------------------------------------------------------
    Write-KsmStep '0. NVIDIA GPU 확인'

    $nvidiaSmi = Get-Command -Name 'nvidia-smi.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nvidiaSmi) {
        $nvidiaSmi = Get-Command -Name 'nvidia-smi' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $nvidiaSmi) {
        # WindowsHardwareDetector 와 같은 폴백 위치입니다. 환경 변수가 비어 있을 수 있으므로
        # (비-Windows 호스트, 잠긴 환경) Join-Path 전에 반드시 확인합니다.
        $fallbacks = New-Object System.Collections.Generic.List[string]

        if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
            $fallbacks.Add((Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'))
        }
        if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
            $fallbacks.Add((Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'))
        }

        foreach ($candidate in $fallbacks) {
            if (Test-Path -LiteralPath $candidate) {
                $nvidiaSmi = Get-Item -LiteralPath $candidate
                break
            }
        }
    }

    if (-not $nvidiaSmi) {
        if (-not $AllowCpu) {
            throw @(
                'NVIDIA GPU 를 찾지 못했습니다. 이 스크립트는 실행하지 않습니다.'
                ''
                'smoke-gpu.ps1 은 CUDA 경로를 검증하는 것이 목적입니다. CPU 로 돌리면'
                '아무것도 검증하지 못하면서 몇 분에서 몇십 분이 걸립니다.'
                ''
                'nvidia-smi 를 찾지 못했습니다. 확인할 것:'
                '  - NVIDIA 드라이버가 설치되어 있는지'
                '  - 명령 프롬프트에서 nvidia-smi 가 실행되는지'
                '  - 원격 데스크톱 세션이 GPU 를 가리고 있지 않은지'
                ''
                '파이프라인만 확인하고 싶다면 -AllowCpu 를 지정하거나,'
                'KSubMaker 설정의 "Fake AI 모드" 를 쓰세요.'
            ) -join [Environment]::NewLine
        }
        Write-KsmWarn '-AllowCpu 가 지정되어 GPU 없이 계속합니다. GPU 검증은 이루어지지 않습니다.'
    }
    else {
        $smi = Invoke-KsmProcess -FilePath $nvidiaSmi.Source `
            -ArgumentList @('--query-gpu=index,name,memory.total,memory.free,driver_version,compute_cap',
                            '--format=csv,noheader,nounits') `
            -TimeoutSeconds 30

        if ($smi.ExitCode -ne 0 -and -not $AllowCpu) {
            throw "nvidia-smi 가 종료 코드 $($smi.ExitCode) 로 끝났습니다.`n$($smi.StandardError)"
        }

        $gpuLines = @($smi.StandardOutput -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
        if ($gpuLines.Count -eq 0 -and -not $AllowCpu) {
            throw 'nvidia-smi 가 GPU 를 보고하지 않았습니다. 이 스크립트는 실행하지 않습니다.'
        }

        foreach ($line in $gpuLines) {
            $fields = $line -split ','
            if ($fields.Count -ge 4) {
                Write-KsmOk ("GPU #{0}: {1} — VRAM {2} MiB (여유 {3} MiB), 드라이버 {4}" -f `
                    $fields[0].Trim(), $fields[1].Trim(), $fields[2].Trim(), $fields[3].Trim(),
                    $(if ($fields.Count -ge 5) { $fields[4].Trim() } else { '?' }))
            }
        }
    }

    # -----------------------------------------------------------------------
    # 환경 준비
    # -----------------------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($PythonExe)) {
        # -RequireWorkerModule: 실행만 되는 파이썬이 아니라 ksubmaker_worker 를 import 할 수
        # 있는 파이썬을 고릅니다. Microsoft Store 앱 실행 별칭은 여기서 걸러집니다.
        $PythonExe = Get-KsmPythonExe -RequireWorkerModule -Verbose:($VerbosePreference -ne 'SilentlyContinue')
    }
    if ([string]::IsNullOrWhiteSpace($PythonExe)) {
        throw (@(
            'ksubmaker_worker 를 실행할 수 있는 Python 을 찾지 못했습니다.'
            ''
            '해결 방법 중 하나를 고르세요.'
            '  1) .\scripts\build-worker.ps1  — 임베디드 런타임(tools\python)을 만들고 워커와 의존성을 설치합니다. 권장.'
            '  2) 이미 있는 Python 3.10~3.12 를 쓰려면:'
            '       python -m pip install -e "worker"'
            '       $env:KSUBMAKER_WORKER_PYTHON = "C:\Path\To\python.exe"'
            '  3) -PythonExe 로 경로를 직접 지정'
            ''
            '참고: %LOCALAPPDATA%\Microsoft\WindowsApps\python.exe 는 Microsoft Store 앱 실행 별칭'
            '스텁이라 실행해도 아무 출력 없이 끝납니다. 이 스크립트는 그 후보를 건너뜁니다.'
            '어떤 후보가 왜 탈락했는지 보려면 -Verbose 를 붙여 다시 실행하세요.'
        ) -join [Environment]::NewLine)
    }

    # 여기까지 왔어도 모듈이 없을 수 있습니다(폴백 경로). 미리 확인해서
    # "워커가 응답하지 않는다"는 진단 불가능한 실패 대신 원인을 알려 줍니다.
    $pythonCheck = Test-KsmPython -Path $PythonExe -RequireWorkerModule
    if (-not $pythonCheck.HasWorkerModule) {
        throw (@(
            "선택한 Python 에서 ksubmaker_worker 를 가져올 수 없습니다: $PythonExe"
            "  Python  : $($pythonCheck.Version)"
            "  원인    : $($pythonCheck.Reason)"
            ''
            '.\scripts\build-worker.ps1 을 실행하거나, 해당 Python 에 워커를 설치하세요:'
            "  `"$PythonExe`" -m pip install -e `"$(Join-Path (Get-KsmRepoRoot) 'worker')`""
        ) -join [Environment]::NewLine)
    }
    Write-KsmNote "Python      : $PythonExe (버전 $($pythonCheck.Version), ksubmaker_worker 확인됨)"

    if ([string]::IsNullOrWhiteSpace($ModelsDirectory)) {
        # IAppPaths 의 기본값과 같은 위치. LOCALAPPDATA 가 없는 환경(비-Windows)에서는
        # AppPaths.DefaultRoot 와 같은 순서로 사용자 프로필로 폴백합니다.
        $localAppData = $env:LOCALAPPDATA
        if ([string]::IsNullOrWhiteSpace($localAppData)) {
            $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
        }
        if ([string]::IsNullOrWhiteSpace($localAppData)) {
            $localAppData = [System.IO.Path]::GetTempPath()
        }
        $ModelsDirectory = Join-Path $localAppData 'KSubMaker\models'
    }

    if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
        $WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ksubmaker-smoke-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
        $createdWorkDir = $true
    }
    if (-not (Test-Path -LiteralPath $WorkDirectory)) {
        New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
    }

    Write-KsmNote "python      : $PythonExe"
    Write-KsmNote "모델 폴더   : $ModelsDirectory"
    Write-KsmNote "작업 폴더   : $WorkDirectory"

    if (-not (Test-Path -LiteralPath $ModelsDirectory)) {
        Write-KsmWarn "모델 폴더가 없습니다: $ModelsDirectory — 모델을 먼저 내려받아야 합니다."
    }

    $workerEnvironment = @{
        PYTHONPATH             = (Join-Path $repoRoot 'worker')
        PYTHONIOENCODING       = 'utf-8'
        PYTHONUNBUFFERED       = '1'
        KSUBMAKER_MODELS_DIR   = $ModelsDirectory
        KSUBMAKER_TOOLS_DIR    = $toolsDir
        KSUBMAKER_WORKER_LOG_LEVEL = 'INFO'
    }

    # -----------------------------------------------------------------------
    # 1. 워커를 통한 하드웨어 감지
    # -----------------------------------------------------------------------
    Write-KsmStep '1. 워커를 통한 하드웨어 감지'

    $worker = Start-KsmWorker -PythonExe $PythonExe -WorkingDirectory $repoRoot -Environment $workerEnvironment
    Write-KsmOk ("worker $($worker.Ready.workerVersion), python $($worker.Ready.pythonVersion), " +
                 "protocol $($worker.Ready.protocolVersion)")

    Send-KsmCommand -Process $worker.Process -Command @{
        command         = 'hello'
        requestId       = 'smoke-hello'
        protocolVersion = '1.2'
        hostVersion     = 'smoke-gpu.ps1'
    }

    Send-KsmCommand -Process $worker.Process -Command @{
        command         = 'detectHardware'
        requestId       = 'smoke-hw'
        protocolVersion = '1.2'
    }

    $hardware = $null
    $hwDeadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $hwDeadline) {
        $workerEvent = Read-KsmEvent -Process $worker.Process -TimeoutSeconds 30
        if ($null -eq $workerEvent) { break }
        if ($workerEvent.type -eq 'hardware') { $hardware = $workerEvent; break }
        if ($workerEvent.type -eq 'log') { Write-KsmNote "[worker] $($workerEvent.message)" }
    }

    if ($null -eq $hardware) {
        throw "워커가 hardware 이벤트를 보내지 않았습니다.`nstderr:`n$(Get-KsmWorkerStdErr -Worker $worker)"
    }

    Write-KsmNote "CPU        : $($hardware.cpuName) ($($hardware.logicalCores) 논리 코어)"
    Write-KsmNote ("RAM        : {0:N1} GB (여유 {1:N1} GB)" -f `
        ($hardware.totalRamBytes / 1GB), ($hardware.availableRamBytes / 1GB))
    Write-KsmNote "CUDA 사용   : $($hardware.cudaAvailable)  (버전 $($hardware.cudaVersion))"

    # 프로토콜 1.2. 없는 필드는 $null 이 되므로 1.1 워커에서도 안전합니다.
    if ($hardware.PSObject.Properties.Name -contains 'cudaDeviceDetected') {
        Write-KsmNote "  디바이스   : $($hardware.cudaDeviceDetected)  (드라이버가 동작한다는 뜻일 뿐입니다)"
        Write-KsmNote "  지원 라이브러리: $($hardware.cudaLibrariesAvailable)  (cuBLAS 12 / cuDNN 9 실제 로드)"
    }

    # @() must wrap the WHOLE pipeline: Where-Object unwraps an empty result to $null, and
    # $null.Count throws under Set-StrictMode on Windows PowerShell 5.1.
    $missingCudaLibraries = @(@($hardware.missingCudaLibraries) | Where-Object { $_ })
    if ($missingCudaLibraries.Count -gt 0) {
        Write-KsmWarn ("불러오지 못한 CUDA 지원 라이브러리: " + ($missingCudaLibraries -join ', '))
    }

    foreach ($gpu in @($hardware.gpus)) {
        Write-KsmNote ("GPU #{0}    : {1} — VRAM {2:N1} GB (여유 {3:N1} GB), compute {4}" -f `
            $gpu.index, $gpu.name, ($gpu.totalVramBytes / 1GB), ($gpu.freeVramBytes / 1GB), $gpu.computeCapability)
    }

    foreach ($warning in @($hardware.warnings)) {
        Write-KsmWarn "[worker] $warning"
    }

    if (-not $hardware.cudaAvailable) {
        if (-not $AllowCpu) {
            throw @(
                '워커가 CUDA 를 사용할 수 없다고 보고했습니다. 이 스크립트는 실행하지 않습니다.'
                ''
                'nvidia-smi 는 GPU 를 보았지만 추론 스택(CTranslate2)이 CUDA 를 쓸 수 없는 상태입니다.'
                '확인할 것:'
                $(if ($missingCudaLibraries.Count -gt 0) {
                    '  - !! CUDA 지원 라이브러리 누락: ' + ($missingCudaLibraries -join ', ') +
                    ' — .\scripts\build-worker.ps1 을 다시 실행하세요 (docs/TROUBLESHOOTING.md 2번 항목)'
                } else {
                    '  - NVIDIA 드라이버를 최신으로 업데이트'
                })
                '  - tools\python 에 ctranslate2 가 제대로 설치되어 있는지 (.\scripts\build-worker.ps1)'
                '  - 워커 stderr 의 cuda_setup: 로 시작하는 줄 (등록한 DLL 폴더와 찾은 파일 목록)'
                '  - 로그의 detail 필드'
                ''
                'CPU 로라도 진행하려면 -AllowCpu 를 지정하세요 (GPU 검증은 되지 않습니다).'
            ) -join [Environment]::NewLine
        }
        Write-KsmWarn '-AllowCpu 가 지정되어 CPU 로 계속합니다.'
    }
    else {
        Write-KsmOk 'CUDA 사용 가능. 계속합니다.'
    }

    # -----------------------------------------------------------------------
    # 2. 시험용 클립
    # -----------------------------------------------------------------------
    Write-KsmStep '2. 시험용 클립 준비'

    $syntheticClip = $false
    $videoPath = $SampleVideo

    if ([string]::IsNullOrWhiteSpace($videoPath)) {
        $ffmpeg = Join-Path $toolsDir 'ffmpeg\bin\ffmpeg.exe'
        if (-not (Test-Path -LiteralPath $ffmpeg)) {
            $onPath = Get-Command -Name 'ffmpeg' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($onPath) {
                $ffmpeg = $onPath.Source
                Write-KsmWarn "번들 ffmpeg 가 없어 PATH 의 것을 씁니다: $ffmpeg"
            }
            else {
                throw ("ffmpeg 를 찾지 못해 시험용 클립을 만들 수 없습니다.`n" +
                       "  .\scripts\fetch-ffmpeg.ps1 을 실행하거나 -SampleVideo 로 영상을 지정하세요.")
            }
        }

        $videoPath = Join-Path $WorkDirectory 'smoke-clip.mkv'
        $syntheticClip = $true

        Write-KsmNote "합성 클립 생성: $videoPath ($DurationSeconds 초)"

        # mpeg4 / pcm_s16le 는 LGPL 빌드에 항상 들어 있습니다. libx264 는 GPL 빌드에만
        # 있으므로 쓰지 않습니다.
        $ffmpegArgs = @(
            '-hide_banner', '-nostdin', '-y', '-loglevel', 'error'
            '-f', 'lavfi', '-i', "sine=frequency=440:sample_rate=16000:duration=$DurationSeconds"
            '-f', 'lavfi', '-i', "testsrc=size=320x240:rate=10:duration=$DurationSeconds"
            '-map', '1:v', '-map', '0:a'
            '-c:v', 'mpeg4', '-c:a', 'pcm_s16le'
            '-shortest'
            $videoPath
        )

        $make = Invoke-KsmProcess -FilePath $ffmpeg -ArgumentList $ffmpegArgs -TimeoutSeconds 300
        if ($make.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $videoPath)) {
            throw "시험용 클립 생성에 실패했습니다. 종료 코드 $($make.ExitCode)`n$($make.StandardError)"
        }

        Write-KsmWarn ('합성 클립은 사인파 톤이며 사람의 말이 아닙니다. Whisper 가 세그먼트를 ' +
                       '하나도 만들지 못하면 TRANSCRIPTION_FAILED 로 끝날 수 있습니다. ' +
                       '의미 있는 측정을 원하면 -SampleVideo 로 실제 영상을 지정하세요.')
    }
    else {
        if (-not (Test-Path -LiteralPath $videoPath)) {
            throw "지정한 영상을 찾을 수 없습니다: $videoPath"
        }
        $videoPath = (Resolve-Path -LiteralPath $videoPath).ProviderPath
        Write-KsmNote "영상: $videoPath"
    }

    # -----------------------------------------------------------------------
    # 3. process 실행
    # -----------------------------------------------------------------------
    Write-KsmStep '3. 실제 모델로 처리'

    $checkpointDir = Join-Path $WorkDirectory 'checkpoints'
    $outputPath    = Join-Path $WorkDirectory 'smoke-output.ko.srt'
    New-Item -ItemType Directory -Path $checkpointDir -Force | Out-Null

    Write-KsmNote "Whisper 모델 : $WhisperModel ($ComputeType, beam $BeamSize)"
    Write-KsmNote "번역 엔진    : $TranslationEngine"
    Write-KsmNote "VAD 필터     : $([bool] $VadFilter)"

    $processCommand = @{
        command         = 'process'
        requestId       = 'smoke-process'
        protocolVersion = '1.2'
        jobId           = 'smoke-job'
        videoPath       = $videoPath
        outputPath      = $outputPath
        checkpointDir   = $checkpointDir
        sourceMode      = 'audio'
        resume          = $false
        phase           = 'full'
        settings        = @{
            language                = 'auto'
            whisperModel            = $WhisperModel
            computeType             = $ComputeType
            device                  = 'auto'
            beamSize                = $BeamSize
            vadFilter               = [bool] $VadFilter
            wordTimestamps          = $true
            conditionOnPreviousText = $false
            translationEngine       = $TranslationEngine
            translationModel        = $TranslationModel
            llmModel                = 'auto'
            translationStyle        = 'natural'
            batchMaxItems           = 30
            batchMaxChars           = 2500
            batchMaxSeconds         = 180
            contextLines            = 3
            glossary                = @{}
            maxLinesPerCue          = 2
            maxCharsPerLine         = 22
            minCueDurationSeconds   = 1.0
            maxCueDurationSeconds   = 7.0
            minCueGapMilliseconds   = 50
            mergeShortCues          = $true
            autoRetryOnRecoverableError = $false
        }
    }

    $overall = [System.Diagnostics.Stopwatch]::StartNew()
    $stageTimer = [System.Diagnostics.Stopwatch]::StartNew()

    $stageTimings = New-Object System.Collections.Specialized.OrderedDictionary
    $lastSpeed = $null
    $detectedLanguage = $null
    $terminal = $null
    $currentStage = $null

    Send-KsmCommand -Process $worker.Process -Command $processCommand

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $remaining = [int]([math]::Max(5, ($deadline - (Get-Date)).TotalSeconds))
        $workerEvent = Read-KsmEvent -Process $worker.Process -TimeoutSeconds ([math]::Min(60, $remaining))

        if ($null -eq $workerEvent) {
            if ($worker.Process.HasExited) {
                throw ("워커가 결과를 내기 전에 종료되었습니다. 종료 코드 $($worker.Process.ExitCode)`n" +
                       "stderr:`n$(Get-KsmWorkerStdErr -Worker $worker)")
            }
            continue
        }

        switch ($workerEvent.type) {
            'ack' {
                Write-KsmNote "ack: $($workerEvent.command)"
            }
            'started' {
                $resumed = ''
                if ($workerEvent.PSObject.Properties.Name -contains 'resumedFromStage') {
                    $resumed = " (이어하기: $($workerEvent.resumedFromStage))"
                }
                Write-KsmNote "작업 시작$resumed"
                $stageTimer.Restart()
            }
            'languageDetected' {
                $detectedLanguage = $workerEvent.language
                Write-KsmOk ("감지 언어: {0} (확률 {1:P1})" -f $workerEvent.language, $workerEvent.probability)
            }
            'progress' {
                if ($workerEvent.stage -ne $currentStage) {
                    $currentStage = $workerEvent.stage
                    Write-KsmNote "단계: $currentStage"
                }
                if ($workerEvent.PSObject.Properties.Name -contains 'speed' -and $null -ne $workerEvent.speed) {
                    $lastSpeed = [double] $workerEvent.speed
                }
            }
            'stageCompleted' {
                $elapsed = $stageTimer.Elapsed.TotalSeconds
                $stageTimings[$workerEvent.stage] = $elapsed
                Write-KsmOk ("{0} 완료 — {1:N2}초" -f $workerEvent.stage, $elapsed)
                $stageTimer.Restart()
            }
            'log' {
                Write-KsmNote "[worker/$($workerEvent.level)] $($workerEvent.message)"
            }
            'completed' {
                $terminal = $workerEvent
            }
            'error' {
                $terminal = $workerEvent
            }
            'cancelled' {
                $terminal = $workerEvent
            }
            default {
                Write-KsmNote "이벤트: $($workerEvent.type)"
            }
        }

        if ($null -ne $terminal) { break }
    }

    $overall.Stop()

    if ($null -eq $terminal) {
        throw "제한 시간 $TimeoutSeconds 초 안에 결과가 오지 않았습니다."
    }

    # -----------------------------------------------------------------------
    # 4. 보고
    # -----------------------------------------------------------------------
    Write-KsmStep '4. 결과'

    $exitCode = 0

    if ($terminal.type -eq 'completed') {
        Write-KsmOk '처리 완료.'
        Write-KsmNote "출력 경로   : $($terminal.outputPath)"
        Write-KsmNote "자막 큐 수  : $($terminal.cueCount)"
        Write-KsmNote "원본 언어   : $($terminal.sourceLanguage)"
        Write-KsmNote "Whisper     : $($terminal.whisperModel)"
        if ($terminal.PSObject.Properties.Name -contains 'translationModel') {
            Write-KsmNote "번역 모델   : $($terminal.translationModel)"
        }
        Write-KsmNote ("워커 보고 소요 : {0:N2}초" -f $terminal.elapsedSeconds)
        if ($terminal.skipped) {
            Write-KsmWarn '워커가 파일을 쓰지 않았습니다 (skipped=true). 출력 충돌 정책이나 phase 를 확인하세요.'
        }
    }
    elseif ($terminal.type -eq 'error' -and $terminal.code -eq 'TRANSCRIPTION_FAILED' -and $syntheticClip) {
        Write-KsmWarn @(
            '합성 클립에서 인식 가능한 음성을 찾지 못했습니다 (TRANSCRIPTION_FAILED).'
            '예상된 결과입니다 — 사인파 톤에는 사람의 말이 없습니다.'
            '모델 로드와 CUDA 실행 경로는 검증되었으므로 아래 시간은 유효합니다.'
            '정확도/속도를 제대로 측정하려면 -SampleVideo 로 실제 영상을 지정하세요.'
        ) -join [Environment]::NewLine
    }
    elseif ($terminal.type -eq 'error') {
        Write-Host "오류: [$($terminal.code)] $($terminal.message)" -ForegroundColor Red
        if ($terminal.PSObject.Properties.Name -contains 'detail') {
            Write-Host "detail: $($terminal.detail)" -ForegroundColor DarkGray
        }
        Write-KsmNote '해결 방법은 docs/TROUBLESHOOTING.md 를 보세요.'
        $exitCode = 1
    }
    else {
        Write-KsmWarn "작업이 취소되었습니다."
        $exitCode = 1
    }

    Write-KsmStep '측정 결과'

    if ($stageTimings.Count -gt 0) {
        foreach ($key in $stageTimings.Keys) {
            Write-Host ("    {0,-20} {1,8:N2} 초" -f $key, $stageTimings[$key])
        }
    }

    Write-Host ("    {0,-20} {1,8:N2} 초" -f '전체(스크립트 측정)', $overall.Elapsed.TotalSeconds)

    if ($null -ne $lastSpeed) {
        Write-Host ("    {0,-20} {1,8:N2} x" -f '인식 속도', $lastSpeed)
        Write-KsmNote '인식 속도 = 벽시계 1초당 처리한 미디어 초. 클수록 빠릅니다.'
    }

    if ($stageTimings.Contains('transcribing') -and $DurationSeconds -gt 0 -and $syntheticClip) {
        $ratio = $DurationSeconds / [math]::Max(0.001, $stageTimings['transcribing'])
        Write-Host ("    {0,-20} {1,8:N2} x" -f '실시간 대비', $ratio)
    }

    if ((Test-Path -LiteralPath $outputPath)) {
        $srt = Get-Item -LiteralPath $outputPath
        Write-KsmNote ("결과 파일: {0} ({1:N0} 바이트)" -f $outputPath, $srt.Length)

        $preview = Get-Content -LiteralPath $outputPath -TotalCount 8 -Encoding UTF8
        if ($preview) {
            Write-KsmNote '결과 미리보기:'
            foreach ($line in $preview) { Write-Host "        $line" -ForegroundColor DarkGray }
        }
    }

    exit $exitCode
}
catch {
    # 거부 메시지는 여러 줄이라 Write-Error 의 상자 서식보다 그대로 찍는 편이 읽기 좋습니다.
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Verbose $_.ScriptStackTrace
    exit 1
}
finally {
    if ($null -ne $worker) {
        Stop-KsmWorker -Worker $worker
    }

    if ($createdWorkDir -and -not $KeepArtifacts -and -not [string]::IsNullOrWhiteSpace($WorkDirectory)) {
        Remove-Item -LiteralPath $WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($KeepArtifacts -and -not [string]::IsNullOrWhiteSpace($WorkDirectory)) {
        Write-Host ''
        Write-Host "작업 파일을 남겼습니다: $WorkDirectory" -ForegroundColor Gray
    }
}

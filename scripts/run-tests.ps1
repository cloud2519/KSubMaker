<#
.SYNOPSIS
    닷넷(.NET) 테스트와 Python 워커 테스트를 실행합니다.

.DESCRIPTION
    두 스위트를 순서대로 돌립니다.

      1. dotnet test KSubMaker.sln -c Release
      2. python -m pytest worker/tests

    어느 하나라도 실패하면 0 이 아닌 종료 코드로 끝납니다.

    두 스위트 모두 GPU / 모델 / 네트워크 없이 도는 것을 기준으로 만들어져 있습니다.

    반드시 알아 둘 것 — **"전부 통과"가 "전부 실행"은 아닙니다.**
    FFmpeg 나 Python 이 필요한 통합 테스트는 그 도구가 없으면 실패가 아니라 건너뜁니다
    (ExternalTools.FfmpegSkipReason / PythonSkipReason). 그래서 이 스크립트는 dotnet test 의
    출력을 그대로 보여 주며, skipped 개수를 직접 확인해야 합니다.

    또한 실제 GPU(CUDA) 경로는 이 스위트로 검증되지 않습니다. 그것은
    scripts\smoke-gpu.ps1 을 따로 실행해야 합니다. (docs/DECISIONS.md ADR-020 참고)

    방어 장치: 솔루션에 테스트 프로젝트가 하나도 없으면 경고합니다. -RequireDotnetTests 를
    주면 경고 대신 실패로 처리합니다 (CI 에서 권장).

.PARAMETER Configuration
    닷넷 빌드 구성. 기본값 'Release'.

.PARAMETER SkipDotnet
    닷넷 테스트를 건너뜁니다.

.PARAMETER SkipPython
    Python 테스트를 건너뜁니다.

.PARAMETER RequireDotnetTests
    솔루션에 테스트 프로젝트가 하나도 없으면 실패시킵니다.

.PARAMETER PythonExe
    Python 실행 파일 경로. 생략하면 tools\python\python.exe -> %KSUBMAKER_WORKER_PYTHON%
    -> PATH 순으로 찾습니다 (ToolLocator 와 같은 순서).

.PARAMETER Coverage
    커버리지를 함께 수집합니다 (.NET: XPlat Code Coverage, Python: pytest-cov).

.PARAMETER Filter
    닷넷 테스트 필터 (dotnet test --filter 에 그대로 전달).

.PARAMETER PytestArguments
    pytest 에 추가로 넘길 인자.

.EXAMPLE
    .\scripts\run-tests.ps1

    두 스위트를 모두 실행합니다.

.EXAMPLE
    .\scripts\run-tests.ps1 -SkipDotnet -PytestArguments @('-k', 'protocol', '-v')

    프로토콜 관련 Python 테스트만 자세히 실행합니다.

.EXAMPLE
    .\scripts\run-tests.ps1 -Coverage

    커버리지를 함께 수집합니다.

.NOTES
    PowerShell 5.1 호환.
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipDotnet,
    [switch] $SkipPython,
    [switch] $RequireDotnetTests,
    [string] $PythonExe,
    [switch] $Coverage,
    [string] $Filter,
    [string[]] $PytestArguments = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

try {
    $repoRoot = Get-KsmRepoRoot
    $solution = Join-Path $repoRoot 'KSubMaker.sln'

    Write-KsmStep 'KSubMaker 테스트'
    Write-KsmNote "저장소 루트 : $repoRoot"
    Write-KsmNote "구성        : $Configuration"

    # -----------------------------------------------------------------------
    # .NET
    # -----------------------------------------------------------------------
    if ($SkipDotnet) {
        Write-KsmNote '-SkipDotnet 이므로 .NET 테스트를 건너뜁니다.'
    }
    else {
        Write-KsmStep '.NET 테스트'

        # 'dotnet' 이 있다는 것과 SDK 가 있다는 것은 다릅니다 — 런타임만 설치해도 명령은 존재하고,
        # 그 상태에서 dotnet test 를 부르면 원인을 알기 어려운 오류가 납니다.
        $sdkCheck = Test-KsmDotnetSdk
        if (-not $sdkCheck.Satisfied) {
            $failures.Add(((Get-KsmDotnetSdkHelp -Check $sdkCheck) -join [Environment]::NewLine))
        }
        else {
            Write-KsmNote ".NET SDK: $($sdkCheck.Resolved)"
            $dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            # 솔루션에 테스트 프로젝트가 있는지 먼저 확인합니다.
            $projects = @()
            $listResult = Invoke-KsmProcess -FilePath $dotnet.Source `
                -ArgumentList @('sln', $solution, 'list') -WorkingDirectory $repoRoot
            if ($listResult.ExitCode -eq 0) {
                $projects = @($listResult.StandardOutput -split "`r?`n" |
                              Where-Object { $_ -match '\.csproj\s*$' } |
                              ForEach-Object { $_.Trim() })
            }

            $testProjects = @($projects | Where-Object { $_ -match '[Tt]ests?' })

            if ($testProjects.Count -eq 0) {
                $message = @(
                    'KSubMaker.sln 에 테스트 프로젝트가 하나도 없습니다.'
                    'dotnet test 는 성공하지만 테스트를 하나도 실행하지 않습니다 — 초록불이지만'
                    '아무것도 검증되지 않은 상태입니다.'
                    'tests\KSubMaker.UnitTests 와 tests\KSubMaker.IntegrationTests 가 솔루션에'
                    '등록되어 있는지 확인하세요. 자세한 내용: docs/DECISIONS.md ADR-020'
                ) -join [Environment]::NewLine

                if ($RequireDotnetTests) {
                    $failures.Add($message)
                }
                else {
                    Write-KsmWarn $message
                    $warnings.Add('.NET 테스트 프로젝트 없음')
                }
            }
            else {
                Write-KsmNote ("테스트 프로젝트 $($testProjects.Count) 개:")
                foreach ($project in $testProjects) {
                    Write-KsmNote "  - $project"
                }
            }

            $testArgs = @('test', $solution, '-c', $Configuration, '--nologo')
            if (-not [string]::IsNullOrWhiteSpace($Filter)) {
                $testArgs += @('--filter', $Filter)
            }
            if ($Coverage) {
                $testArgs += @('--collect', 'XPlat Code Coverage')
            }

            Write-KsmNote "dotnet $($testArgs -join ' ')"

            & $dotnet.Source @testArgs
            $dotnetExit = $LASTEXITCODE

            if ($dotnetExit -ne 0) {
                $failures.Add("dotnet test 가 실패했습니다. 종료 코드 $dotnetExit")
            }
            else {
                Write-KsmOk 'dotnet test 통과.'
                Write-KsmNote ('위 출력의 Skipped 개수를 확인하세요. FFmpeg / Python 이 없는 환경에서는 ' +
                               '해당 통합 테스트가 실패가 아니라 건너뛰기로 처리됩니다.')
            }
        }
    }

    # -----------------------------------------------------------------------
    # Python
    # -----------------------------------------------------------------------
    if ($SkipPython) {
        Write-KsmNote '-SkipPython 이므로 Python 테스트를 건너뜁니다.'
    }
    else {
        Write-KsmStep 'Python 워커 테스트'

        if ([string]::IsNullOrWhiteSpace($PythonExe)) {
            $PythonExe = Get-KsmPythonExe
        }

        if ([string]::IsNullOrWhiteSpace($PythonExe)) {
            $failures.Add(
                'Python 을 찾지 못했습니다. Python 3.11 이상을 설치하거나, ' +
                '.\scripts\build-worker.ps1 로 임베디드 런타임을 만들거나, -PythonExe 로 경로를 지정하세요.')
        }
        else {
            Write-KsmNote "python : $PythonExe"

            $testsDir = Join-Path $repoRoot 'worker\tests'
            if (-not (Test-Path -LiteralPath $testsDir)) {
                $failures.Add("Python 테스트 디렉터리가 없습니다: $testsDir")
            }
            else {
                $pytestArgs = @('-m', 'pytest', $testsDir)
                if ($Coverage) {
                    $pytestArgs += @('--cov=ksubmaker_worker', '--cov-report=term-missing')
                }
                $pytestArgs += $PytestArguments

                # 패키지를 설치하지 않았어도 소스 트리에서 import 되도록 PYTHONPATH 를 줍니다.
                $environment = @{
                    PYTHONPATH       = (Join-Path $repoRoot 'worker')
                    PYTHONIOENCODING = 'utf-8'
                    PYTHONUNBUFFERED = '1'
                }

                Write-KsmNote "python $($pytestArgs -join ' ')"

                $result = Invoke-KsmProcess -FilePath $PythonExe `
                    -ArgumentList $pytestArgs `
                    -WorkingDirectory $repoRoot `
                    -Environment $environment

                if ($result.StandardOutput) { Write-Host $result.StandardOutput }
                if ($result.StandardError -and $result.ExitCode -ne 0) { Write-Host $result.StandardError }

                if ($result.ExitCode -ne 0) {
                    $failures.Add("pytest 가 실패했습니다. 종료 코드 $($result.ExitCode)")
                }
                else {
                    Write-KsmOk 'pytest 통과.'
                }
            }
        }
    }

    # -----------------------------------------------------------------------
    # 결과
    # -----------------------------------------------------------------------
    Write-KsmStep '결과'

    foreach ($warning in $warnings) {
        Write-KsmWarn $warning
    }

    if ($failures.Count -gt 0) {
        foreach ($failure in $failures) {
            Write-Host "실패: $failure" -ForegroundColor Red
        }
        exit 1
    }

    Write-KsmOk '모든 테스트가 통과했습니다.'
    exit 0
}
catch {
    # 여러 줄짜리 안내 메시지는 Write-Error 의 상자 서식에서 한 줄로 뭉개지므로
    # 본문을 그대로 찍고 종료 코드만 0 이 아닌 값으로 둡니다.
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Verbose $_.ScriptStackTrace
    exit 1
}

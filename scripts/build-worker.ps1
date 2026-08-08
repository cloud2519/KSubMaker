<#
.SYNOPSIS
    임베디드 CPython 런타임(tools/python)을 만들고 KSubMaker AI 워커를 그 안에 설치합니다.

.DESCRIPTION
    사용자가 Python 을 따로 설치하지 않아도 되도록, python-build-standalone 의 CPython 3.11
    Windows x64 배포판을 받아 저장소의

        tools\python\python.exe

    에 배치하고, 거기에 worker/ 패키지와 런타임 의존성을 설치합니다.

    이 경로는 KSubMaker.Worker.Tools.ToolLocator 가 워커를 실행할 때 두 번째로 찾는
    위치입니다. (첫 번째는 얼린 실행 파일 <앱>\worker\ksubmaker-worker.exe 이며 현재
    빌드 파이프라인은 그것을 만들지 않습니다.)

        <앱>\worker\ksubmaker-worker.exe        (얼린 빌드 - 현재 미사용)
        <앱>\tools\python\python.exe -m ksubmaker_worker      <- 이 스크립트가 만드는 것
        %KSUBMAKER_WORKER_PYTHON% -m ksubmaker_worker
        PATH 의 python / python3                (개발용 폴백)

    수행 순서:
      1. python-build-standalone 의 install_only 배포판 다운로드 (+ SHA-256 검증)
      2. tools\python 에 압축 해제
      3. pip 최신화 후 worker/ 를 설치 (기본은 CPU 휠, -Cuda 로 CUDA 휠 인덱스 사용)
      4. CUDA 지원 라이브러리(cuBLAS 12 + cuDNN 9) 설치 (-SkipCudaLibraries 로 생략)
      5. __pycache__ / tests / 문서 등 불필요한 파일 정리
      6. 스모크 테스트: hello + shutdown 명령을 stdin 으로 흘려 넣고,
         stdout 의 모든 줄이 유효한 JSON 인지, ready 와 goodbye 가 왔는지 확인

    CUDA 지원 라이브러리에 대하여
      ctranslate2 >= 4.5 는 cuBLAS(CUDA 12)와 cuDNN 9 에 링크되어 있습니다. 둘 다
      ctranslate2 휠에 들어 있지 않고, NVIDIA **드라이버**도 제공하지 않습니다 — 툴킷
      라이브러리이기 때문입니다. 그래서 이 스크립트가 PyPI 의 nvidia-cublas-cu12 /
      nvidia-cudnn-cu12 (둘 다 win_amd64 휠이 있습니다) 를 함께 설치합니다. 설치하지 않으면
      GPU 가 있는 기계에서 첫 모델 로드가 이렇게 죽습니다:

          RuntimeError('Library cublas64_12.dll is not found or cannot be loaded')

      pip 는 DLL 을 site-packages\nvidia\<구성요소>\bin 에 넣는데, 그 폴더는 Windows DLL
      검색 경로가 아닙니다. 런타임에 worker/ksubmaker_worker/cuda_setup.py 가
      os.add_dll_directory() 로 등록합니다. 설치만으로는 부족합니다.

    라이선스: CPython 은 PSF 라이선스입니다. python-build-standalone 배포판에 포함된
    licenses 디렉터리를 삭제하지 마세요. THIRD_PARTY_NOTICES.md 참고.

.PARAMETER Url
    받을 배포판의 HTTPS 주소를 직접 지정합니다. 지정하면 릴리스 조회를 건너뜁니다.

.PARAMETER Repository
    python-build-standalone 저장소. 기본값 'astral-sh/python-build-standalone'.

.PARAMETER Tag
    릴리스 태그. 기본값 'latest'. 재현성을 위해 고정하는 것을 권장합니다.

.PARAMETER PythonVersion
    설치할 CPython 버전. 기본값 '3.11'. 워커의 requires-python 은 '>=3.11' 입니다.

.PARAMETER AssetPattern
    자산 이름 와일드카드. 기본값은 -PythonVersion 으로부터 만들어집니다
    ('cpython-<버전>*-x86_64-pc-windows-msvc-install_only*.tar.gz').
    일치하는 자산이 없으면 사용 가능한 이름을 모두 출력합니다.

.PARAMETER Sha256
    기대하는 SHA-256. 생략하면 계산해서 출력만 합니다.

.PARAMETER GitHubToken
    선택. GitHub API 요청 한도를 늘립니다.

.PARAMETER Cuda
    CUDA 지원 ctranslate2 / onnxruntime 휠을 설치합니다. 지정하지 않으면 PyPI 기본 휠을
    설치하며, 그것으로도 GPU 가 동작하는 경우가 많지만 환경에 따라 다릅니다.
    (ctranslate2 의 PyPI 휠은 CUDA 지원을 포함해 배포됩니다. 이 스위치는 추가 인덱스가
    필요한 조직 환경을 위한 확장 지점입니다.)

.PARAMETER ExtraIndexUrl
    pip 에 넘길 추가 인덱스 URL (HTTPS 만).

.PARAMETER SkipCudaLibraries
    CUDA 지원 라이브러리(cuBLAS 12 + cuDNN 9) 설치를 건너뜁니다. 디스크 약 1.8 GB
    (다운로드 약 1.2 GB) 를 아낄 수 있지만
    만들어진 배포본은 **CPU 전용**이 됩니다. GPU 기계에서 실행하면 첫 모델 로드에서
    CUDA_LIBRARY_MISSING 으로 실패합니다.

.PARAMETER CublasVersion
    설치할 nvidia-cublas-cu12 버전 제약. 기본 '>=12.9,<13'.

.PARAMETER CudnnVersion
    설치할 nvidia-cudnn-cu12 버전 제약. 기본 '>=9.24,<10'.

.PARAMETER SkipInstall
    파이썬만 배치하고 pip 설치는 건너뜁니다. 런타임 다운로드만 검증할 때 유용합니다.

.PARAMETER SkipSmokeTest
    스모크 테스트를 건너뜁니다. 권장하지 않습니다.

.PARAMETER Force
    tools\python 이 이미 있어도 지우고 다시 만듭니다.

.PARAMETER KeepArchive
    받은 압축 파일을 tools\_downloads 에 남깁니다.

.EXAMPLE
    .\scripts\build-worker.ps1

    임베디드 런타임을 만들고 워커를 설치한 뒤 스모크 테스트합니다.

.EXAMPLE
    .\scripts\build-worker.ps1 -Force -Tag '20250115' -Sha256 '…'

    특정 릴리스를 해시까지 고정해 처음부터 다시 만듭니다.

.EXAMPLE
    .\scripts\build-worker.ps1 -SkipCudaLibraries

    CPU 전용 배포본을 만듭니다. 약 1.8 GB 작아지지만 GPU 는 쓸 수 없습니다.

.EXAMPLE
    .\scripts\build-worker.ps1 -WhatIf

    무엇을 받아 어디에 쓸지만 보여 줍니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
    설치 결과는 수 GB 입니다 (ctranslate2 수백 MB + CUDA 지원 라이브러리 약 1.8 GB).
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Url,
    [string] $Repository = 'astral-sh/python-build-standalone',
    [string] $Tag = 'latest',
    [string] $PythonVersion = '3.11',
    [string] $AssetPattern,
    [string] $Sha256,
    [string] $GitHubToken,
    [switch] $Cuda,
    [string] $ExtraIndexUrl,
    [switch] $SkipCudaLibraries,
    # 주 버전을 고정합니다. ctranslate2 >= 4.5 는 cublas64_12.dll 과 cudnn64_9.dll 이라는
    # **파일 이름**을 링크에 박아 두므로, 최신을 따라가다 cuDNN 10 휠이 들어오면 설치는 깨끗이
    # 되고 로드에서만 실패합니다 — 가장 진단하기 어려운 형태의 고장입니다.
    [string] $CublasVersion = '>=12.9,<13',
    [string] $CudnnVersion  = '>=9.24,<10',
    [switch] $SkipInstall,
    [switch] $SkipSmokeTest,
    [switch] $Force,
    [switch] $KeepArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

function Invoke-KsmPip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $PythonExe,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [string] $Description
    )

    if ($Description) { Write-KsmNote $Description }

    $argv = @('-m', 'pip') + $Arguments
    $result = Invoke-KsmProcess -FilePath $PythonExe -ArgumentList $argv -WorkingDirectory $WorkingDirectory

    if ($result.ExitCode -ne 0) {
        throw ("pip 실행에 실패했습니다 (종료 코드 $($result.ExitCode)).`n" +
               "  명령: $PythonExe $($argv -join ' ')`n" +
               "  stdout:`n$($result.StandardOutput)`n" +
               "  stderr:`n$($result.StandardError)")
    }

    return $result
}

function Get-KsmDirectorySize {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return [int64] 0 }

    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return [int64] 0 }
    return [int64] $sum
}

try {
    $repoRoot   = Get-KsmRepoRoot
    $toolsDir   = Get-KsmToolsDirectory -Create
    $pythonDir  = Join-Path $toolsDir 'python'
    $workerDir  = Join-Path $repoRoot 'worker'

    Write-KsmStep '임베디드 파이썬 런타임 + AI 워커 빌드'
    Write-KsmNote "저장소 루트 : $repoRoot"
    Write-KsmNote "설치 위치   : $pythonDir"

    if (-not (Test-Path -LiteralPath (Join-Path $workerDir 'pyproject.toml'))) {
        throw "worker/pyproject.toml 을 찾지 못했습니다: $workerDir"
    }

    if ([string]::IsNullOrWhiteSpace($AssetPattern)) {
        $AssetPattern = "cpython-$PythonVersion*-x86_64-pc-windows-msvc-install_only*.tar.gz"
    }

    if ((Test-Path -LiteralPath $pythonDir) -and -not $Force) {
        $existing = Join-Path $pythonDir 'python.exe'
        if (Test-Path -LiteralPath $existing) {
            Write-KsmWarn "tools\python 이 이미 있습니다. 런타임 다운로드를 건너뛰고 워커만 다시 설치합니다."
            Write-KsmNote '처음부터 다시 만들려면 -Force 를 지정하세요.'
        }
        else {
            Remove-KsmDirectory -Path $pythonDir
        }
    }

    # -----------------------------------------------------------------------
    # 1. 런타임 다운로드 + 배치
    # -----------------------------------------------------------------------
    $pythonExe = Join-Path $pythonDir 'python.exe'

    if ($Force -or -not (Test-Path -LiteralPath $pythonExe)) {
        Write-KsmStep 'CPython 배포판 다운로드'

        $downloadUrl  = $Url
        $downloadName = $null

        if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
            $asset = Get-KsmGitHubAsset -Repository $Repository -Tag $Tag -Pattern $AssetPattern -GitHubToken $GitHubToken
            $downloadUrl  = $asset.Url
            $downloadName = $asset.Name
            Write-KsmNote "릴리스 : $($asset.Tag)"
            Write-KsmNote "자산   : $($asset.Name)  ($([math]::Round($asset.SizeBytes / 1MB, 1)) MB)"
        }
        else {
            $downloadName = [System.IO.Path]::GetFileName(([uri] $downloadUrl).AbsolutePath)
            if ([string]::IsNullOrWhiteSpace($downloadName)) {
                $downloadName = 'cpython.tar.gz'
            }
        }

        $downloadDir = Join-Path $toolsDir '_downloads'
        $archivePath = Join-Path $downloadDir $downloadName

        if (-not (Test-Path -LiteralPath $downloadDir)) {
            if ($PSCmdlet.ShouldProcess($downloadDir, '디렉터리 생성')) {
                New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
            }
        }

        Invoke-KsmDownload -Uri $downloadUrl -OutFile $archivePath -Sha256 $Sha256 | Out-Null

        if ($WhatIfPreference) {
            Write-KsmNote '-WhatIf 이므로 압축 해제 이후 단계는 건너뜁니다.'
            exit 0
        }

        $stagingDir = Join-Path $downloadDir 'python-staging'
        Remove-KsmDirectory -Path $stagingDir
        Expand-KsmArchive -ArchivePath $archivePath -Destination $stagingDir

        # install_only 배포판은 'python\' 한 겹으로 감싸여 있습니다.
        $foundExe = Get-ChildItem -LiteralPath $stagingDir -Filter 'python.exe' -Recurse -File -ErrorAction SilentlyContinue |
                    Select-Object -First 1
        if (-not $foundExe) {
            throw "압축을 푼 결과에서 python.exe 를 찾지 못했습니다: $stagingDir"
        }

        Remove-KsmDirectory -Path $pythonDir
        New-Item -ItemType Directory -Path (Split-Path -Parent $pythonDir) -Force | Out-Null
        Move-Item -LiteralPath $foundExe.DirectoryName -Destination $pythonDir -Force

        Remove-KsmDirectory -Path $stagingDir
        if (-not $KeepArchive) {
            Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        }

        if (-not (Test-Path -LiteralPath $pythonExe)) {
            throw "배치 후에도 python.exe 가 없습니다: $pythonExe"
        }

        $ver = Invoke-KsmProcess -FilePath $pythonExe -ArgumentList @('--version') -TimeoutSeconds 60
        if ($ver.ExitCode -ne 0) {
            throw "임베디드 python 을 실행하지 못했습니다. 종료 코드 $($ver.ExitCode)`n$($ver.StandardError)"
        }
        Write-KsmOk (($ver.StandardOutput + $ver.StandardError).Trim())
    }
    else {
        Write-KsmNote "기존 런타임을 사용합니다: $pythonExe"
    }

    if ($WhatIfPreference) {
        Write-KsmNote '-WhatIf 이므로 pip 설치와 스모크 테스트는 건너뜁니다.'
        exit 0
    }

    # -----------------------------------------------------------------------
    # 2. 워커 설치
    # -----------------------------------------------------------------------
    if ($SkipInstall) {
        Write-KsmWarn '-SkipInstall 이 지정되어 워커 패키지를 설치하지 않습니다.'
    }
    else {
        Write-KsmStep '워커 패키지 설치'

        if ($PSCmdlet.ShouldProcess($pythonDir, 'pip 로 worker 패키지 설치')) {
            Invoke-KsmPip -PythonExe $pythonExe -WorkingDirectory $repoRoot `
                -Arguments @('install', '--upgrade', '--no-warn-script-location', 'pip', 'setuptools', 'wheel') `
                -Description 'pip / setuptools / wheel 최신화' | Out-Null

            $installArgs = @('install', '--no-warn-script-location', '--no-compile')

            if (-not [string]::IsNullOrWhiteSpace($ExtraIndexUrl)) {
                if ($ExtraIndexUrl -notmatch '^https://') {
                    throw "추가 인덱스는 HTTPS 여야 합니다: $ExtraIndexUrl"
                }
                $installArgs += @('--extra-index-url', $ExtraIndexUrl)
            }
            elseif ($Cuda) {
                Write-KsmNote ('-Cuda 가 지정되었지만 추가 인덱스가 없습니다. ctranslate2 의 PyPI 휠은 ' +
                               'CUDA 지원을 포함하므로 대개 그대로 동작합니다.')
            }

            # 편집 가능 설치(-e)가 아니라 일반 설치입니다. 배포본은 소스 트리에
            # 의존해서는 안 되기 때문입니다.
            $installArgs += $workerDir

            Invoke-KsmPip -PythonExe $pythonExe -WorkingDirectory $repoRoot `
                -Arguments $installArgs `
                -Description 'ksubmaker-worker 와 런타임 의존성 설치 (수백 MB, 몇 분 걸립니다)' | Out-Null
        }

        # -------------------------------------------------------------------
        # 2b. CUDA 지원 라이브러리
        #
        # worker/pyproject.toml 의 의존성으로 넣지 않는 이유: 이 두 휠은 win_amd64 (와 linux)
        # 전용이라 개발자 기계나 CI 에서 `pip install -e worker[dev]` 를 깨뜨립니다. 배포본을
        # 만드는 이 스크립트만 설치합니다.
        # -------------------------------------------------------------------
        if ($SkipCudaLibraries) {
            Write-KsmWarn ('-SkipCudaLibraries 가 지정되었습니다. CPU 전용 배포본이 됩니다 — ' +
                           'GPU 기계에서는 첫 모델 로드가 CUDA_LIBRARY_MISSING 으로 실패합니다.')
        }
        else {
            Write-KsmStep 'CUDA 지원 라이브러리 설치 (cuBLAS 12 + cuDNN 9)'
            Write-KsmNote 'ctranslate2 가 요구하지만 휠에도, NVIDIA 드라이버에도 들어 있지 않습니다.'
            Write-KsmNote '건너뛰려면 -SkipCudaLibraries 를 지정하세요 (약 1.8 GB 절약, GPU 사용 불가).'

            $sizeBeforeCuda = Get-KsmDirectorySize -Path $pythonDir

            if ($PSCmdlet.ShouldProcess($pythonDir, 'pip 로 CUDA 지원 라이브러리 설치')) {
                $cudaArgs = @('install', '--no-warn-script-location', '--no-compile')

                if (-not [string]::IsNullOrWhiteSpace($ExtraIndexUrl)) {
                    $cudaArgs += @('--extra-index-url', $ExtraIndexUrl)
                }

                # 두 패키지를 한 번에 넘겨 pip 가 의존성을 한 번만 해석하게 합니다.
                $cudaArgs += @("nvidia-cublas-cu12$CublasVersion", "nvidia-cudnn-cu12$CudnnVersion")

                Invoke-KsmPip -PythonExe $pythonExe -WorkingDirectory $repoRoot `
                    -Arguments $cudaArgs `
                    -Description "nvidia-cublas-cu12 $CublasVersion / nvidia-cudnn-cu12 $CudnnVersion (다운로드 약 1.2 GB, 디스크 약 1.8 GB)" | Out-Null
            }

            $sizeAfterCuda = Get-KsmDirectorySize -Path $pythonDir
            $addedBytes = $sizeAfterCuda - $sizeBeforeCuda
            if ($addedBytes -gt 0) {
                Write-KsmNote ("CUDA 라이브러리가 추가한 용량: {0:N0} MB (누적 {1:N0} MB)" -f `
                               ($addedBytes / 1MB), ($sizeAfterCuda / 1MB))
            }

            # 설치되었다는 것과 런타임이 찾을 수 있다는 것은 별개입니다. 여기서는 파일이
            # 제자리에 있는지만 확인합니다 — 실제 로드 여부는 GPU 가 있어야 알 수 있습니다.
            $expected = @('cublas64_12.dll', 'cudnn64_9.dll')
            $sitePackages = Join-Path $pythonDir 'Lib\site-packages\nvidia'
            $missing = New-Object System.Collections.Generic.List[string]

            foreach ($dll in $expected) {
                $hit = Get-ChildItem -LiteralPath $sitePackages -Filter $dll -Recurse -File -ErrorAction SilentlyContinue |
                       Select-Object -First 1
                if ($hit) {
                    Write-KsmOk ("$dll : " + $hit.FullName.Substring($pythonDir.Length).TrimStart('\'))
                }
                else {
                    $missing.Add($dll)
                }
            }

            if ($missing.Count -gt 0 -and -not $WhatIfPreference) {
                throw ("CUDA 지원 라이브러리를 설치했는데 다음 DLL 이 없습니다: $($missing -join ', ')`n" +
                       "  휠 레이아웃이 바뀌었을 수 있습니다. $sitePackages 아래를 확인하세요.`n" +
                       "  CPU 전용 배포본을 만들려면 -SkipCudaLibraries 를 지정하세요.")
            }
        }

        $frozen = Invoke-KsmProcess -FilePath $pythonExe -ArgumentList @('-m', 'pip', 'freeze') -WorkingDirectory $repoRoot
        if ($frozen.ExitCode -eq 0) {
            $manifestPath = Join-Path $pythonDir 'INSTALLED-PACKAGES.txt'
            Set-Content -LiteralPath $manifestPath -Value $frozen.StandardOutput -Encoding UTF8
            Write-KsmNote "설치된 패키지 목록: $manifestPath"
            Write-KsmNote 'THIRD_PARTY_NOTICES.md 갱신 시 이 목록을 참고하세요.'
        }
    }

    # -----------------------------------------------------------------------
    # 3. 정리
    # -----------------------------------------------------------------------
    Write-KsmStep '불필요한 파일 정리'

    $before = (Get-ChildItem -LiteralPath $pythonDir -Recurse -File -ErrorAction SilentlyContinue |
               Measure-Object -Property Length -Sum).Sum

    $removedDirs = 0
    Get-ChildItem -LiteralPath $pythonDir -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('__pycache__', 'tests', 'test', '.pytest_cache') } |
        Sort-Object -Property { $_.FullName.Length } -Descending |
        ForEach-Object {
            # site-packages 밖의 표준 라이브러리 test 패키지도 지웁니다. 워커는 쓰지 않습니다.
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removedDirs++
        }

    $removedFiles = 0
    Get-ChildItem -LiteralPath $pythonDir -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.pyc', '.pyo') } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            $removedFiles++
        }

    $after = (Get-ChildItem -LiteralPath $pythonDir -Recurse -File -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum).Sum

    Write-KsmNote "디렉터리 $removedDirs 개, 파일 $removedFiles 개 삭제"
    if ($before -and $after) {
        Write-KsmNote ("크기: {0:N0} MB -> {1:N0} MB" -f ($before / 1MB), ($after / 1MB))
    }

    # -----------------------------------------------------------------------
    # 4. 스모크 테스트
    # -----------------------------------------------------------------------
    if ($SkipSmokeTest) {
        Write-KsmWarn '-SkipSmokeTest 가 지정되어 스모크 테스트를 건너뜁니다.'
    }
    else {
        Write-KsmStep '스모크 테스트 (hello + shutdown)'

        $stdin = @(
            '{"command":"hello","requestId":"smoke-hello","protocolVersion":"1.2","hostVersion":"build-worker.ps1"}'
            '{"command":"shutdown","requestId":"smoke-shutdown","protocolVersion":"1.2"}'
        ) -join "`n"
        $stdin = $stdin + "`n"

        $result = Invoke-KsmProcess -FilePath $pythonExe `
            -ArgumentList @('-m', 'ksubmaker_worker') `
            -StandardInput $stdin `
            -WorkingDirectory $repoRoot `
            -Environment @{ PYTHONIOENCODING = 'utf-8'; PYTHONUNBUFFERED = '1' } `
            -TimeoutSeconds 180

        if ($result.ExitCode -ne 0) {
            throw ("워커가 종료 코드 $($result.ExitCode) 로 끝났습니다.`n" +
                   "stdout:`n$($result.StandardOutput)`n" +
                   "stderr:`n$($result.StandardError)")
        }

        $lines = @($result.StandardOutput -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })

        if ($lines.Count -eq 0) {
            throw "워커가 stdout 에 아무것도 쓰지 않았습니다.`nstderr:`n$($result.StandardError)"
        }

        $types = New-Object System.Collections.Generic.List[string]

        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            try {
                $workerEvent = $trimmed | ConvertFrom-Json
            }
            catch {
                throw ("stdout 에 JSON 이 아닌 줄이 있습니다. stdout 은 프로토콜 전용이어야 합니다.`n" +
                       "  줄   : $trimmed`n" +
                       "  오류 : $($_.Exception.Message)`n" +
                       "  (worker/ksubmaker_worker/protocol.py 의 install_stdout_guard 를 확인하세요.)")
            }

            if (-not ($workerEvent.PSObject.Properties.Name -contains 'type')) {
                throw "이벤트에 type 필드가 없습니다: $trimmed"
            }

            $types.Add([string] $workerEvent.type)
        }

        Write-KsmNote ("받은 이벤트: " + ($types -join ', '))

        foreach ($required in @('ready', 'goodbye')) {
            if ($types -notcontains $required) {
                throw "'$required' 이벤트가 오지 않았습니다. 받은 것: $($types -join ', ')"
            }
        }

        $ackCount = @($types | Where-Object { $_ -eq 'ack' }).Count
        if ($ackCount -lt 2) {
            throw "ack 이벤트가 2개여야 하는데 $ackCount 개만 왔습니다. (hello, shutdown)"
        }

        $ready = $lines[0].Trim() | ConvertFrom-Json
        if ($ready.type -ne 'ready') {
            throw "첫 줄이 ready 가 아닙니다: $($ready.type)"
        }
        if ($ready.protocolVersion -ne '1.2') {
            Write-KsmWarn ("워커가 보고한 프로토콜 버전이 1.2 가 아닙니다: $($ready.protocolVersion). " +
                           "src/KSubMaker.WorkerProtocol/ProtocolConstants.cs 와 맞는지 확인하세요.")
        }

        Write-KsmOk ("스모크 테스트 통과 — worker $($ready.workerVersion), " +
                     "python $($ready.pythonVersion), protocol $($ready.protocolVersion)")
    }

    Write-KsmStep '완료'
    Write-KsmNote "python : $pythonExe"
    if ($SkipCudaLibraries) {
        Write-KsmWarn 'CUDA 지원 라이브러리 없이 만들어졌습니다. 이 배포본은 CPU 전용입니다.'
    }
    else {
        Write-KsmNote 'CUDA 지원 라이브러리(cuBLAS 12 + cuDNN 9) 포함. GPU 기계에서 그대로 동작합니다.'
    }
    Write-KsmNote '다음: .\scripts\build-portable.ps1'
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

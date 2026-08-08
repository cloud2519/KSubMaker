<#
.SYNOPSIS
    KSubMaker 포터블 배포본(zip)을 만듭니다.

.DESCRIPTION
    수행 순서:

      1. dotnet publish src/KSubMaker.App -c Release -r win-x64 --self-contained true
         (self-contained 이므로 사용자가 .NET 런타임을 설치할 필요가 없습니다)
      2. tools\ 전체를 게시 폴더로 복사
         -> <앱>\tools\ffmpeg\bin, <앱>\tools\python, <앱>\tools\llama
         IAppPaths.ToolsDirectory 는 <앱 실행 파일 폴더>\tools 이므로 경로가 정확히 맞습니다.
      3. 워커 소스를 <앱>\worker\ksubmaker_worker 로 복사
         ToolLocator.FindWorkerSourceDirectory 가 <앱>\worker 를 찾아 PYTHONPATH 로 설정합니다.
         (tools\python 에 이미 설치되어 있으므로 이중 안전장치입니다.)
      4. LICENSE, THIRD_PARTY_NOTICES.md, README.md, docs\ 복사
      5. VERSION.txt 작성
      6. artifacts\KSubMaker-portable-<버전>-win-x64.zip 으로 압축

    사전 조건: tools\ffmpeg\bin\ffmpeg.exe 와 tools\python\python.exe 가 있어야 합니다.
    없으면 경고하고 계속하되 -RequireTools 를 주면 실패시킵니다.

        .\scripts\fetch-ffmpeg.ps1
        .\scripts\build-worker.ps1

.PARAMETER Configuration
    빌드 구성. 기본값 'Release'.

.PARAMETER Runtime
    런타임 식별자. 기본값 'win-x64'.

.PARAMETER Version
    산출물 이름에 쓸 버전. 생략하면 Directory.Build.props 의 <Version> 을 씁니다.

.PARAMETER OutputDirectory
    zip 을 만들 폴더. 기본값 <저장소 루트>\artifacts.

.PARAMETER SkipPublish
    dotnet publish 를 건너뛰고 기존 게시 결과를 그대로 씁니다.

.PARAMETER SkipZip
    압축을 건너뛰고 게시 폴더만 남깁니다. 설치 프로그램 빌드에서 사용합니다.

.PARAMETER RequireTools
    tools\ffmpeg 또는 tools\python 이 없으면 경고 대신 실패시킵니다. 릴리스 빌드에 쓰세요.

.PARAMETER SelfContained
    self-contained 게시 여부. 기본값 $true. $false 로 하면 .NET 런타임 설치가 필요해집니다.

.EXAMPLE
    .\scripts\build-portable.ps1

    포터블 zip 을 만듭니다.

.EXAMPLE
    .\scripts\build-portable.ps1 -RequireTools -Version '0.1.0'

    도구가 모두 준비된 상태에서만 성공하는 릴리스 빌드.

.EXAMPLE
    .\scripts\build-portable.ps1 -WhatIf

    무엇을 어디에 쓸지만 보여 줍니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
    zip 압축은 Compress-Archive 대신 System.IO.Compression.ZipFile 을 씁니다
    (PowerShell 5.1 의 Compress-Archive 는 2GB 를 넘기지 못합니다).
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Version,
    [string] $OutputDirectory,
    [switch] $SkipPublish,
    [switch] $SkipZip,
    [switch] $RequireTools,
    [bool] $SelfContained = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

function Copy-KsmTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Source,
        [Parameter(Mandatory)][string] $Destination,
        [string[]] $ExcludeDirectoryNames = @()
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return 0
    }

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $sourceFull = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')
    $count = 0

    Get-ChildItem -LiteralPath $Source -Recurse -File -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceFull.Length).TrimStart('\')

        foreach ($excluded in $ExcludeDirectoryNames) {
            if ($relative -like "*\$excluded\*" -or $relative -like "$excluded\*") {
                return
            }
        }

        $targetPath = Join-Path $Destination $relative
        $targetDir = Split-Path -Parent $targetPath
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }

        Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        $count++
    }

    return $count
}

try {
    $repoRoot = Get-KsmRepoRoot
    $toolsDir = Join-Path $repoRoot 'tools'

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-KsmVersion
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot 'artifacts'
    }

    $publishDir = Join-Path $repoRoot ("publish\{0}-{1}" -f $Runtime, $Configuration)
    $zipName    = "KSubMaker-portable-$Version-$Runtime.zip"
    $zipPath    = Join-Path $OutputDirectory $zipName

    Write-KsmStep 'KSubMaker 포터블 빌드'
    Write-KsmNote "버전       : $Version"
    Write-KsmNote "구성/런타임: $Configuration / $Runtime"
    Write-KsmNote "게시 폴더  : $publishDir"
    Write-KsmNote "산출물     : $zipPath"

    # -----------------------------------------------------------------------
    # 사전 조건
    # -----------------------------------------------------------------------
    # 사전 조건은 **한 번에 모두** 점검합니다. 하나씩 실패시키면 사용자가 설치 → 재실행 →
    # 다음 실패를 반복하게 됩니다. SDK 는 없으면 진행 자체가 불가능하므로 오류, 번들 도구는
    # 없어도 zip 은 만들어지므로 경고입니다 — 다만 둘 다 같은 화면에 보여 줍니다.
    Write-KsmStep '사전 조건 점검'

    $sdkCheck = Test-KsmDotnetSdk
    if ($sdkCheck.Satisfied) {
        Write-KsmOk ".NET SDK      : $($sdkCheck.Resolved)"
    }
    else {
        Write-Host '    .NET SDK      : 사용할 수 없음' -ForegroundColor Red
    }

    $missingTools = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'ffmpeg\bin\ffmpeg.exe'))) {
        $missingTools.Add('tools\ffmpeg\bin\ffmpeg.exe  (.\scripts\fetch-ffmpeg.ps1)')
    }
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'ffmpeg\bin\ffprobe.exe'))) {
        $missingTools.Add('tools\ffmpeg\bin\ffprobe.exe (.\scripts\fetch-ffmpeg.ps1)')
    }
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'python\python.exe'))) {
        $missingTools.Add('tools\python\python.exe      (.\scripts\build-worker.ps1)')
    }

    if ($missingTools.Count -eq 0) {
        Write-KsmOk '번들 도구      : ffmpeg, ffprobe, python 모두 준비됨'
    }
    else {
        Write-Host '    번들 도구      : 일부 없음' -ForegroundColor Yellow
        foreach ($item in $missingTools) {
            Write-Host "      - $item" -ForegroundColor Yellow
        }
    }

    if (-not $sdkCheck.Satisfied) {
        $help = New-Object System.Collections.Generic.List[string]
        [void] $help.Add('빌드를 진행할 수 없습니다.')
        [void] $help.Add('')
        foreach ($line in (Get-KsmDotnetSdkHelp -Check $sdkCheck)) { [void] $help.Add($line) }

        if ($missingTools.Count -gt 0) {
            [void] $help.Add('')
            [void] $help.Add('SDK 설치 후 아래도 함께 준비하세요(지금 빠져 있습니다):')
            foreach ($item in $missingTools) { [void] $help.Add("  - $item") }
        }

        [void] $help.Add('')
        [void] $help.Add('권장 순서:')
        [void] $help.Add('  1) .NET 10 SDK 설치 후 새 PowerShell 창 열기')
        [void] $help.Add('  2) .\scripts\build-worker.ps1     (임베디드 Python — 기존 Python 불필요)')
        [void] $help.Add('  3) .\scripts\fetch-ffmpeg.ps1')
        [void] $help.Add('  4) .\scripts\build-portable.ps1')

        throw ($help -join [Environment]::NewLine)
    }

    $dotnet = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($missingTools.Count -gt 0) {
        $message = "다음 구성 요소가 없습니다:`n" + (($missingTools | ForEach-Object { "      - $_" }) -join "`n")
        if ($RequireTools) {
            throw $message
        }
        Write-KsmWarn ($message + "`n이 배포본은 그대로는 동작하지 않습니다. -RequireTools 로 강제할 수 있습니다.")
    }

    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'llama\llama-server.exe'))) {
        Write-KsmNote 'tools\llama 가 없습니다 — 로컬 LLM 엔진은 이 배포본에 포함되지 않습니다 (선택 사항).'
    }

    # -----------------------------------------------------------------------
    # 1. dotnet publish
    # -----------------------------------------------------------------------
    if ($SkipPublish) {
        Write-KsmNote '-SkipPublish 이므로 기존 게시 결과를 사용합니다.'
        if (-not (Test-Path -LiteralPath $publishDir)) {
            throw "게시 폴더가 없습니다: $publishDir"
        }
    }
    else {
        Write-KsmStep 'dotnet publish'

        if ($PSCmdlet.ShouldProcess($publishDir, 'KSubMaker.App 게시')) {
            Remove-KsmDirectory -Path $publishDir

            $selfContainedValue = 'true'
            if (-not $SelfContained) { $selfContainedValue = 'false' }

            $publishArgs = @(
                'publish'
                (Join-Path $repoRoot 'src\KSubMaker.App\KSubMaker.App.csproj')
                '-c', $Configuration
                '-r', $Runtime
                "--self-contained", $selfContainedValue
                '-o', $publishDir
                '-p:PublishSingleFile=false'
                '-p:PublishReadyToRun=false'
                '--nologo'
            )

            Write-KsmNote "dotnet $($publishArgs -join ' ')"

            & $dotnet.Source @publishArgs
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish 가 실패했습니다. 종료 코드 $LASTEXITCODE"
            }
        }
    }

    if ($WhatIfPreference) {
        Write-KsmNote '-WhatIf 이므로 복사와 압축은 건너뜁니다.'
        exit 0
    }

    $appExe = Join-Path $publishDir 'KSubMaker.App.exe'
    if (-not (Test-Path -LiteralPath $appExe)) {
        throw "게시 결과에 KSubMaker.App.exe 가 없습니다: $publishDir"
    }

    # -----------------------------------------------------------------------
    # 2. tools\ 복사
    # -----------------------------------------------------------------------
    Write-KsmStep 'tools\ 복사'

    if (Test-Path -LiteralPath $toolsDir) {
        # _downloads 는 중간 산출물이므로 배포본에 넣지 않습니다.
        $copiedTools = Copy-KsmTree -Source $toolsDir -Destination (Join-Path $publishDir 'tools') `
            -ExcludeDirectoryNames @('_downloads', '__pycache__', '.pytest_cache')
        Write-KsmOk "$copiedTools 개 파일을 복사했습니다."
    }
    else {
        Write-KsmWarn "tools\ 가 없습니다: $toolsDir"
    }

    # -----------------------------------------------------------------------
    # 3. 워커 소스 복사
    # -----------------------------------------------------------------------
    Write-KsmStep '워커 페이로드 복사'

    $workerSource = Join-Path $repoRoot 'worker\ksubmaker_worker'
    if (-not (Test-Path -LiteralPath $workerSource)) {
        throw "워커 소스를 찾지 못했습니다: $workerSource"
    }

    $workerTarget = Join-Path $publishDir 'worker\ksubmaker_worker'
    $copiedWorker = Copy-KsmTree -Source $workerSource -Destination $workerTarget `
        -ExcludeDirectoryNames @('__pycache__', '.pytest_cache', 'tests')

    Copy-Item -LiteralPath (Join-Path $repoRoot 'worker\pyproject.toml') `
              -Destination (Join-Path $publishDir 'worker\pyproject.toml') -Force
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'worker\README.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'worker\README.md') `
                  -Destination (Join-Path $publishDir 'worker\README.md') -Force
    }

    Write-KsmOk "$copiedWorker 개 파일을 복사했습니다: worker\ksubmaker_worker"

    # -----------------------------------------------------------------------
    # 4. 문서와 라이선스
    # -----------------------------------------------------------------------
    Write-KsmStep '문서와 라이선스 복사'

    foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md')) {
        $source = Join-Path $repoRoot $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $name) -Force
        }
        else {
            Write-KsmWarn "찾지 못했습니다: $name"
        }
    }

    $docsSource = Join-Path $repoRoot 'docs'
    if (Test-Path -LiteralPath $docsSource) {
        $copiedDocs = Copy-KsmTree -Source $docsSource -Destination (Join-Path $publishDir 'docs')
        Write-KsmNote "문서 $copiedDocs 개 복사"
    }

    # -----------------------------------------------------------------------
    # 5. VERSION.txt
    # -----------------------------------------------------------------------
    Write-KsmStep 'VERSION.txt 작성'

    $ffmpegVersion = '(없음)'
    $ffmpegExe = Join-Path $publishDir 'tools\ffmpeg\bin\ffmpeg.exe'
    if (Test-Path -LiteralPath $ffmpegExe) {
        try {
            $out = Invoke-KsmProcess -FilePath $ffmpegExe -ArgumentList @('-hide_banner', '-version') -TimeoutSeconds 60
            if ($out.ExitCode -eq 0) {
                $ffmpegVersion = (($out.StandardOutput -split "`n")[0]).Trim()
            }
        }
        catch {
            $ffmpegVersion = "(확인 실패: $($_.Exception.Message))"
        }
    }

    $pythonVersion = '(없음)'
    $pythonExe = Join-Path $publishDir 'tools\python\python.exe'
    if (Test-Path -LiteralPath $pythonExe) {
        try {
            $out = Invoke-KsmProcess -FilePath $pythonExe -ArgumentList @('--version') -TimeoutSeconds 60
            if ($out.ExitCode -eq 0) {
                $pythonVersion = (($out.StandardOutput + $out.StandardError)).Trim()
            }
        }
        catch {
            $pythonVersion = "(확인 실패: $($_.Exception.Message))"
        }
    }

    $llamaPresent = '없음 (로컬 LLM 엔진 미포함)'
    if (Test-Path -LiteralPath (Join-Path $publishDir 'tools\llama\llama-server.exe')) {
        $llamaPresent = '포함'
    }

    $versionLines = @(
        'KSubMaker'
        "버전            : $Version"
        "빌드 시각(UTC)  : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))"
        "구성            : $Configuration"
        "런타임          : $Runtime"
        "self-contained  : $SelfContained"
        "빌드 호스트     : $env:COMPUTERNAME"
        ''
        "FFmpeg          : $ffmpegVersion"
        "Python          : $pythonVersion"
        "llama-server    : $llamaPresent"
        ''
        '라이선스: LICENSE (MIT) / THIRD_PARTY_NOTICES.md'
        '  - FFmpeg 는 LGPL 공유 빌드이며 별도 프로세스로 실행됩니다.'
        '  - 기본 번역 모델 NLLB-200 은 CC-BY-NC-4.0 (비상업적 사용) 입니다.'
        ''
        '모든 처리는 로컬에서 이루어지며 모델 다운로드 외에는 인터넷 연결이 필요 없습니다.'
    )

    $versionPath = Join-Path $publishDir 'VERSION.txt'
    Set-Content -LiteralPath $versionPath -Value ($versionLines -join "`r`n") -Encoding UTF8
    Write-KsmOk "작성 완료: $versionPath"

    # -----------------------------------------------------------------------
    # 6. 압축
    # -----------------------------------------------------------------------
    if ($SkipZip) {
        Write-KsmStep '완료 (압축 생략)'
        Write-KsmNote "게시 폴더: $publishDir"
        exit 0
    }

    Write-KsmStep 'zip 압축'

    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    # PowerShell 5.1 의 Compress-Archive 는 2GB 를 넘기지 못하고 매우 느립니다.
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path -LiteralPath $publishDir).ProviderPath,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $zipInfo = Get-Item -LiteralPath $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-KsmStep '완료'
    Write-KsmOk  "산출물   : $zipPath"
    Write-KsmNote ("크기     : {0:N1} MB" -f ($zipInfo.Length / 1MB))
    Write-KsmNote "SHA-256  : $hash"

    Set-Content -LiteralPath ($zipPath + '.sha256') -Value "$hash  $zipName" -Encoding ASCII

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

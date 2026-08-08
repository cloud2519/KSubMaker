<#
.SYNOPSIS
    포터블 빌드를 만든 뒤 Inno Setup 으로 KSubMaker 설치 프로그램을 컴파일합니다.

.DESCRIPTION
    수행 순서:

      1. scripts\build-portable.ps1 -SkipZip 실행
         -> publish\win-x64-Release 에 게시본 + tools\ + worker\ + 문서가 준비됩니다.
      2. ISCC.exe (Inno Setup 컴파일러) 위치 확인
      3. installer\KSubMaker.iss 컴파일
         -> artifacts\KSubMaker-<버전>-setup.exe

    Inno Setup 이 설치되어 있지 않으면 어디서 받을 수 있는지 알려 주고 실패합니다.
    (WiX / MSIX 대신 Inno Setup 을 고른 이유는 docs/DECISIONS.md ADR-012 를 보세요.)

.PARAMETER Configuration
    빌드 구성. 기본값 'Release'.

.PARAMETER Runtime
    런타임 식별자. 기본값 'win-x64'.

.PARAMETER Version
    설치 프로그램 버전. 생략하면 Directory.Build.props 의 <Version> 을 씁니다.

.PARAMETER IsccPath
    ISCC.exe 경로를 직접 지정합니다. 생략하면 표준 설치 위치와 PATH 를 찾습니다.

.PARAMETER OutputDirectory
    설치 프로그램을 만들 폴더. 기본값 <저장소 루트>\artifacts.

.PARAMETER SkipPortableBuild
    포터블 빌드를 건너뛰고 기존 게시 결과를 그대로 씁니다.

.PARAMETER RequireTools
    tools\ffmpeg 나 tools\python 이 없으면 실패시킵니다. 릴리스 빌드에 쓰세요.

.PARAMETER SignTool
    선택. 서명에 사용할 signtool.exe 경로. 지정하면 Inno Setup 에 SignTool 지시문을 넘기는
    대신, 컴파일이 끝난 뒤 산출물에 직접 서명합니다.

.PARAMETER SignToolArguments
    -SignTool 과 함께 쓸 인자 배열. 예: @('sign','/fd','SHA256','/a')
    마지막에 설치 프로그램 경로가 자동으로 덧붙습니다.

.EXAMPLE
    .\scripts\build-installer.ps1

    포터블 빌드 후 설치 프로그램을 만듭니다.

.EXAMPLE
    .\scripts\build-installer.ps1 -SkipPortableBuild -Version '0.1.0'

    이미 만들어 둔 게시 결과로 설치 프로그램만 다시 만듭니다.

.EXAMPLE
    .\scripts\build-installer.ps1 -WhatIf

    무엇을 실행할지만 보여 줍니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Version,
    [string] $IsccPath,
    [string] $OutputDirectory,
    [switch] $SkipPortableBuild,
    [switch] $RequireTools,
    [string] $SignTool,
    [string[]] $SignToolArguments = @('sign', '/fd', 'SHA256', '/a')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

function Find-KsmIscc {
    <#
    .SYNOPSIS
        ISCC.exe (Inno Setup 컴파일러) 를 찾습니다. 없으면 $null.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([string] $Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (Test-Path -LiteralPath $Explicit) {
            return (Resolve-Path -LiteralPath $Explicit).ProviderPath
        }
        throw "지정한 ISCC 경로가 존재하지 않습니다: $Explicit"
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        foreach ($folder in @('Inno Setup 6', 'Inno Setup 5')) {
            $candidates.Add((Join-Path $base (Join-Path $folder 'ISCC.exe')))
        }
    }

    # 레지스트리에 등록된 설치 경로
    foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($item -and $item.InstallLocation) {
                $candidates.Add((Join-Path $item.InstallLocation 'ISCC.exe'))
            }
        }
        catch {
            # 레지스트리 접근 실패는 치명적이지 않습니다.
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).ProviderPath
        }
    }

    $onPath = Get-Command -Name 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) {
        return $onPath.Source
    }

    return $null
}

try {
    $repoRoot = Get-KsmRepoRoot

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-KsmVersion
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repoRoot 'artifacts'
    }

    $publishDir = Join-Path $repoRoot ("publish\{0}-{1}" -f $Runtime, $Configuration)
    $issPath    = Join-Path $repoRoot 'installer\KSubMaker.iss'
    $setupName  = "KSubMaker-$Version-setup"
    $setupPath  = Join-Path $OutputDirectory ($setupName + '.exe')

    Write-KsmStep 'KSubMaker 설치 프로그램 빌드'
    Write-KsmNote "버전      : $Version"
    Write-KsmNote "게시 폴더 : $publishDir"
    Write-KsmNote "스크립트  : $issPath"
    Write-KsmNote "산출물    : $setupPath"

    if (-not (Test-Path -LiteralPath $issPath)) {
        throw "Inno Setup 스크립트를 찾지 못했습니다: $issPath"
    }

    # -----------------------------------------------------------------------
    # ISCC 확인 — 오래 걸리는 빌드를 시작하기 전에 먼저 봅니다.
    # -----------------------------------------------------------------------
    Write-KsmStep 'Inno Setup 컴파일러 확인'

    $iscc = Find-KsmIscc -Explicit $IsccPath

    if (-not $iscc) {
        $message = @(
            'Inno Setup 컴파일러(ISCC.exe)를 찾지 못했습니다.'
            ''
            '설치 프로그램을 만들려면 Inno Setup 6 이 필요합니다.'
            '  - 내려받기: https://jrsoftware.org/isdl.php'
            '  - 기본 설치 경로: C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
            ''
            '이미 설치했다면 -IsccPath 로 ISCC.exe 경로를 직접 지정하세요.'
            '  .\scripts\build-installer.ps1 -IsccPath "D:\Tools\Inno Setup 6\ISCC.exe"'
            ''
            '설치 프로그램 없이 배포하려면 포터블 빌드를 쓰세요.'
            '  .\scripts\build-portable.ps1'
        ) -join [Environment]::NewLine

        throw $message
    }

    Write-KsmOk "ISCC: $iscc"

    # -----------------------------------------------------------------------
    # 포터블 빌드
    # -----------------------------------------------------------------------
    if ($SkipPortableBuild) {
        Write-KsmNote '-SkipPortableBuild 이므로 기존 게시 결과를 사용합니다.'
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'KSubMaker.App.exe'))) {
            throw "게시 결과가 없습니다: $publishDir`n먼저 .\scripts\build-portable.ps1 을 실행하세요."
        }
    }
    else {
        Write-KsmStep '포터블 빌드 실행'

        $portableScript = Join-Path $PSScriptRoot 'build-portable.ps1'
        if (-not (Test-Path -LiteralPath $portableScript)) {
            throw "build-portable.ps1 을 찾지 못했습니다: $portableScript"
        }

        $portableArgs = @{
            Configuration = $Configuration
            Runtime       = $Runtime
            Version       = $Version
            SkipZip       = $true
        }
        if ($RequireTools) { $portableArgs['RequireTools'] = $true }
        if ($WhatIfPreference) { $portableArgs['WhatIf'] = $true }

        & $portableScript @portableArgs

        if ($LASTEXITCODE -ne 0) {
            throw "포터블 빌드가 실패했습니다. 종료 코드 $LASTEXITCODE"
        }
    }

    if ($WhatIfPreference) {
        Write-KsmNote '-WhatIf 이므로 ISCC 컴파일은 건너뜁니다.'
        Write-KsmNote "실행했을 명령: `"$iscc`" /DAppVersion=$Version /DPublishDir=... /DOutputDir=... `"$issPath`""
        exit 0
    }

    # -----------------------------------------------------------------------
    # ISCC 컴파일
    # -----------------------------------------------------------------------
    Write-KsmStep 'Inno Setup 컴파일'

    if (-not (Test-Path -LiteralPath $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    }

    $publishFull = (Resolve-Path -LiteralPath $publishDir).ProviderPath
    $outputFull  = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
    $repoFull    = (Resolve-Path -LiteralPath $repoRoot).ProviderPath

    $isccArgs = @(
        "/DAppVersion=$Version"
        "/DPublishDir=$publishFull"
        "/DOutputDir=$outputFull"
        "/DRepoRoot=$repoFull"
        "/DSetupBaseName=$setupName"
        $issPath
    )

    Write-KsmNote "ISCC $($isccArgs -join ' ')"

    if ($PSCmdlet.ShouldProcess($setupPath, 'Inno Setup 컴파일')) {
        $result = Invoke-KsmProcess -FilePath $iscc -ArgumentList $isccArgs -WorkingDirectory $repoRoot

        # ISCC 는 진행 상황을 stdout 에 씁니다. 실패했을 때만 전부 보여 줍니다.
        if ($result.ExitCode -ne 0) {
            throw ("Inno Setup 컴파일이 실패했습니다. 종료 코드 $($result.ExitCode)`n" +
                   "stdout:`n$($result.StandardOutput)`n" +
                   "stderr:`n$($result.StandardError)")
        }

        $tail = @($result.StandardOutput -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 5)
        foreach ($line in $tail) {
            Write-KsmNote $line.Trim()
        }
    }

    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw ("컴파일은 성공했지만 산출물이 없습니다: $setupPath`n" +
               "installer\KSubMaker.iss 의 OutputBaseFilename 과 -Version 이 맞는지 확인하세요.")
    }

    # -----------------------------------------------------------------------
    # 선택: 서명
    # -----------------------------------------------------------------------
    if (-not [string]::IsNullOrWhiteSpace($SignTool)) {
        Write-KsmStep '코드 서명'

        if (-not (Test-Path -LiteralPath $SignTool)) {
            throw "signtool.exe 를 찾지 못했습니다: $SignTool"
        }

        $signArgs = @($SignToolArguments) + @($setupPath)
        $signResult = Invoke-KsmProcess -FilePath $SignTool -ArgumentList $signArgs -WorkingDirectory $repoRoot

        if ($signResult.ExitCode -ne 0) {
            throw ("코드 서명이 실패했습니다. 종료 코드 $($signResult.ExitCode)`n" +
                   $signResult.StandardOutput + "`n" + $signResult.StandardError)
        }

        Write-KsmOk '서명 완료.'
    }
    else {
        Write-KsmNote '코드 서명을 하지 않았습니다. 설치 시 SmartScreen 경고가 표시됩니다.'
    }

    # -----------------------------------------------------------------------
    # 결과
    # -----------------------------------------------------------------------
    $setupInfo = Get-Item -LiteralPath $setupPath
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($setupPath + '.sha256') -Value "$hash  $($setupInfo.Name)" -Encoding ASCII

    Write-KsmStep '완료'
    Write-KsmOk  "산출물  : $setupPath"
    Write-KsmNote ("크기    : {0:N1} MB" -f ($setupInfo.Length / 1MB))
    Write-KsmNote "SHA-256 : $hash"

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

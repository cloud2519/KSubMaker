<#
.SYNOPSIS
    KSubMaker 가 사용하는 FFmpeg (LGPL 공유 빌드, Windows x64) 를 tools/ffmpeg/bin 에 설치합니다.

.DESCRIPTION
    ffmpeg.exe / ffprobe.exe 와 함께 필요한 공유 DLL 을 저장소의

        tools\ffmpeg\bin\

    아래에 배치합니다. 이 경로는 KSubMaker.Worker.Tools.ToolLocator 와
    worker/ksubmaker_worker/ffmpeg_service.py 가 **가장 먼저** 찾는 위치이며,
    두 구현 모두 다음 순서로 탐색합니다.

        tools\ffmpeg\bin  ->  tools  ->  앱 폴더  ->  PATH

    PATH 에서 찾으면 경고 로그를 남깁니다. 프로덕션에서 그 경고가 보이면 배포가 깨진 것입니다.

    라이선스 (중요):
      FFmpeg 는 같은 소스에서 LGPL 또는 GPL 로 빌드될 수 있습니다. KSubMaker 는
      **LGPL 공유(shared) 빌드**만 사용하며, 링크하지 않고 별도 프로세스로 실행합니다.
      그래서 KSubMaker 자체를 MIT 로 배포할 수 있습니다.

      GPL 빌드(--enable-gpl, x264/x265 포함 등)로 바꿔치기하면 그 분석이 무효가 되고
      배포 조건이 달라집니다. 자세한 내용은 THIRD_PARTY_NOTICES.md 를 보세요.

      이 스크립트의 기본 자산 패턴은 'lgpl-shared' 를 포함하는 자산만 고릅니다.
      -AssetPattern 을 바꿔 GPL 빌드를 받으면 경고를 출력합니다.

    다운로드 주소는 문서에 박아 두지 않고 GitHub 릴리스 API 로 **찾습니다**. 상류에서
    파일 이름이 바뀌어도 패턴만 조정하면 되고, 존재하지 않는 URL 을 지어내지 않습니다.
    완전히 재현 가능한 빌드를 원하면 -Url 과 -Sha256 을 함께 고정하세요.

.PARAMETER Url
    받을 압축 파일의 HTTPS 주소를 직접 지정합니다. 지정하면 릴리스 조회를 건너뜁니다.
    재현 가능한 릴리스 빌드에서는 -Sha256 과 함께 쓰세요.

.PARAMETER Repository
    자산을 찾을 GitHub 저장소 ('owner/name'). 기본값은 Windows용 FFmpeg 빌드를 배포하는
    BtbN/FFmpeg-Builds 입니다.

.PARAMETER Tag
    릴리스 태그. 기본값 'latest'.

.PARAMETER AssetPattern
    자산 이름 와일드카드. 기본값은 LGPL 공유 빌드 x64 를 고릅니다.

.PARAMETER Sha256
    기대하는 SHA-256. 지정하면 검증하고, 다르면 중단합니다. 생략하면 계산해서 출력만 합니다.
    (출력된 값을 다음 실행부터 고정하는 것이 권장 흐름입니다.)

.PARAMETER GitHubToken
    선택. GitHub API 요청 한도를 늘립니다.

.PARAMETER Force
    tools\ffmpeg 가 이미 있어도 지우고 다시 설치합니다.

.PARAMETER KeepArchive
    받은 압축 파일을 tools\_downloads 에 남깁니다. 기본값은 삭제입니다.

.EXAMPLE
    .\scripts\fetch-ffmpeg.ps1

    최신 LGPL 공유 빌드를 받아 tools\ffmpeg\bin 에 설치하고 SHA-256 을 출력합니다.

.EXAMPLE
    .\scripts\fetch-ffmpeg.ps1 -Url 'https://example.invalid/ffmpeg-lgpl-shared.zip' -Sha256 'abc…' -Force

    주소와 해시를 고정해 재현 가능하게 설치합니다.

.EXAMPLE
    .\scripts\fetch-ffmpeg.ps1 -WhatIf

    무엇을 받아 어디에 쓸지만 보여 주고 아무것도 바꾸지 않습니다.

.NOTES
    PowerShell 5.1 호환. 실패 시 0 이 아닌 종료 코드로 끝납니다.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Url,
    [string] $Repository = 'BtbN/FFmpeg-Builds',
    [string] $Tag = 'latest',
    [string] $AssetPattern = '*win64-lgpl-shared*.zip',
    [string] $Sha256,
    [string] $GitHubToken,
    [switch] $Force,
    [switch] $KeepArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot '_common.ps1')

try {
    $repoRoot  = Get-KsmRepoRoot
    $toolsDir  = Get-KsmToolsDirectory -Create
    $targetDir = Join-Path $toolsDir 'ffmpeg'
    $binDir    = Join-Path $targetDir 'bin'

    Write-KsmStep 'FFmpeg (LGPL 공유 빌드) 설치'
    Write-KsmNote "저장소 루트 : $repoRoot"
    Write-KsmNote "설치 위치   : $binDir"

    # -- 라이선스 가드 ------------------------------------------------------
    if ($AssetPattern -notlike '*lgpl*') {
        Write-KsmWarn (
            "자산 패턴에 'lgpl' 이 없습니다: $AssetPattern`n" +
            "KSubMaker 는 LGPL 공유 빌드를 전제로 배포됩니다. GPL 빌드를 넣으면 " +
            "THIRD_PARTY_NOTICES.md 의 라이선스 분석이 무효가 됩니다.")
    }
    if ($AssetPattern -like '*gpl*' -and $AssetPattern -notlike '*lgpl*') {
        Write-KsmWarn 'GPL 빌드로 보이는 패턴입니다. 배포 전에 라이선스 영향을 반드시 검토하세요.'
    }

    # -- 기존 설치 확인 -----------------------------------------------------
    if ((Test-Path -LiteralPath $targetDir) -and -not $Force) {
        $existing = Join-Path $binDir 'ffmpeg.exe'
        if (Test-Path -LiteralPath $existing) {
            Write-KsmOk "이미 설치되어 있습니다: $existing"
            Write-KsmNote '다시 설치하려면 -Force 를 지정하세요.'
            exit 0
        }
        Write-KsmWarn "tools\ffmpeg 가 있지만 ffmpeg.exe 가 없습니다. 다시 설치합니다."
    }

    # -- 받을 자산 결정 -----------------------------------------------------
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
            $downloadName = 'ffmpeg.zip'
        }
    }

    if ($downloadName -notlike '*lgpl*') {
        Write-KsmWarn "자산 이름에 'lgpl' 이 없습니다: $downloadName — 라이선스 구성을 직접 확인하세요."
    }

    # -- 다운로드 -----------------------------------------------------------
    $downloadDir = Join-Path $toolsDir '_downloads'
    $archivePath = Join-Path $downloadDir $downloadName

    if (-not (Test-Path -LiteralPath $downloadDir)) {
        if ($PSCmdlet.ShouldProcess($downloadDir, '디렉터리 생성')) {
            New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
        }
    }

    Invoke-KsmDownload -Uri $downloadUrl -OutFile $archivePath -Sha256 $Sha256 | Out-Null

    if ($WhatIfPreference) {
        Write-KsmNote '-WhatIf 이므로 압축 해제와 배치는 건너뜁니다.'
        exit 0
    }

    # -- 압축 해제 ----------------------------------------------------------
    $stagingDir = Join-Path $downloadDir 'ffmpeg-staging'
    Remove-KsmDirectory -Path $stagingDir
    Expand-KsmArchive -ArchivePath $archivePath -Destination $stagingDir

    # 배포본은 대개 ffmpeg-<버전>-win64-lgpl-shared\bin\... 처럼 한 겹 감싸여 있습니다.
    $sourceBin = $null
    $ffmpegExe = Get-ChildItem -LiteralPath $stagingDir -Filter 'ffmpeg.exe' -Recurse -File -ErrorAction SilentlyContinue |
                 Select-Object -First 1
    if ($ffmpegExe) {
        $sourceBin = $ffmpegExe.DirectoryName
    }

    if (-not $sourceBin) {
        throw "압축을 푼 결과에서 ffmpeg.exe 를 찾지 못했습니다: $stagingDir"
    }

    $ffprobeExe = Join-Path $sourceBin 'ffprobe.exe'
    if (-not (Test-Path -LiteralPath $ffprobeExe)) {
        throw "ffprobe.exe 가 없습니다. KSubMaker 는 컨테이너 메타데이터를 읽는 데 ffprobe 를 반드시 사용합니다. ($sourceBin)"
    }

    # -- 배치 ---------------------------------------------------------------
    Remove-KsmDirectory -Path $targetDir
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null

    # ffmpeg.exe / ffprobe.exe 와, 공유 빌드가 필요로 하는 모든 DLL 을 같은 폴더에 둡니다.
    # ffplay 는 쓰지 않으므로 제외합니다 (SDL 의존성까지 따라옵니다).
    $copied = 0
    Get-ChildItem -LiteralPath $sourceBin -File | ForEach-Object {
        $name = $_.Name.ToLowerInvariant()
        if ($name -eq 'ffplay.exe') { return }
        if ($name -like 'sdl*.dll') { return }
        if ($_.Extension -notin @('.exe', '.dll')) { return }

        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $binDir $_.Name) -Force
        $copied++
    }

    # 라이선스 파일은 LGPL 의무이므로 반드시 함께 둡니다.
    $licenseNames = @('LICENSE', 'LICENSE.txt', 'COPYING.LGPLv2.1', 'COPYING.LGPLv3',
                      'COPYING.GPLv2', 'COPYING.GPLv3', 'README.txt')
    $licenseRoot = Split-Path -Parent $sourceBin
    foreach ($candidate in $licenseNames) {
        $path = Join-Path $licenseRoot $candidate
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $targetDir $candidate) -Force
        }
    }
    Get-ChildItem -LiteralPath $licenseRoot -Directory -Filter 'LICENSE*' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $targetDir -Recurse -Force }

    Remove-KsmDirectory -Path $stagingDir
    if (-not $KeepArchive) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    }

    # -- 검증 ---------------------------------------------------------------
    $installedFfmpeg  = Join-Path $binDir 'ffmpeg.exe'
    $installedFfprobe = Join-Path $binDir 'ffprobe.exe'

    foreach ($required in @($installedFfmpeg, $installedFfprobe)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "설치 후에도 파일이 없습니다: $required"
        }
    }

    Write-KsmOk "$copied 개 파일을 배치했습니다: $binDir"

    $version = Invoke-KsmProcess -FilePath $installedFfmpeg -ArgumentList @('-hide_banner', '-version') -TimeoutSeconds 60
    if ($version.ExitCode -ne 0) {
        throw "설치한 ffmpeg 를 실행하지 못했습니다. 종료 코드 $($version.ExitCode)`n$($version.StandardError)"
    }

    $firstLine = ($version.StandardOutput -split "`n")[0].Trim()
    Write-KsmOk $firstLine

    if ($version.StandardOutput -match '--enable-gpl') {
        Write-KsmWarn (
            "이 빌드는 --enable-gpl 로 구성되어 있습니다 (GPL 빌드).`n" +
            "KSubMaker 의 라이선스 분석은 LGPL 빌드를 전제로 합니다. " +
            "배포 전에 THIRD_PARTY_NOTICES.md 를 검토하고 필요하면 LGPL 빌드로 교체하세요.")
    }
    elseif ($version.StandardOutput -match '--enable-version3|configuration:') {
        Write-KsmOk 'GPL 구성 플래그가 발견되지 않았습니다 (LGPL 빌드로 보입니다).'
    }

    $probeVersion = Invoke-KsmProcess -FilePath $installedFfprobe -ArgumentList @('-hide_banner', '-version') -TimeoutSeconds 60
    if ($probeVersion.ExitCode -ne 0) {
        throw "설치한 ffprobe 를 실행하지 못했습니다. 종료 코드 $($probeVersion.ExitCode)"
    }

    Write-KsmStep '완료'
    Write-KsmNote "ffmpeg  : $installedFfmpeg"
    Write-KsmNote "ffprobe : $installedFfprobe"
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

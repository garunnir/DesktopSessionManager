<#
.SYNOPSIS
  배포 폴더를 만듭니다 (self-contained 단일 EXE + vda/ + plugins/ + integrations/ + THIRD_PARTY_NOTICES.md).

.DESCRIPTION
  build.cmd와 릴리즈 워크플로가 같이 씁니다.
  -Zip을 주면 배포 폴더를 DesktopSessionManager-<버전>-win-x64.zip으로 묶고,
  같은 폴더에 릴리즈 노트(release-notes.md: 지원 Windows 빌드 표, SHA256)를 만듭니다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package.ps1
  powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 1.2.0 -Out out\dist -Zip
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0',
    [string]$Out,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $Root 'dist' }
$Out = [IO.Path]::GetFullPath($Out)

# 앱 설정(plugins/)을 덮어쓰지 않도록 기존 폴더는 지우지 않습니다. 릴리즈는 빈 폴더에서 만듭니다.
dotnet publish (Join-Path $Root 'DesktopSessionManager.csproj') -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:Version=$Version -o $Out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 실패' }

Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') $Out -Force
foreach ($dir in 'plugins', 'integrations') {
    Copy-Item (Join-Path $Root $dir) $Out -Recurse -Force
}
Write-Host "배포 폴더: $Out"
if (-not $Zip) { exit 0 }

$name = "DesktopSessionManager-$Version-win-x64"
$zipPath = Join-Path (Split-Path -Parent $Out) "$name.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# 압축 루트에 버전 폴더를 두어 풀었을 때 파일이 흩어지지 않게 합니다.
$stage = Join-Path ([IO.Path]::GetTempPath()) "package-$([guid]::NewGuid())"
Copy-Item $Out (Join-Path $stage $name) -Recurse
Get-ChildItem (Join-Path $stage $name) -Filter *.pdb | Remove-Item -Force
Compress-Archive -Path (Join-Path $stage $name) -DestinationPath $zipPath
Remove-Item $stage -Recurse -Force
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

# 릴리즈 노트: 이 버전이 지원하는 Windows 빌드 (csproj VdaRelease 테이블 기준)
$csproj = [IO.File]::ReadAllText((Join-Path $Root 'DesktopSessionManager.csproj'))
$aliases = @{}
foreach ($m in [regex]::Matches($csproj, '<VdaBuildAlias Include="(\d+)" Base="(\d+)"')) {
    $aliases[$m.Groups[2].Value] = @($aliases[$m.Groups[2].Value]) + $m.Groups[1].Value | Where-Object { $_ }
}
$rows = foreach ($m in [regex]::Matches($csproj, '<VdaRelease Include="([^"]+)" Build="(\d+)" MinUbr="(\d+)"')) {
    $build = $m.Groups[2].Value
    $builds = (@($build) + @($aliases[$build] | Where-Object { $_ })) -join ', '
    "| $builds | $($m.Groups[3].Value) 이상 | ``$($m.Groups[1].Value)`` |"
}
$notes = @"
## 지원 Windows

실행할 때 Windows 빌드에 맞는 VirtualDesktopAccessor DLL을 골라 SHA256을 확인한 뒤 로드합니다.
아래 표에 없는 Windows 빌드(Windows 10, 11 21H2 등)에서는 가상 데스크톱 기능이 꺼집니다.

| 빌드 | UBR | VirtualDesktopAccessor |
|---|---|---|
$($rows -join "`n")

## 설치

``$name.zip``을 원하는 폴더에 풀고 ``DesktopSessionManager.exe``를 실행하세요. 설치 과정은 없습니다.
플러그인 설정이 EXE 옆 ``plugins/``에 저장되므로 ``C:\Program Files``처럼 쓰기 권한이 없는 곳은 피하세요.
서명되지 않은 EXE라 처음 실행할 때 SmartScreen 경고가 뜨면 [추가 정보] → [실행]을 누르세요.

업데이트할 때는 새 zip을 풀고 기존 ``plugins/``의 수정한 설정만 옮기면 됩니다. 프로필은 ``%LOCALAPPDATA%\DesktopSessionManager``에 있어 그대로 유지됩니다.

SHA256 (``$name.zip``): ``$sha``
"@
$notesPath = Join-Path (Split-Path -Parent $Out) 'release-notes.md'
[IO.File]::WriteAllText($notesPath, $notes, (New-Object Text.UTF8Encoding($false)))
Write-Host "압축: $zipPath"
Write-Host "SHA256: $sha"
Write-Host "릴리즈 노트: $notesPath"

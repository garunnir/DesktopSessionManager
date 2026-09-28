<#
.SYNOPSIS
  VirtualDesktopAccessor 새 릴리즈를 감지·검증해 csproj의 호환 테이블(VdaRelease)에 추가합니다.

.DESCRIPTION
  VDA는 Windows 빌드마다 전용 DLL이 필요합니다. 앱은 csproj의 VdaRelease 테이블에서 실행 중인
  Windows에 맞는 DLL을 고르므로, 새 릴리즈는 기존 항목을 바꾸지 않고 테이블에 추가합니다.

  기본 동작
    1. csproj의 VdaRelease/VdaIgnoredRelease에 없는 정식 릴리즈를 찾습니다.
    2. 각 릴리즈의 DLL을 검증합니다.
       - GitHub asset digest(sha256) 일치 (제공되는 경우)
       - x64 PE DLL
       - Program.cs가 DllImport하는 모든 EntryPoint export (DLL은 로드하지 않고 PE를 직접 파싱)
    3. 릴리즈 노트의 "22631.3085" 같은 빌드 번호로 Build/MinUbr를 추론합니다.
       추론하지 못하면 "?"로 넣고 needs_review=true를 출력합니다 (그 상태로는 빌드가 실패합니다).
    4. csproj와 THIRD_PARTY_NOTICES.md에 항목을 추가합니다.

  -VerifyAll: 테이블의 모든 항목을 다시 내려받아 SHA256과 export를 검증합니다 (CI용).
  -CheckOnly: 새 릴리즈가 있는지만 출력합니다.

  GitHub Actions에서는 GITHUB_OUTPUT에 updated/tags/needs_review/notes_path를 기록합니다.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\update-vda.ps1
  powershell -ExecutionPolicy Bypass -File scripts\update-vda.ps1 -CheckOnly
  powershell -ExecutionPolicy Bypass -File scripts\update-vda.ps1 -VerifyAll
  powershell -ExecutionPolicy Bypass -File scripts\update-vda.ps1 -Tag 2024-12-16-windows11
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [switch]$CheckOnly,
    [switch]$VerifyAll
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'Ciantic/VirtualDesktopAccessor'
$AssetName = 'VirtualDesktopAccessor.dll'
$Root = Split-Path -Parent $PSScriptRoot
$Csproj = Join-Path $Root 'DesktopSessionManager.csproj'
$Notices = Join-Path $Root 'THIRD_PARTY_NOTICES.md'
$Headers = @{ 'Accept' = 'application/vnd.github+json'; 'User-Agent' = 'DesktopSessionManager-update-vda' }
if ($env:GITHUB_TOKEN) { $Headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$TmpRoot = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'update-vda'

function Set-Output([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8 }
}

function Read-Text([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = (New-Object Text.UTF8Encoding($false)).GetString($bytes)
    if ($bom) { $text = $text.Substring(1) }
    return @{ Text = $text; Bom = $bom }
}

function Write-Text([string]$Path, [string]$Text, [bool]$Bom) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($Bom)))
}

# PE export 테이블에서 함수 이름 목록과 머신 타입을 읽습니다 (DLL 코드는 실행하지 않음).
function Get-PeExports([string]$Path) {
    $b = [IO.File]::ReadAllBytes($Path)
    if ([BitConverter]::ToUInt16($b, 0) -ne 0x5A4D) { throw 'MZ 헤더가 아닙니다.' }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ([BitConverter]::ToUInt32($b, $pe) -ne 0x4550) { throw 'PE 시그니처가 아닙니다.' }
    $machine = [BitConverter]::ToUInt16($b, $pe + 4)
    $sectionCount = [BitConverter]::ToUInt16($b, $pe + 6)
    $optSize = [BitConverter]::ToUInt16($b, $pe + 20)
    $opt = $pe + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $dirOffset = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $exportRva = [BitConverter]::ToUInt32($b, $dirOffset)
    $sections = $opt + $optSize

    $toOffset = {
        param([uint32]$rva)
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $s = $sections + $i * 40
            $va = [BitConverter]::ToUInt32($b, $s + 12)
            $size = [Math]::Max([BitConverter]::ToUInt32($b, $s + 8), [BitConverter]::ToUInt32($b, $s + 16))
            if ($rva -ge $va -and $rva -lt $va + $size) { return [int]($rva - $va + [BitConverter]::ToUInt32($b, $s + 20)) }
        }
        throw "RVA 0x{0:X}를 파일 오프셋으로 변환할 수 없습니다." -f $rva
    }

    $names = @()
    if ($exportRva -ne 0) {
        $exp = & $toOffset $exportRva
        $count = [BitConverter]::ToUInt32($b, $exp + 24)
        $namesTable = & $toOffset ([BitConverter]::ToUInt32($b, $exp + 32))
        for ($i = 0; $i -lt $count; $i++) {
            $p = & $toOffset ([BitConverter]::ToUInt32($b, $namesTable + $i * 4))
            $end = [Array]::IndexOf($b, [byte]0, $p)
            $names += [Text.Encoding]::ASCII.GetString($b, $p, $end - $p)
        }
    }
    return @{ Machine = $machine; Exports = $names }
}

function Get-Dll([string]$ReleaseTag) {
    $dir = Join-Path $TmpRoot $ReleaseTag
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dll = Join-Path $dir $AssetName
    if (-not (Test-Path $dll)) {
        $url = "https://github.com/$Repo/releases/download/$ReleaseTag/$AssetName"
        Invoke-WebRequest -Uri $url -OutFile $dll -UseBasicParsing -Headers @{ 'User-Agent' = $Headers['User-Agent'] }
    }
    return $dll
}

# x64 PE이고 Program.cs가 쓰는 export가 모두 있는지 확인합니다.
function Test-Dll([string]$ReleaseTag, [string]$Dll) {
    $pe = Get-PeExports $Dll
    if ($pe.Machine -ne 0x8664) { throw ("{0}: x64 DLL이 아닙니다 (machine 0x{1:X})." -f $ReleaseTag, $pe.Machine) }
    $missing = $RequiredExports | Where-Object { $pe.Exports -notcontains $_ }
    if ($missing) { throw "${ReleaseTag}: 없는 export: $($missing -join ', ')" }
    Write-Host "  검증: x64 PE, export $($RequiredExports.Count)개 모두 존재"
}

# --- 현재 테이블 ---
$csprojDoc = Read-Text $Csproj
$table = @([regex]::Matches($csprojDoc.Text, '<VdaRelease Include="([^"]+)" Build="([^"]*)" MinUbr="([^"]*)" Sha256="([^"]*)"') |
    ForEach-Object { [pscustomobject]@{ Tag = $_.Groups[1].Value; Build = $_.Groups[2].Value; MinUbr = $_.Groups[3].Value; Sha256 = $_.Groups[4].Value } })
$ignored = @([regex]::Matches($csprojDoc.Text, '<VdaIgnoredRelease Include="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$aliases = @{}
[regex]::Matches($csprojDoc.Text, '<VdaBuildAlias Include="(\d+)" Base="(\d+)"') | ForEach-Object { $aliases[$_.Groups[1].Value] = $_.Groups[2].Value }
if (-not $table) { throw 'csproj에서 VdaRelease 항목을 찾지 못했습니다.' }

$programText = (Read-Text (Join-Path $Root 'Program.cs')).Text
$RequiredExports = @([regex]::Matches($programText, 'DllImport\("VirtualDesktopAccessor\.dll"[^\]]*EntryPoint\s*=\s*"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if (-not $RequiredExports) { throw 'Program.cs에서 VirtualDesktopAccessor EntryPoint를 찾지 못했습니다.' }

if ($VerifyAll) {
    foreach ($entry in $table) {
        Write-Host "$($entry.Tag) (Windows $($entry.Build).$($entry.MinUbr)+)"
        if ($entry.Build -notmatch '^\d+$' -or $entry.MinUbr -notmatch '^\d+$') { throw "$($entry.Tag): Build/MinUbr가 숫자가 아닙니다." }
        $dll = Get-Dll $entry.Tag
        $sha = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -ne $entry.Sha256.ToLowerInvariant()) { throw "$($entry.Tag): SHA256 불일치 ($sha)" }
        Write-Host '  검증: SHA256 일치'
        Test-Dll $entry.Tag $dll
    }
    Write-Host "모든 항목 검증 완료 ($($table.Count)개)"
    exit 0
}

# --- 새 릴리즈 찾기 ---
# Windows PowerShell 5.1의 Invoke-RestMethod는 배열을 한 객체로 내보내므로 변수에 받은 뒤 펼칩니다.
if ($Tag) {
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $Headers
    $releases = @($response)
} else {
    $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers $Headers
    $releases = @($response | ForEach-Object { $_ } | Where-Object { -not $_.draft -and -not $_.prerelease })
}
$known = @($table | ForEach-Object { $_.Tag }) + $ignored
$new = @($releases | Where-Object { $known -notcontains $_.tag_name } | Sort-Object published_at)

Write-Host "테이블: $(($table | ForEach-Object { $_.Tag }) -join ', ')"
if (-not $new) {
    Write-Host '새 릴리즈가 없습니다.'
    Set-Output 'updated' 'false'
    exit 0
}
if ($CheckOnly) {
    $new | ForEach-Object { Write-Host "새 릴리즈: $($_.tag_name) $($_.html_url)" }
    Set-Output 'updated' 'false'
    exit 0
}

# --- 검증 후 추가 ---
$noticesDoc = Read-Text $Notices
$csprojText = $csprojDoc.Text
$noticesText = $noticesDoc.Text
$needsReview = $false
$sections = @()
foreach ($release in $new) {
    $t = $release.tag_name
    Write-Host "$t ($($release.published_at))"
    $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) { throw "${t}: $AssetName 가 없습니다. 테이블에 넣지 않으려면 csproj에 VdaIgnoredRelease로 추가하세요." }
    $dll = Get-Dll $t
    $sha = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  SHA256: $sha"
    $digestNote = 'GitHub asset digest 미제공, 건너뜀'
    if ($asset.digest) {
        if ($asset.digest -ne "sha256:$sha") { throw "${t}: GitHub asset digest 불일치 ($($asset.digest))" }
        $digestNote = 'GitHub asset digest 일치'
        Write-Host "  검증: $digestNote"
    }
    Test-Dll $t $dll

    # 릴리즈 노트의 "26100.2605" 같은 빌드 번호에서 지원 범위를 추론합니다.
    $found = @([regex]::Matches([string]$release.body, '\b(2\d{4})\.(\d{1,5})\b') | ForEach-Object {
        $b = $_.Groups[1].Value
        if ($aliases.ContainsKey($b)) { $b = $aliases[$b] }
        [pscustomobject]@{ Build = $b; Ubr = [int]$_.Groups[2].Value }
    })
    $builds = @($found | ForEach-Object { $_.Build } | Sort-Object -Unique)
    if ($builds.Count -eq 1) {
        $build = $builds[0]
        $minUbr = [string](($found | Measure-Object -Property Ubr -Minimum).Minimum)
        $buildNote = "릴리즈 노트에서 추론: $build.$minUbr 이상"
        $supported = "$build.$minUbr+"
    } else {
        $build = '?'; $minUbr = '?'; $needsReview = $true
        $buildNote = if ($builds) { "릴리즈 노트에 빌드 계열이 여러 개입니다 ($($builds -join ', ')). 직접 정하세요." } else { '릴리즈 노트에 빌드 번호가 없습니다. 직접 정하세요.' }
        $supported = '확인 필요'
    }
    Write-Host "  지원 빌드: $buildNote"

    # 테이블은 최신순이므로 첫 항목 앞에 넣습니다.
    $line = "<VdaRelease Include=`"$t`" Build=`"$build`" MinUbr=`"$minUbr`" Sha256=`"$sha`" />"
    $nl = if ($csprojText -match "`r`n") { "`r`n" } else { "`n" }
    $csprojText = ([regex]'(?m)^([ \t]*)(?=<VdaRelease )').Replace($csprojText, { param($m) "$($m.Value)$line$nl$($m.Value)" }, 1)
    $row = "| ``$t`` | $supported | ``$sha`` |"
    $nl = if ($noticesText -match "`r`n") { "`r`n" } else { "`n" }
    $noticesText = ([regex]'(?m)^\|---\|---\|---\|(?=\r?$)').Replace($noticesText, { param($m) "$($m.Value)$nl$row" }, 1)

    $sections += @"
## [$t]($($release.html_url))

- SHA256: ``$sha``
- 자동 검증: $digestNote / x64 PE / export 존재 ($(($RequiredExports | ForEach-Object { "``$_``" }) -join ', '))
- **지원 Windows 빌드: $buildNote**

<details><summary>릴리즈 노트</summary>

$($release.body)

</details>
"@
}
if ($csprojText -notmatch [regex]::Escape($new[-1].tag_name)) { throw 'csproj에 항목을 넣지 못했습니다.' }
if ($noticesText -notmatch [regex]::Escape($new[-1].tag_name)) { throw 'THIRD_PARTY_NOTICES.md 표를 찾지 못했습니다.' }
Write-Text $Csproj $csprojText $csprojDoc.Bom
Write-Text $Notices $noticesText $noticesDoc.Bom

# --- PR 본문 ---
$tags = ($new | ForEach-Object { $_.tag_name }) -join ', '
$check = if ($needsReview) {
    "> [!WARNING]`n> 지원 Windows 빌드를 추론하지 못했습니다. csproj의 ``Build``/``MinUbr``와 THIRD_PARTY_NOTICES.md를 릴리즈 노트대로 채우세요. 그 전까지는 빌드가 실패합니다.`n"
} else {
    "- [x] ``dotnet build`` (테이블의 모든 DLL 다운로드 + SHA256 확인)`n"
}
$body = @"
[$Repo](https://github.com/$Repo) 새 릴리즈를 VdaRelease 테이블에 추가합니다. 기존 항목은 그대로 두므로 이전 Windows 빌드 지원은 유지됩니다.

$check
### 머지 전 확인
- [ ] 추론한 지원 빌드(Build/MinUbr)가 릴리즈 노트와 맞는지. 같은 빌드 계열의 기존 항목 범위가 새 항목 때문에 잘못 좁아지지 않는지
- [ ] 새 빌드 번호가 기존 빌드의 enablement package라면 csproj에 ``VdaBuildAlias``를 추가
- [ ] 해당 Windows에서 실제로 세션 저장·복원 확인

$($sections -join "`n")
"@
New-Item -ItemType Directory -Force -Path $TmpRoot | Out-Null
$notes = Join-Path $TmpRoot 'pr-body.md'
Write-Text $notes $body $false
Set-Output 'tags' $tags
Set-Output 'needs_review' $(if ($needsReview) { 'true' } else { 'false' })
Set-Output 'notes_path' $notes
Set-Output 'updated' 'true'
Write-Host "추가: $tags"

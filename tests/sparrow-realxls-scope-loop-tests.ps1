#requires -Version 5.1
<#
    REAL-XLS SCOPE LOOP regression for [XLS 분리] directory/file scope selection (team collaboration).

    Reconstructs the REAL project structure from the MyApp Sparrow xls into a MIRROR checkout at a
    DIFFERENT drive/root (a temp dir) — i.e. a teammate whose checkout path differs from the path baked
    into the shared xls — and drives many folder/file scope selections through SparrowXlsExport
    (--files-from + --root), asserting the cross-PC (Tier-2 relative-tail) matcher narrows exactly to the
    selection: correct per-folder counts, directory-boundary correctness (View vs ViewModel), single file,
    disjoint union, full selection, wrong-selection [범위 불일치] diagnostic, mixed real+ghost, idempotency,
    and no cross-folder leakage.

    검증은 산출물(<out>\<체커 키>\{ID}_{파일명}_{라인}.md)을 직접 읽어 수행한다 — 익스포터는 index.csv를
    만들지 않으므로 각 md 필드표의 '경로' 행에서 검출 경로를 얻는다.

    The .xls is NOT in the repo (it lives in Downloads). SELF-SKIPS (not fails) when the .xls or .NET SDK
    is absent. Run: validate.ps1 -IncludeSparrowRealXlsScopeLoopTests   (or run this file directly).

    xls 경로 결정 순서(명시 opt-in 우선):
      1) -XlsPath <path>
      2) $env:SPARROW_TEST_XLS
      3) %USERPROFILE%\Downloads\issues_*.xls 중 최신 (자동 탐색)
         → -NoAutoDiscover 또는 $env:SPARROW_TEST_XLS_NO_AUTODISCOVER=1 로 끌 수 있다.

    [보안] stdout 은 공유물이다(tests\_logs\validate-*.log → CONTRIBUTING 이 실패 신고 시 첨부를 지시,
    레포는 public). 그래서 xls 경로/파일명뿐 아니라 xls 에서 뽑아낸 '사내 폴더/파일 경로'도 찍지 않는다 —
    'MyApp\Service' / 'MyApp\View\SomeView.xaml.cs' 같은 단정 메시지는 그 자체가 사내 프로젝트 구조
    노출이다. 대신 이 실행 안에서만 유효한 익명 라벨(폴더 #1, 파일 #1)로 부르고, 기대/실 건수는 그대로 찍는다
    (무엇을 검증하는지는 불변 — 판정 로직·건수는 손대지 않았다).
    라벨 ↔ 실제 경로 대응표는 로컬 파일(tests\_logs\scopeloop-labels-<stamp>.txt, .gitignore 대상)에만 쓰고,
    stdout 에는 그 파일의 상대 경로만 알린다. 그 파일 첫 줄에 "당신의 소스 구조가 들어 있으니 공유 전 확인"
    경고를 박는다.
#>
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # 명시 지정용. 비우면 $env:SPARROW_TEST_XLS → Downloads 자동 탐색 순으로 해석한다.
    # (특정 파일명을 박으면 실 산출물 파일명이 사람/회차마다 달라 늘 self-skip 으로 죽는다.)
    [string]$XlsPath,
    [switch]$NoAutoDiscover
)

$ErrorActionPreference = 'Stop'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "dotnet SDK not found; skipping scope-loop tests."
    $global:SparrowTestSkip = "dotnet SDK 없음"
    return
}

# ---- xls 경로 해석 (경로/파일명은 로그에 남기지 않는다) ----
$xlsOrigin = ''
if (-not [string]::IsNullOrWhiteSpace($XlsPath)) {
    $xlsOrigin = '-XlsPath 로 명시 지정됨'
}
elseif (-not [string]::IsNullOrWhiteSpace($env:SPARROW_TEST_XLS)) {
    $XlsPath = $env:SPARROW_TEST_XLS
    $xlsOrigin = '$env:SPARROW_TEST_XLS 로 명시 지정됨'
}
elseif ($NoAutoDiscover -or (-not [string]::IsNullOrWhiteSpace($env:SPARROW_TEST_XLS_NO_AUTODISCOVER))) {
    $XlsPath = ''
    $xlsOrigin = '자동 탐색 비활성(-NoAutoDiscover)'
}
else {
    $XlsPath = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Downloads') -Filter 'issues_*.xls' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    $xlsOrigin = '자동 탐색됨: Downloads\issues_*.xls 중 최신'
}

if ([string]::IsNullOrWhiteSpace($XlsPath) -or -not (Test-Path -LiteralPath $XlsPath)) {
    Write-Host "Sparrow xls not found; skipping scope-loop tests (the .xls is not in the repo)."
    Write-Host ("  xls 후보: <{0}>  — 지정하려면 -XlsPath 또는 `$env:SPARROW_TEST_XLS" -f $xlsOrigin)
    $global:SparrowTestSkip = ("실 xls 없음 ({0})" -f $xlsOrigin)
    return
}
# 파일명/경로 대신 '출처 라벨 + 크기'만 남긴다.
Write-Host ("  xls: <{0}>  ({1:N0} KB)" -f $xlsOrigin, ((Get-Item -LiteralPath $XlsPath).Length / 1KB))

$proj = Join-Path $RepositoryRoot "tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj"
if (-not (Test-Path -LiteralPath $proj)) { throw "missing project: $proj" }
$exe = Join-Path $RepositoryRoot "tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe"
Write-Host "  building SparrowXlsExport (Release)..."
$p = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try { & $dotnet.Source build $proj -c Release -v q 2>&1 | Out-Null } finally { $ErrorActionPreference = $p }
if (-not (Test-Path -LiteralPath $exe)) { throw "build produced no exe: $exe" }

$W = Join-Path $env:TEMP ('scopeloop-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$mirror = Join-Path $W 'mirror'
New-Item -ItemType Directory -Force $mirror | Out-Null

function Invoke-Exe([string[]]$a) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & $exe @a 2>&1 | Out-String } finally { $ErrorActionPreference = $prev }
    return $o
}

# 익스포터는 index.csv를 만들지 않는다(체커별 폴더 + 항목 md만). 검출 1건 = md 1개이므로
# 항목 md 필드표의 '경로' 행을 읽어 옛 index.csv '경로' 컬럼과 동일한 행 집합을 만든다.
# 필드표는 첫 '## ' 섹션 앞에서 끝나므로 거기서 파싱을 멈춰 소스 코드 본문을 필드로 오인하지 않는다.
function Get-MdFields([string]$File) {
    $path = ''; $checker = ''
    foreach ($line in [System.IO.File]::ReadAllLines($File)) {
        if ($line.StartsWith('## ')) { break }
        if ($line -match '^\|\s*경로\s*\|\s*(.*?)\s*\|\s*$') { $path = $matches[1] }
        elseif ($line -match '^\|\s*체커 키\s*\|\s*(.*?)\s*\|\s*$') { $checker = $matches[1] }
    }
    return [pscustomobject]@{ Path = $path; Checker = $checker }
}
function Get-ExportRows([string]$OutDir) {
    if (-not (Test-Path -LiteralPath $OutDir)) { return @() }
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($md in @(Get-ChildItem -LiteralPath $OutDir -Recurse -Filter *.md -File)) {
        $f = Get-MdFields $md.FullName
        $list.Add([pscustomobject]@{ Folder = $md.Directory.Name; Checker = $f.Checker; '경로' = $f.Path }) | Out-Null
    }
    return @($list.ToArray())
}

Invoke-Exe @($XlsPath, '--out', "$W\all") | Out-Null
$idx = Get-ExportRows "$W\all"
$totalAll = $idx.Count

function Get-Tail([string]$p) { if ($p -match 'release\\[^\\]+\\(.+)$') { return $matches[1] } else { return $null } }
$rows = @()
foreach ($r in $idx) {
    $tail = Get-Tail $r.'경로'
    if (-not $tail) { continue }
    $rows += [pscustomobject]@{ Tail = $tail; MirrorFull = (Join-Path $mirror $tail) }
}
if ($rows.Count -eq 0) {
    Write-Host "  xls has no usable '경로' tails; skipping."
    Remove-Item -Recurse -Force $W -ErrorAction SilentlyContinue
    $global:SparrowTestSkip = "xls 에 사용 가능한 '경로' 꼬리가 없음"
    return
}
$rows | Select-Object -ExpandProperty MirrorFull -Unique | ForEach-Object {
    New-Item -ItemType Directory -Force (Split-Path $_ -Parent) | Out-Null
    Set-Content $_ "public class Mirror {}" -Encoding UTF8
}
$mirrorCount = ($rows.MirrorFull | Sort-Object -Unique).Count
$byTail = $rows | Group-Object Tail

function New-Manifest([string[]]$mirrorFiles) {
    $mp = Join-Path $W ('m-' + [guid]::NewGuid().ToString('N').Substring(0,6) + '.csv')
    $sb = New-Object System.Text.StringBuilder; [void]$sb.AppendLine('파일명')
    foreach ($f in ($mirrorFiles | Sort-Object -Unique)) { [void]$sb.AppendLine('"' + $f + '"') }
    [System.IO.File]::WriteAllText($mp, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    return $mp
}
function Files-UnderFolder([string]$pre) { ($rows | Where-Object { $_.Tail -eq $pre -or $_.Tail.StartsWith($pre + '\') } | Select-Object -ExpandProperty MirrorFull -Unique) }
function Expected-UnderFolder([string]$pre) { ($rows | Where-Object { $_.Tail -eq $pre -or $_.Tail.StartsWith($pre + '\') }).Count }

function Run-Scope([string]$manifest) {
    $out = Join-Path $W ('o-' + [guid]::NewGuid().ToString('N').Substring(0,6))
    $so = Invoke-Exe @($XlsPath, '--out', $out, '--files-from', $manifest, '--root', $mirror)
    $mism = ($so -match '범위 불일치')
    $rows = @(Get-ExportRows $out)
    $n = $rows.Count
    $paths = @()
    if ($n -gt 0) { $paths = @($rows | Select-Object -ExpandProperty '경로' | Sort-Object -Unique) }
    # 부산물 금지 계약: 체커별 폴더 아래 md 외에는 아무것도 만들지 않는다(index.csv/checkers.md/요약 md 포함).
    $stray = @()
    if (Test-Path -LiteralPath $out) {
        $stray = @(Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object { $_.Extension -ne '.md' })
        $stray += @(Get-ChildItem -LiteralPath $out -File)
    }
    return [pscustomobject]@{ N = $n; Mismatch = $mism; Paths = $paths; Stray = $stray.Count }
}

$fails = 0
function Check($name, $cond) { if ($cond) { Write-Host "  [ok]   $name" } else { Write-Host "  [FAIL] $name"; $script:fails++ } }

# [보안] 실 폴더/파일 경로 대신 실행 로컬 익명 라벨을 붙인다(위 헤더 주석 참조). 같은 경로는 항상 같은
# 라벨을 받으므로 "폴더 #1 결과에 타폴더 0건" 처럼 단정끼리 상호 참조가 가능하고, 실제 경로는 로컬
# 대응표 파일에서만 확인한다.
$script:anonLabel = [ordered]@{}
$script:anonSeq = @{}
function Get-AnonLabel([string]$Kind, [string]$Value, [string]$Extra) {
    $key = $Kind + '|' + $Value
    if (-not $script:anonLabel.Contains($key)) {
        if (-not $script:anonSeq.ContainsKey($Kind)) { $script:anonSeq[$Kind] = 0 }
        $script:anonSeq[$Kind]++
        $suffix = if ($Extra) { "($Extra)" } else { "" }
        $script:anonLabel[$key] = ("{0} #{1}{2}" -f $Kind, $script:anonSeq[$Kind], $suffix)
    }
    return $script:anonLabel[$key]
}
function FolderLabel([string]$Folder) { return (Get-AnonLabel '폴더' $Folder ("깊이 " + $Folder.Split('\').Count)) }
function FileLabel([string]$Tail) { return (Get-AnonLabel '파일' $Tail '') }

Write-Host ("  scope loop (real xls, mirror at different root): 총 {0} / 미러 {1}" -f $totalAll, $mirrorCount)

# 폴더 케이스는 xls 경로에서 '동적으로' 발견한다. 하드코딩 폴더명은 xls 가 바뀌거나 경로가 익명화되면
# 매칭 0 이 되어 '0건 vs 0건' 거짓 통과를 만든다(실제로 그렇게 죽어 있었다) → 재발 방지.
function Get-FolderCounts {
    $h = @{}
    foreach ($r in $rows) {
        $p = Split-Path $r.Tail -Parent
        while ($p) {
            if (-not $h.ContainsKey($p)) { $h[$p] = 0 }
            $h[$p]++
            $p = Split-Path $p -Parent
        }
    }
    return @($h.GetEnumerator() |
        Sort-Object -Property @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key } } |
        ForEach-Object { [pscustomobject]@{ Folder = $_.Key; Count = $_.Value } })
}
$folderStats = Get-FolderCounts
Check ("폴더 케이스: xls 에서 폴더 {0}개 동적 발견" -f $folderStats.Count) ($folderStats.Count -gt 0)

# 하위 폴더(깊이 2 이상) 중 건수 상위 4개 — 전부 건수 > 0 이 보장된다.
$deep = @($folderStats | Where-Object { $_.Folder.Split('\').Count -ge 2 })
if ($deep.Count -eq 0) { $deep = $folderStats }
$folderCases = @($deep | Select-Object -First 4)

# 실제 폴더 선택 → 정확 건수 + 결과가 전부 범위 내
foreach ($fc in $folderCases) {
    $folder = $fc.Folder
    $exp = Expected-UnderFolder $folder
    $r = Run-Scope (New-Manifest (Files-UnderFolder $folder))
    $fl = FolderLabel $folder
    Check ("{0} 선택 → 기대 {1} / 실 {2} (0건 아님)" -f $fl, $exp, $r.N) (($r.N -eq $exp) -and ($exp -gt 0))
    $outside = @($r.Paths | Where-Object { -not ((Get-Tail $_).StartsWith($folder, [StringComparison]::OrdinalIgnoreCase)) })
    Check ("{0} 결과 전부 범위내 (범위밖 {1})" -f $fl, $outside.Count) ($outside.Count -eq 0)
}

# 경계: 한 폴더명이 다른 폴더명의 접두인 형제 쌍(예: X\View vs X\ViewModel)을 동적으로 찾아
# 디렉토리 경계 오매칭을 검증한다.
$allFolders = @($folderStats.Folder)
$bnd = $null
foreach ($a in $allFolders) {
    foreach ($b in $allFolders) {
        if ($b.Length -gt $a.Length -and
            $b.StartsWith($a, [StringComparison]::OrdinalIgnoreCase) -and
            $b[$a.Length] -ne '\') {
            $bnd = [pscustomobject]@{ Short = $a; Long = $b }
            break
        }
    }
    if ($bnd) { break }
}
if ($bnd) {
    $expS = Expected-UnderFolder $bnd.Short
    $rS = Run-Scope (New-Manifest (Files-UnderFolder $bnd.Short))
    $lblShort = FolderLabel $bnd.Short
    $lblLong = FolderLabel $bnd.Long
    Check ("경계: {0} → 기대 {1} / 실 {2}" -f $lblShort, $expS, $rS.N) (($rS.N -eq $expS) -and ($expS -gt 0))
    $leakL = @($rS.Paths | Where-Object {
        $t = Get-Tail $_
        $t -eq $bnd.Long -or $t.StartsWith(($bnd.Long + '\'), [StringComparison]::OrdinalIgnoreCase)
    })
    Check ("경계: {0} 가 {1}(접두 형제) 오매칭 안함 (누출 {2})" -f $lblShort, $lblLong, $leakL.Count) ($leakL.Count -eq 0)
}
else {
    Write-Host "  [skip] 경계: 접두 형제 폴더 쌍이 이 xls 에 없음(조용한 스킵 아님 — 명시 로그)"
}

# 단일 파일
$topFile = $byTail | Sort-Object Count -Descending | Select-Object -First 1
$rf = Run-Scope (New-Manifest @((Join-Path $mirror $topFile.Name)))
$topFileLabel = FileLabel $topFile.Name
Check ("단일 {0} 선택 → 기대 {1} / 실 {2}" -f $topFileLabel, $topFile.Count, $rf.N) ($rf.N -eq $topFile.Count)
Check ("단일 파일: 결과 경로 1개 (실 {0})" -f (@($rf.Paths).Count)) ((@($rf.Paths)).Count -eq 1)

# 서로 겹치지 않는(접두관계 아닌) 두 폴더 합집합 — 동적 선정
$u1 = $folderCases[0].Folder
$u2 = @($folderStats | Where-Object {
    $f = $_.Folder
    (-not $f.StartsWith($u1, [StringComparison]::OrdinalIgnoreCase)) -and
    (-not $u1.StartsWith($f, [StringComparison]::OrdinalIgnoreCase))
} | Select-Object -First 1).Folder
if ($u2) {
    $expSum = (Expected-UnderFolder $u1) + (Expected-UnderFolder $u2)
    $rSum = Run-Scope (New-Manifest (@(Files-UnderFolder $u1) + @(Files-UnderFolder $u2)))
    Check ("두 폴더 합집합 {0}+{1} → 기대 {2} / 실 {3}" -f (FolderLabel $u1), (FolderLabel $u2), $expSum, $rSum.N) (($rSum.N -eq $expSum) -and ($expSum -gt 0))
}
else {
    Write-Host "  [skip] 합집합: 서로 겹치지 않는 두 폴더가 이 xls 에 없음(명시 로그)"
}

# 전체 선택 → 전건
$rAll = Run-Scope (New-Manifest ($rows.MirrorFull | Sort-Object -Unique))
Check ("전체 파일 선택 → 기대 {0} / 실 {1}" -f $totalAll, $rAll.N) ($rAll.N -eq $totalAll)
Check ("전체 선택: md 외 부산물 0 (실 {0})" -f $rAll.Stray) ($rAll.Stray -eq 0)

# 체커별 폴더 그룹화: 폴더명 = md 안의 '체커 키', 폴더 개수 = 고유 체커 수, 폴더별 md 수 = 그 체커 검출 건수.
# (기대값은 폴더명이 아니라 md 내용의 '체커 키'에서 뽑아 순환 검증을 피한다.)
$allDirs = @(Get-ChildItem -LiteralPath "$W\all" -Directory)
$expectedCheckers = @($idx | Group-Object Checker)
Check ("체커별 폴더 {0}개 = 고유 체커 {1}개" -f $allDirs.Count, $expectedCheckers.Count) ($allDirs.Count -eq $expectedCheckers.Count)
Check ("폴더명 = 항목 md의 체커 키 (불일치 {0})" -f @($idx | Where-Object { $_.Folder -ne $_.Checker }).Count) (
    @($idx | Where-Object { $_.Folder -ne $_.Checker }).Count -eq 0
)
$badFolder = @($expectedCheckers | Where-Object {
    @(Get-ChildItem -LiteralPath (Join-Path "$W\all" $_.Name) -Filter *.md -File -ErrorAction SilentlyContinue).Count -ne $_.Count
})
Check ("폴더별 md 수 = 체커별 검출 건수 (불일치 {0})" -f $badFolder.Count) ($badFolder.Count -eq 0)
Check ("전건 산출에 index.csv/checkers.md/items 없음") (
    (-not (Test-Path -LiteralPath "$W\all\index.csv")) -and
    (-not (Test-Path -LiteralPath "$W\all\checkers.md")) -and
    (-not (Test-Path -LiteralPath "$W\all\items")) -and
    (@(Get-ChildItem -LiteralPath "$W\all" -File).Count -eq 0)
)

# 틀린 선택 → 0 + [범위 불일치]
$fake = Join-Path $mirror 'Nonexistent\Ghost.cs'; New-Item -ItemType Directory -Force (Split-Path $fake -Parent) | Out-Null; Set-Content $fake "x" -Encoding UTF8
$rWrong = Run-Scope (New-Manifest @($fake))
Check "틀린 선택 → 0건" ($rWrong.N -eq 0)
Check "틀린 선택 → [범위 불일치] 예외 전시" ($rWrong.Mismatch)

# 혼합(실+가짜) → 실 파일만, 예외 없음
$rMix = Run-Scope (New-Manifest @((Join-Path $mirror $topFile.Name), $fake))
Check ("혼합(실+가짜) → 실 파일 {0}건만 (실 {1})" -f $topFile.Count, $rMix.N) ($rMix.N -eq $topFile.Count)
Check "혼합: 일부 매칭이므로 [범위 불일치] 없음" (-not $rMix.Mismatch)

# 멱등 — 0건끼리 비교해 거짓 통과하지 않도록 '0건 아님'을 함께 단정한다.
$idemFolder = $folderCases[0].Folder
$man = New-Manifest (Files-UnderFolder $idemFolder)
$a = Run-Scope $man; $b = Run-Scope $man
Check ("멱등: {0} 2회 동일 ({1}={2}, 0건 아님)" -f (FolderLabel $idemFolder), $a.N, $b.N) (($a.N -eq $b.N) -and ($a.N -gt 0))

# 비선택 폴더 완전 배제
$exFolder = $folderCases[0].Folder
$rEx = Run-Scope (New-Manifest (Files-UnderFolder $exFolder))
$leak = @($rEx.Paths | Where-Object { -not (Get-Tail $_).StartsWith($exFolder, [StringComparison]::OrdinalIgnoreCase) })
Check ("비선택 폴더 완전 배제: {0} 결과에 타폴더 {1}건 (선택 {2}건)" -f (FolderLabel $exFolder), $leak.Count, $rEx.N) (
    ($leak.Count -eq 0) -and ($rEx.N -gt 0)
)

Remove-Item -Recurse -Force $W -ErrorAction SilentlyContinue

# 라벨 ↔ 실제 경로 대응표는 로컬에만 남긴다. tests\_logs\ 는 .gitignore 대상이라 커밋될 수 없고,
# 첫 줄이 공유 전 확인을 요구한다. stdout 에는 레포 상대 경로만 알린다(계정명 노출 없음).
if ($script:anonLabel.Count -gt 0) {
    $logDir = Join-Path $RepositoryRoot 'tests\_logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $rel = 'tests\_logs\scopeloop-labels-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.txt'
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('!!! 주의: 이 파일에는 당신의(회사의) 실제 프로젝트 폴더/파일 경로가 들어 있습니다.')
    $lines.Add('!!! 공개 레포 이슈/PR/채팅에 그대로 붙여넣지 마세요. 공유 전에 반드시 내용을 직접 확인하세요.')
    $lines.Add('!!! (이 폴더 tests\_logs\ 는 .gitignore 대상이라 커밋되지 않습니다.)')
    $lines.Add(('!!! 생성: {0} · 출처 xls: <{1}>' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $xlsOrigin))
    $lines.Add('')
    $lines.Add('라벨 -> 실제 경로 (이번 실행 한정)')
    foreach ($k in $script:anonLabel.Keys) {
        $lines.Add(('  {0,-18} = {1}' -f $script:anonLabel[$k], $k.Substring($k.IndexOf('|') + 1)))
    }
    [System.IO.File]::WriteAllText((Join-Path $RepositoryRoot $rel), ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    # 회전: 사내 경로가 든 파일이 무한히 쌓이지 않도록 최신 10개만 남긴다(이름 = 시각이라 이름 정렬 = 시간 정렬).
    foreach ($old in @(Get-ChildItem -LiteralPath $logDir -Filter 'scopeloop-labels-*.txt' -File -ErrorAction SilentlyContinue |
                       Sort-Object Name -Descending | Select-Object -Skip 10)) {
        Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue
    }
    Write-Host ("  라벨 대응표(실 경로 포함, 공유 금지): <repo>\{0}" -f $rel)
}

if ($fails -ne 0) { Write-Host ("Sparrow real-xls scope-loop tests FAILED ({0})." -f $fails); exit 1 }
Write-Host "Sparrow real-xls scope-loop tests passed."
# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
exit 0

#requires -Version 5.1
<#
    Compare-Sparrow.ps1 — [XLS 분리] G2 게이트 툴 (count-based, full-path-aware, line-insensitive).

    수정 전/후 Sparrow .xls 두 개를 SparrowXlsExport.exe로 파싱해 산출 md(체커별 폴더)를 비교한다.
    (익스포터는 index.csv를 만들지 않는다 — 항목 md의 필드표에서 체커 키/파일명/경로를 읽는다.)

    핵심 정체성(identity) = (체커 키, 전체경로).
      - 전체경로 = 항목 md 필드표의 '경로'(dir+file) 행. '경로'가 비어 있으면 파일명(basename)으로 폴백하며,
        폴백 시 동명파일 충돌 위험을 WARN 으로 알린다.
      - 라인 번호는 정체성에서 제외한다 → 수정으로 라인이 밀려도 "신규"로 오탐하지 않는다.

    비교 방식 = (체커, 전체경로)별 건수(count) 맵.
      - "해소" = 건수 감소, "신규" = 건수 증가(쌍 단위). 순수 라인 이동은 건수가 그대로라 신규 0.

    게이트:
      - 기본(체커 미지정): (체커,전체경로) 어디에도 건수 증가가 없으면 PASS(회귀 없음).
      - -Checker 지정: 그 체커 after-count == 0(검출 소멸) AND 전체 건수 증가 0(회귀 없음) → PASS.
      - FAIL 시 증가한 (체커,전체경로) 쌍을 before→after 로 나열(상위 50, 초과분은 생략 표기).

    스캔 위생(scan hygiene):
      - before/after 의 전체경로 집합을 비교한다. 한쪽에만 있는 경로가 있으면 델타를 신뢰할 수 없으므로
        WARNING 블록을 반드시 눈에 띄게 출력한다(다른 체크아웃/스캔 스코프 의심).
      - 기본은 경고만(PASS/ FAIL 판정을 바꾸지 않음). -StrictScope 지정 시 스코프 불일치를 FAIL 로 승격.
      - 전제: before/after 는 반드시 동일 체크아웃 + 동일 스캔 스코프여야 델타가 의미를 가진다.

    사용:
      .\Compare-Sparrow.ps1 -Before before.xls -After after.xls [-Checker FORWARD_NULL] `
                            [-Exe C:\...\SparrowXlsExport.exe] [-Work C:\temp\g2] [-StrictScope]

    종료코드: PASS → 0, FAIL → 1.
#>
param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After,
    [string]$Checker,
    [string]$Exe,
    [string]$Work,
    [switch]$StrictScope
)

$ErrorActionPreference = 'Stop'

# (체커,전체경로) 합성키 구분자: 파일경로/체커키에 절대 나타나지 않는 제어문자(Unit Separator).
$KeySep = [char]0x1F

function Get-SkillDir {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    return (Split-Path -Parent $scriptDir)     # 레포 루트(tools\ 의 부모)
}

# 파서 exe 확보: (1) -Exe, (2) publish\, (3) bin\Release\net8.0\, (4) dotnet+csproj 로 빌드. 모두 실패 시 시도경로 나열.
function Resolve-ParserExe {
    param([string]$ExeParam, [string]$SkillDir)
    $tried = New-Object System.Collections.Generic.List[string]

    if ($ExeParam) {
        [void]$tried.Add($ExeParam)
        if (Test-Path -LiteralPath $ExeParam) { return $ExeParam }
    }
    $publish = Join-Path $SkillDir 'tools\_internal\SparrowXlsExport\publish\SparrowXlsExport.exe'
    [void]$tried.Add($publish)
    if (Test-Path -LiteralPath $publish) { return $publish }

    $binRel = Join-Path $SkillDir 'tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe'
    [void]$tried.Add($binRel)
    if (Test-Path -LiteralPath $binRel) { return $binRel }

    # 마지막 수단: dotnet + csproj 가 있으면 빌드(run-e2e.ps1 과 동일 방식).
    $csproj = Join-Path $SkillDir 'tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet -and (Test-Path -LiteralPath $csproj)) {
        Write-Host ("  파서 exe가 없어 빌드합니다: {0}" -f $csproj)
        & $dotnet.Source build $csproj -c Release --nologo | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $binRel)) { return $binRel }
    }

    throw (
        "SparrowXlsExport.exe 를 찾지 못했습니다. 시도한 경로:`n  - " + ($tried -join "`n  - ") +
        "`n  (dotnet+csproj 자동 빌드도 불가/실패). -Exe 로 exe 경로를 직접 지정하세요."
    )
}

# 항목 md 1개 → 필드표에서 '체커 키'/'파일명'/'경로'를 읽어 index.csv 행과 동일한 형태의 객체로 만든다.
# 필드표는 md 최상단(제목 다음)에 있고 '## '로 시작하는 첫 섹션 전에 끝나므로, 그 지점에서 파싱을 멈춰
# '## 소스 코드' 본문의 우연한 파이프 라인을 필드로 오인하지 않는다. 값이 빈 컬럼은 표에 없으므로 ''로 남는다.
function Read-ItemMd {
    param([string]$Path, [string]$CheckerFromDir)
    $checker = ''; $file = ''; $pathCol = ''
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line.StartsWith('## ')) { break }
        if ($line -match '^\|\s*([^|]+?)\s*\|\s*(.*?)\s*\|\s*$') {
            $name = $matches[1]; $value = $matches[2]
            if ($name -eq '체커 키') { $checker = $value }
            elseif ($name -eq '파일명') { $file = $value }
            elseif ($name -eq '경로') { $pathCol = $value }
        }
    }
    # 방어: xls에 '체커 키' 컬럼이 없어 표에 안 실린 경우 폴더명(=체커 키)으로 폴백.
    if ($checker.Length -eq 0) { $checker = $CheckerFromDir }
    return [pscustomobject]@{ '체커 키' = $checker; '파일명' = $file; '경로' = $pathCol }
}

# 익스포터 출력 폴더(<out>\<체커 키>\*.md) 전체 → 검출 1건 = 객체 1개.
function Read-ExportRows {
    param([string]$OutDir)
    $rows = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $OutDir)) { return @() }
    foreach ($dir in @(Get-ChildItem -LiteralPath $OutDir -Directory | Sort-Object Name)) {
        foreach ($md in @(Get-ChildItem -LiteralPath $dir.FullName -Filter *.md -File | Sort-Object Name)) {
            [void]$rows.Add((Read-ItemMd -Path $md.FullName -CheckerFromDir $dir.Name))
        }
    }
    return @($rows.ToArray())
}

# 전체경로 산출. '경로'(dir+file)를 우선 사용하고, 디렉터리만 있으면 파일명을 결합한다.
# '경로'가 비면 파일명(basename)으로 폴백(동명파일 충돌 위험 → Fallback=$true 로 표시).
function Get-FullPath {
    param([string]$PathCol, [string]$FileName)
    $p = ([string]$PathCol -replace '\\', '/').Trim()
    $f = ([string]$FileName -replace '\\', '/').Trim()
    if ($p.Length -eq 0) {
        return [pscustomobject]@{ Path = $f; Fallback = $true }
    }
    # 실제 Sparrow '경로'는 dir+file 전체경로 → 파일명으로 끝나면 그대로 사용.
    if ($f.Length -gt 0 -and ($p -eq $f -or $p.EndsWith('/' + $f))) {
        return [pscustomobject]@{ Path = $p; Fallback = $false }
    }
    # '경로'가 디렉터리처럼 보이면 파일명을 결합.
    $joined = if ($p.EndsWith('/')) { $p + $f } else { $p + '/' + $f }
    return [pscustomobject]@{ Path = $joined; Fallback = $false }
}

# 검출 행(항목 md 1건) → (체커,전체경로) 건수 맵/경로 집합/체커별 총계/메타/폴백수.
function Build-Maps {
    param($Rows)
    $counts = @{}
    $meta = @{}   # key → @{ Checker; Path }
    $paths = New-Object System.Collections.Generic.HashSet[string]
    $byChecker = @{}
    $fallback = 0
    foreach ($r in $Rows) {
        $checker = ([string]$r.'체커 키').Trim()
        $file = [string]$r.'파일명'
        $pathCol = [string]$r.'경로'
        $fp = Get-FullPath -PathCol $pathCol -FileName $file
        if ($fp.Fallback) { $fallback++ }
        $full = $fp.Path
        $key = $checker + $KeySep + $full
        if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1; $meta[$key] = [pscustomobject]@{ Checker = $checker; Path = $full } }
        [void]$paths.Add($full)
        if ($byChecker.ContainsKey($checker)) { $byChecker[$checker]++ } else { $byChecker[$checker] = 1 }
    }
    return [pscustomobject]@{ Counts = $counts; Meta = $meta; Paths = $paths; ByChecker = $byChecker; Fallback = $fallback }
}

# native exe 실행: stderr를 throwing 경로에 넣지 않고 $LASTEXITCODE로 판정.
function Invoke-Parser {
    param([string]$ExePath, [string]$Xls, [string]$OutDir)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $ExePath $Xls --out $OutDir | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -ne 0) { throw "파서 실패(exit $code): $Xls" }
    if (-not (Test-Path -LiteralPath $OutDir)) { throw "익스포터 출력 폴더 생성 안 됨: $OutDir" }
    return $OutDir
}

if (-not (Test-Path -LiteralPath $Before)) { throw "before xls 없음: $Before" }
if (-not (Test-Path -LiteralPath $After)) { throw "after xls 없음: $After" }

$skillDir = Get-SkillDir
$Exe = Resolve-ParserExe -ExeParam $Exe -SkillDir $skillDir

if (-not $Work) { $Work = Join-Path ([System.IO.Path]::GetTempPath()) ('sparrow-g2-' + [System.IO.Path]::GetRandomFileName()) }
$ownWork = -not (Test-Path -LiteralPath $Work)   # 우리가 만든 임시폴더만 정리한다.

try {
    $beforeOut = Join-Path $Work 'before'
    $afterOut = Join-Path $Work 'after'
    [void](New-Item -ItemType Directory -Force -Path $beforeOut)
    [void](New-Item -ItemType Directory -Force -Path $afterOut)

    $beforeDir = Invoke-Parser -ExePath $Exe -Xls (Resolve-Path -LiteralPath $Before).Path -OutDir $beforeOut
    $afterDir = Invoke-Parser -ExePath $Exe -Xls (Resolve-Path -LiteralPath $After).Path -OutDir $afterOut

    $beforeRows = @(Read-ExportRows -OutDir $beforeDir)   # @() 강제: 1건일 때 스칼라 접힘 방지
    $afterRows = @(Read-ExportRows -OutDir $afterDir)

    $beforeMaps = Build-Maps $beforeRows
    $afterMaps = Build-Maps $afterRows

    # --- 증가(신규)/감소(해소) 계산 ---
    $allKeys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($k in $beforeMaps.Counts.Keys) { [void]$allKeys.Add($k) }
    foreach ($k in $afterMaps.Counts.Keys) { [void]$allKeys.Add($k) }

    $increases = New-Object System.Collections.Generic.List[object]   # {Checker; Path; Before; After}
    $perCheckerNew = @{}
    foreach ($k in $allKeys) {
        $b = if ($beforeMaps.Counts.ContainsKey($k)) { $beforeMaps.Counts[$k] } else { 0 }
        $a = if ($afterMaps.Counts.ContainsKey($k)) { $afterMaps.Counts[$k] } else { 0 }
        if ($a -gt $b) {
            $m = if ($afterMaps.Meta.ContainsKey($k)) { $afterMaps.Meta[$k] } else { $beforeMaps.Meta[$k] }
            [void]$increases.Add([pscustomobject]@{ Checker = $m.Checker; Path = $m.Path; Before = $b; After = $a })
            $inc = $a - $b
            if ($perCheckerNew.ContainsKey($m.Checker)) { $perCheckerNew[$m.Checker] += $inc } else { $perCheckerNew[$m.Checker] = $inc }
        }
    }

    # --- 스캔 위생: 전체경로 집합 비교 ---
    $onlyBefore = @($beforeMaps.Paths | Where-Object { -not $afterMaps.Paths.Contains($_) } | Sort-Object)
    $onlyAfter = @($afterMaps.Paths | Where-Object { -not $beforeMaps.Paths.Contains($_) } | Sort-Object)
    $scopeMismatch = ($onlyBefore.Count -gt 0 -or $onlyAfter.Count -gt 0)

    # --- 체커별 요약 테이블 ---
    $checkers = New-Object System.Collections.Generic.SortedSet[string]
    foreach ($c in $beforeMaps.ByChecker.Keys) { [void]$checkers.Add($c) }
    foreach ($c in $afterMaps.ByChecker.Keys) { [void]$checkers.Add($c) }

    Write-Host ""
    Write-Host "=== Sparrow 비교 (G2, count-based / full-path) ==="
    Write-Host ("before : {0}" -f (Resolve-Path -LiteralPath $Before).Path)
    Write-Host ("after  : {0}" -f (Resolve-Path -LiteralPath $After).Path)
    Write-Host ("파서   : {0}" -f $Exe)
    Write-Host ""
    Write-Host ("{0,-45} {1,8} {2,8} {3,8} {4,8}" -f '체커 키', 'before', 'after', 'delta', '신규')
    Write-Host ("-" * 80)
    foreach ($c in $checkers) {
        $b = if ($beforeMaps.ByChecker.ContainsKey($c)) { $beforeMaps.ByChecker[$c] } else { 0 }
        $a = if ($afterMaps.ByChecker.ContainsKey($c)) { $afterMaps.ByChecker[$c] } else { 0 }
        $n = if ($perCheckerNew.ContainsKey($c)) { $perCheckerNew[$c] } else { 0 }
        Write-Host ("{0,-45} {1,8} {2,8} {3,8} {4,8}" -f $c, $b, $a, ($a - $b), $n)
    }
    $totalNew = 0
    foreach ($v in $perCheckerNew.Values) { $totalNew += $v }
    Write-Host ("-" * 80)
    Write-Host ("{0,-45} {1,8} {2,8} {3,8} {4,8}" -f '(합계)', $beforeRows.Count, $afterRows.Count, ($afterRows.Count - $beforeRows.Count), $totalNew)

    # --- 폴백(경로 없음) 경고 ---
    if ($beforeMaps.Fallback -gt 0 -or $afterMaps.Fallback -gt 0) {
        Write-Host ""
        Write-Host "  [WARN] '경로'가 빈 검출이 있어 파일명(basename)으로 폴백했습니다 (동명파일 충돌 위험):"
        Write-Host ("         before={0}건, after={1}건. 원본 xls에 '경로' 컬럼이 채워져 있는지 확인하세요." -f $beforeMaps.Fallback, $afterMaps.Fallback)
    }

    # --- 스캔 위생 WARNING(항상 눈에 띄게) ---
    if ($scopeMismatch) {
        Write-Host ""
        Write-Host "########################################################################"
        Write-Host "## [스캔 위생 경고] before/after 전체경로 집합이 다릅니다."
        Write-Host "##   다른 체크아웃/스캔 스코프 의심 — 델타를 신뢰할 수 없습니다."
        Write-Host ("##   before 에만 있는 경로: {0}건,  after 에만 있는 경로: {1}건" -f $onlyBefore.Count, $onlyAfter.Count)
        if ($onlyBefore.Count -gt 0) {
            Write-Host "##   before 에만 있음(예시, 최대 10):"
            $shown = 0
            foreach ($p in $onlyBefore) { Write-Host ("##     - {0}" -f $p); $shown++; if ($shown -ge 10) { break } }
            if ($onlyBefore.Count -gt 10) { Write-Host ("##     ... 외 {0}건" -f ($onlyBefore.Count - 10)) }
        }
        if ($onlyAfter.Count -gt 0) {
            Write-Host "##   after 에만 있음(예시, 최대 10):"
            $shown = 0
            foreach ($p in $onlyAfter) { Write-Host ("##     - {0}" -f $p); $shown++; if ($shown -ge 10) { break } }
            if ($onlyAfter.Count -gt 10) { Write-Host ("##     ... 외 {0}건" -f ($onlyAfter.Count - 10)) }
        }
        Write-Host ("##   전제: before/after 는 동일 체크아웃 + 동일 스캔 스코프여야 합니다." )
        if ($StrictScope) { Write-Host "##   -StrictScope: 이 스코프 불일치를 FAIL 로 처리합니다." }
        else { Write-Host "##   (경고만 — 판정 미변경. 엄격 검사는 -StrictScope 사용)" }
        Write-Host "########################################################################"
    }

    # --- 게이트 판정 ---
    $anyIncrease = ($increases.Count -gt 0)
    Write-Host ""
    if ($Checker) {
        $afterChk = if ($afterMaps.ByChecker.ContainsKey($Checker)) { $afterMaps.ByChecker[$Checker] } else { 0 }
        Write-Host ("게이트 대상 체커: {0}" -f $Checker)
        Write-Host ("  after-count(검출 소멸 목표=0) : {0}" -f $afterChk)
        Write-Host ("  전체 건수 증가(회귀, 목표=0)  : {0}" -f $totalNew)
        $pass = ($afterChk -eq 0) -and (-not $anyIncrease)
    }
    else {
        Write-Host ("전체 건수 증가(회귀, 목표=0) : {0}" -f $totalNew)
        $pass = (-not $anyIncrease)
    }
    if ($StrictScope -and $scopeMismatch) {
        Write-Host "  -StrictScope: 스코프 불일치로 FAIL 승격"
        $pass = $false
    }

    Write-Host ""
    if ($pass) {
        Write-Host "결과: PASS"
        exit 0
    }
    else {
        Write-Host "결과: FAIL"
        if ($anyIncrease) {
            Write-Host ("  증가한 (체커,전체경로) 쌍 (상위 50, before→after):")
            $sorted = @($increases | Sort-Object -Property @{ Expression = { $_.After - $_.Before }; Descending = $true }, Checker, Path)
            $shown = 0
            foreach ($it in $sorted) {
                Write-Host ("    - {0} | {1} : {2} -> {3}" -f $it.Checker, $it.Path, $it.Before, $it.After)
                $shown++; if ($shown -ge 50) { break }
            }
            if ($sorted.Count -gt 50) { Write-Host ("    ... 외 {0}쌍 생략" -f ($sorted.Count - 50)) }
        }
        if ($StrictScope -and $scopeMismatch) {
            Write-Host "  (스코프 불일치가 -StrictScope 로 FAIL 처리됨 — 위 스캔 위생 경고 참조)"
        }
        exit 1
    }
}
finally {
    if ($ownWork -and (Test-Path -LiteralPath $Work)) {
        Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

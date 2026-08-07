#requires -Version 5.1
<#
    Opt-in end-to-end test for the [XLS 분리] checker->rule MAPPING layer (--guides). NOT run by the default
    validate gate (needs the .NET SDK + NPOI restore). Run manually or via `validate.ps1 -IncludeSparrowMappingTests`.

    It builds FixtureGen + the tool, generates the synthetic BIFF fixture (K1=MISSING_BLANK_LINE_BEFORE_COMMENT
    with 3 items, K2=PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICITLY_TYPE with 1 item), then exercises the
    CLI --guides contract E2E under the ASSIGNMENT model (named rule library + _assignments.json; NO name-based
    auto-mapping):

      * PURE (no --guides)      : md carry NO "## 매핑 규칙" section (opt-in purity, regression guard).
      * NO-ASSIGN (--guides,    : a rule file named exactly like the checker key exists but there is no
        no _assignments.json)     assignment => still all pure. Proves the file's mere existence never auto-maps.
      * ATTACHED (--guides +    : _assignments.json maps K1 -> rule "빈줄규칙" (name != key); K1's EVERY item md
        _assignments.json)        gets a self-contained "## 매핑 규칙 (K1)" section between 체커 설명 and 소스 코드;
                                  K2 (unassigned) stays pure.
      * SUMMARY                 : stdout reports "mapped checkers: 1" / "unmapped checkers: 1" + the unmapped key.
      * IDEMPOTENT              : re-running --guides into the same out is byte-identical (no duplicate section).

    Always runs on a freshly generated synthetic xls (no self-skip on a missing real fixture). Skips cleanly
    (not fails) only when the .NET SDK is absent, consistent with the sibling E2E scripts.

    PS 5.1 notes honored: collections wrapped in @() before .Count; no &&/ternary/null-coalescing; md read as
    raw bytes for byte-identity; the TOOL writes UTF-8 without BOM via .NET.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow mapping E2E."; $global:SparrowTestSkip = "dotnet SDK 없음"; return }

$toolDir = Join-Path $RepositoryRoot "tools\_internal\SparrowXlsExport"
$toolProj = Join-Path $toolDir "SparrowXlsExport.csproj"
$fixtureProj = Join-Path $toolDir "FixtureGen\FixtureGen.csproj"
foreach ($p in @($toolProj, $fixtureProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

$work = Join-Path $env:TEMP ("sparrow-mapping-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { $script:failures += $name } }
    catch { $script:failures += "$name ($($_.Exception.Message))" }
}

# Run the tool capturing exit code + stdout (needed for the summary assertions).
function Invoke-ToolCapture {
    param([string[]]$ToolArgs)
    $o = & $dotnet.Source run --project $toolProj -c Release --no-build -- @ToolArgs 2>&1 | Out-String
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $o }
}

function Get-Md {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $Dir -Recurse -Filter *.md -File)
}

# Relative-path -> byte content snapshot, for byte-identity comparison across idempotent re-runs.
function Get-MdSnapshot {
    param([string]$Dir)
    $map = @{}
    foreach ($f in (Get-Md $Dir)) {
        $rel = $f.FullName.Substring($Dir.Length).TrimStart('\')
        $map[$rel] = [System.IO.File]::ReadAllBytes($f.FullName)
    }
    return $map
}

function Snapshots-Equal {
    param($A, $B)
    if ($A.Count -ne $B.Count) { return $false }
    foreach ($k in $A.Keys) {
        if (-not $B.ContainsKey($k)) { return $false }
        $ba = $A[$k]; $bb = $B[$k]
        if ($ba.Length -ne $bb.Length) { return $false }
        for ($i = 0; $i -lt $ba.Length; $i++) { if ($ba[$i] -ne $bb[$i]) { return $false } }
    }
    return $true
}

$k1 = "MISSING_BLANK_LINE_BEFORE_COMMENT"
$k2 = "PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICITLY_TYPE"
$mapHeader = "## 매핑 규칙 ($k1)"

try {
    Write-Host "  building FixtureGen + SparrowXlsExport (Release)..."
    & $dotnet.Source build $fixtureProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FixtureGen build failed" }
    & $dotnet.Source build $toolProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowXlsExport build failed" }

    $fixtureXls = Join-Path $work "fixture.xls"
    & $dotnet.Source run --project $fixtureProj -c Release --no-build -- $fixtureXls 2>&1 | Out-Null
    Check "fixture.xls generated" { (Test-Path -LiteralPath $fixtureXls) -and ((Get-Item -LiteralPath $fixtureXls).Length -gt 0) }

    # Rule LIBRARY + explicit assignment. The library rule is named "빈줄규칙" (deliberately NOT equal to the
    # checker key, to prove rule name != checker key). It is written with a UTF-8 BOM (the tool must strip the
    # leading U+FEFF) and an internal "## 근거" header (the tool must not mistake it for the section boundary).
    # _assignments.json maps ONLY K1 -> 빈줄규칙; K2 is left unassigned.
    $ruleName = "빈줄규칙"
    $guidesDir = Join-Path $work "guides"
    New-Item -ItemType Directory -Force -Path $guidesDir | Out-Null
    $ruleBody = "규칙: 주석 앞에는 빈 줄을 둔다.`r`n`r`n## 근거`r`nSparrow 권장 사항.`r`n"
    [System.IO.File]::WriteAllText((Join-Path $guidesDir "$ruleName.md"), $ruleBody, (New-Object System.Text.UTF8Encoding($true)))
    $assignJson = "{`r`n  ""$k1"": ""$ruleName""`r`n}`r`n"
    [System.IO.File]::WriteAllText((Join-Path $guidesDir "_assignments.json"), $assignJson, (New-Object System.Text.UTF8Encoding($false)))

    # NO-ASSIGN guides: a rule file NAMED exactly like the checker key ($k1.md) but with NO _assignments.json,
    # to prove there is no name-based auto-mapping (the file's mere existence must NOT attach anything).
    $guidesNoAssign = Join-Path $work "guides_noassign"
    New-Item -ItemType Directory -Force -Path $guidesNoAssign | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $guidesNoAssign "$k1.md"), $ruleBody, (New-Object System.Text.UTF8Encoding($false)))

    # --- PURE: no --guides => md carry no mapping section (opt-in purity / regression guard) ---
    $pureOut = Join-Path $work "pure"
    $rp = Invoke-ToolCapture @($fixtureXls, "--out", $pureOut)
    Check "pure run exit 0" { $rp.Exit -eq 0 }
    Check "pure run: 4 md" { (Get-Md $pureOut).Count -eq 4 }
    Check "pure run: no '## 매핑 규칙' anywhere" {
        @(Get-Md $pureOut | Where-Object { (Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName).Contains("## 매핑 규칙") }).Count -eq 0
    }
    Check "pure run: no mapping summary on stdout" { -not $rp.Out.Contains("mapped checkers:") }

    # --- NO-ASSIGN: --guides with a key-named rule file but no _assignments.json => still all pure (no auto-map) ---
    $naOut = Join-Path $work "noassign"
    $rna = Invoke-ToolCapture @($fixtureXls, "--out", $naOut, "--guides", $guidesNoAssign)
    Check "no-assign run exit 0" { $rna.Exit -eq 0 }
    Check "no-assign: 체커키 동명 규칙 파일이 있어도 지정 없으면 부착 0 (자동매핑 없음)" {
        @(Get-Md $naOut | Where-Object { (Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName).Contains("## 매핑 규칙") }).Count -eq 0
    }
    Check "no-assign: summary mapped checkers: 0" { $rna.Out -match "mapped checkers:\s*0" }

    # --- ATTACHED: --guides + _assignments.json => K1 all-md self-contained embed, K2 pure, summary correct ---
    $mapOut = Join-Path $work "mapped"
    $rm = Invoke-ToolCapture @($fixtureXls, "--out", $mapOut, "--guides", $guidesDir)
    Check "guides run exit 0" { $rm.Exit -eq 0 }

    $k1Dir = Join-Path $mapOut $k1
    $k2Dir = Join-Path $mapOut $k2
    $k1Mds = @(Get-ChildItem -LiteralPath $k1Dir -Filter *.md -File)
    Check "K1 폴더에 항목 md 3건" { $k1Mds.Count -eq 3 }

    # self-contained: EVERY K1 md carries the rule header + body + preserved internal '## 근거'
    $embedCount = 0
    $bodyCount = 0
    $posOk = 0
    $noBom = 0
    $singleSection = 0
    foreach ($f in $k1Mds) {
        $t = Get-Content -Raw -Encoding UTF8 -LiteralPath $f.FullName
        if ($t.Contains($mapHeader)) { $embedCount++ }
        if ($t.Contains("주석 앞에는 빈 줄을 둔다") -and $t.Contains("## 근거")) { $bodyCount++ }
        $di = $t.IndexOf("## 체커 설명"); $mi = $t.IndexOf("## 매핑 규칙"); $si = $t.IndexOf("## 소스 코드")
        if (($di -ge 0) -and ($mi -gt $di) -and ($si -gt $mi)) { $posOk++ }
        if (-not $t.Contains([char]0xFEFF)) { $noBom++ }
        if (([regex]::Matches($t, [regex]::Escape("## 매핑 규칙")).Count) -eq 1) { $singleSection++ }
    }
    Check "K1 모든 md 에 매핑 규칙 헤더 (self-contained)" { $embedCount -eq 3 }
    Check "K1 모든 md 에 규칙 본문 + 내부 '## 근거' 보존" { $bodyCount -eq 3 }
    Check "K1 임베드 위치 = 체커 설명 → 매핑 규칙 → 소스 코드" { $posOk -eq 3 }
    Check "K1 임베드된 규칙에 BOM 없음" { $noBom -eq 3 }
    Check "K1 매핑 규칙 섹션 단 1개(중복 없음)" { $singleSection -eq 3 }

    # K2 unmapped => pure
    $k2Mds = @(Get-ChildItem -LiteralPath $k2Dir -Filter *.md -File)
    Check "K2 폴더에 항목 md 1건" { $k2Mds.Count -eq 1 }
    Check "K2 (미매핑) md 는 순수(매핑 규칙 없음)" {
        -not (Get-Content -Raw -Encoding UTF8 -LiteralPath $k2Mds[0].FullName).Contains("## 매핑 규칙")
    }

    # stdout summary
    Check "summary: mapped checkers: 1" { $rm.Out -match "mapped checkers:\s*1" }
    Check "summary: unmapped checkers: 1" { $rm.Out -match "unmapped checkers:\s*1" }
    Check "summary: items touched: 3" { $rm.Out -match "items touched:\s*3" }
    Check "summary: unmapped keys lists K2" { $rm.Out.Contains($k2) }

    # --- IDEMPOTENT: re-run --guides into the same out => byte-identical tree (no duplicate section) ---
    $before = Get-MdSnapshot $mapOut
    $ri = Invoke-ToolCapture @($fixtureXls, "--out", $mapOut, "--guides", $guidesDir)
    Check "guides re-run exit 0" { $ri.Exit -eq 0 }
    $after = Get-MdSnapshot $mapOut
    Check "재실행 byte-identical (멱등, 중복 삽입 없음)" { Snapshots-Equal $before $after }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("Sparrow mapping E2E failed:`n  " + ($failures -join "`n  ")) }
Write-Host "Sparrow mapping E2E passed."
# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
exit 0

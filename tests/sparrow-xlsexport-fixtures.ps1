#requires -Version 5.1
<#
    Opt-in end-to-end smoke test for SparrowXlsExport. NOT run by the default validate gate (needs the
    .NET SDK + an NPOI restore -- env/time heavy). Run it manually, or via `validate.ps1 -IncludeSparrowE2E`.

    It builds the FixtureGen generator + the tool, generates a tiny real BIFF (.xls) fixture, runs the
    tool, and asserts the split output layout (체커별 폴더 + 항목 md, 그 외 산출물 없음), the item md
    content, the filters (--severity / --checker / --max), and idempotent re-runs. Skips cleanly (not
    fails) when the .NET SDK is missing.

    산출물 계약: <out>\<체커 키>\{ID}_{파일명}_{라인}.md 만 생성된다 —
    items\ 하위폴더도, index.csv 도, checkers.md 도, 요약/지침 md 도 만들지 않는다.

    PS 5.1 notes honored here: collections wrapped in @() before .Count; no &&/ternary/null-coalescing;
    md read with -Encoding UTF8 (the TOOL writes UTF-8 without BOM via .NET, not via PowerShell).
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow XLS export E2E."; return }

$toolDir = Join-Path $RepositoryRoot "tools\_internal\SparrowXlsExport"
$toolProj = Join-Path $toolDir "SparrowXlsExport.csproj"
$fixtureProj = Join-Path $toolDir "FixtureGen\FixtureGen.csproj"
foreach ($p in @($toolProj, $fixtureProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

$work = Join-Path $env:TEMP ("sparrow-e2e-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { $script:failures += $name } }
    catch { $script:failures += "$name ($($_.Exception.Message))" }
}

function Invoke-Tool {
    param([string[]]$ToolArgs)
    & $dotnet.Source run --project $toolProj -c Release --no-build -- @ToolArgs 2>&1 | Out-Null
    return $LASTEXITCODE
}

function Get-MdCount {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return 0 }
    return @(Get-ChildItem -LiteralPath $Dir -Recurse -Filter *.md -File).Count
}

function Get-CheckerDirs {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $Dir -Directory | Select-Object -ExpandProperty Name | Sort-Object)
}

# 출력 폴더 전체에서 md 가 아닌 파일(= 부산물)을 찾는다. 계약상 0개여야 한다.
function Get-NonMdFiles {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $Dir -Recurse -File | Where-Object { $_.Extension -ne ".md" })
}

try {
    Write-Host "  building FixtureGen + SparrowXlsExport (Release)..."
    & $dotnet.Source build $fixtureProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FixtureGen build failed" }
    & $dotnet.Source build $toolProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowXlsExport build failed" }

    $fixtureXls = Join-Path $work "fixture.xls"
    & $dotnet.Source run --project $fixtureProj -c Release --no-build -- $fixtureXls 2>&1 | Out-Null
    Check "fixture.xls generated" { (Test-Path -LiteralPath $fixtureXls) -and ((Get-Item -LiteralPath $fixtureXls).Length -gt 0) }

    # --- default run ---
    $out = Join-Path $work "out"
    $exit = Invoke-Tool @($fixtureXls, "--out", $out)
    Check "exit 0 (default run)" { $exit -eq 0 }
    Check "4 md files written" { (Get-MdCount $out) -eq 4 }

    # 산출 레이아웃: 체커키 디렉토리가 최상위. K1=3건, K2=1건.
    $k1 = "MISSING_BLANK_LINE_BEFORE_COMMENT"
    $k2 = "PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICITLY_TYPE"
    $k1Dir = Join-Path $out $k1
    $k2Dir = Join-Path $out $k2
    $dirs = Get-CheckerDirs $out
    Check "체커키 폴더 2개 (K1/K2)만 존재" { ($dirs.Count -eq 2) -and ($dirs -contains $k1) -and ($dirs -contains $k2) }
    Check "K1 폴더에 3건" { (Get-MdCount $k1Dir) -eq 3 }
    Check "K2 폴더에 1건" { (Get-MdCount $k2Dir) -eq 1 }

    # 부산물 금지: items\ 없음, index.csv 없음, checkers.md 없음, 루트 md(요약/지침) 없음.
    Check "items\ 하위폴더 없음" { -not (Test-Path -LiteralPath (Join-Path $out "items")) }
    Check "index.csv 없음" { -not (Test-Path -LiteralPath (Join-Path $out "index.csv")) }
    Check "checkers.md 없음" { -not (Test-Path -LiteralPath (Join-Path $out "checkers.md")) }
    Check "출력 루트에 파일 없음 (요약/지침 md 포함 부산물 0)" { @(Get-ChildItem -LiteralPath $out -File).Count -eq 0 }
    Check "md 외 파일 없음 (부산물 0)" { (Get-NonMdFiles $out).Count -eq 0 }

    # 파일명 규칙: {ID}_{파일명}_{라인}.md (체커키는 폴더명이므로 파일명에서 제외 -- 잘림 없음).
    $k1Names = @(Get-ChildItem -LiteralPath $k1Dir -Filter *.md -File | Select-Object -ExpandProperty Name | Sort-Object)
    $k2Names = @(Get-ChildItem -LiteralPath $k2Dir -Filter *.md -File | Select-Object -ExpandProperty Name | Sort-Object)
    Check "K1 파일명 = {ID}_{파일명}_{라인}.md" {
        ($k1Names -join "|") -eq "101_main.cpp_42.md|102_util.cpp_7.md|104_legacy.c_99.md"
    }
    Check "K2 파일명 = {ID}_{파일명}_{라인}.md" { ($k2Names -join "|") -eq "103_Service.cs_15.md" }
    Check "파일명에 체커 키가 들어가지 않음" {
        (@($k1Names + $k2Names) | Where-Object { $_.Contains($k1) -or $_.Contains("PRACTICE.") }).Count -eq 0
    }

    # row (c) md (ID 103): 콤마가 든 체커명이 필드표에 그대로 실린다(옛 index.csv 인용 검사 대체).
    $rowC = @(Get-ChildItem -LiteralPath $k2Dir -Filter "103_*.md" -File)
    Check "row (c) md exists" { $rowC.Count -eq 1 }
    if ($rowC.Count -eq 1) {
        $c = Get-Content -Raw -Encoding UTF8 -LiteralPath $rowC[0].FullName
        Check "row (c) 체커명(콤마 포함) 유지" { $c.Contains("| 체커명 | 사용되지 않는 객체, 암시적 타입 |") }
        Check "row (c) 위험도 높음" { $c.Contains("| 위험도 | 높음 |") }
    }

    # row (b) md (ID 102): pipe escaped + <br> in table; source fenced + verbatim multi-line.
    $rowB = @(Get-ChildItem -LiteralPath $k1Dir -Filter "102_*.md" -File)
    Check "row (b) md exists" { $rowB.Count -eq 1 }
    if ($rowB.Count -eq 1) {
        $b = Get-Content -Raw -Encoding UTF8 -LiteralPath $rowB[0].FullName
        Check "row (b) table escapes | and collapses newline to <br>" { $b.Contains('a\|b<br>c,d"e') }
        Check "row (b) source is fenced (text code block)" { $b -match '(?m)^```text' }
        Check "row (b) source verbatim line 1" { $b.Contains("   6: void f() {") }
        Check "row (b) source verbatim line 2" { $b.Contains("   7:   int x=0; // x") }
        Check "row (b) source verbatim line 3" { $b.Contains("   8: }") }
        Check "row (b) 소스 코드/체커 설명 excluded from table" { (-not ($b -match "(?m)^\| 소스 코드 ")) -and (-not ($b -match "(?m)^\| 체커 설명 ")) }
        # 상수/무기여 컬럼(유형·언어·체커 타입·이슈 상태)은 필드표에서 제외한다 -- 수정 판단에 기여하지 않음.
        Check "row (b) 유형/언어/체커 타입/이슈 상태 excluded from table" {
            (-not ($b -match "(?m)^\| 유형 ")) -and (-not ($b -match "(?m)^\| 언어 ")) -and
            (-not ($b -match "(?m)^\| 체커 타입 ")) -and (-not ($b -match "(?m)^\| 이슈 상태 "))
        }
        # 판단에 쓰이는 컬럼은 그대로 남는다.
        Check "row (b) keeps 위험도/체커 키/라인/파일명 rows" {
            ($b -match "(?m)^\| 위험도 ") -and ($b -match "(?m)^\| 체커 키 ") -and
            ($b -match "(?m)^\| 라인 ") -and ($b -match "(?m)^\| 파일명 ")
        }
        # md 는 xls 컬럼 렌더링만 담는다: 필드표 + 체커 설명 + 소스 코드. 그 외 섹션은 없다.
        Check "row (b) keeps 체커 설명 section" { $b.Contains("## 체커 설명") }
        Check "row (b) 섹션은 체커 설명/소스 코드 뿐" {
            ([regex]::Matches($b, "(?m)^## ") | Measure-Object).Count -eq 2 -and $b.Contains("## 소스 코드")
        }
        # 도구가 지시문·앵커 마커를 주입하지 않는다(수정 범위 판단은 사용자·체커별 몫).
        Check "row (b) 주입 문구 없음 (수정 대상/지시/앵커 마커)" {
            (-not $b.Contains("## 수정 대상")) -and (-not $b.Contains("TARGET LINE")) -and
            (-not $b.Contains("ANCHOR")) -and (-not $b.Contains("수정 기준점")) -and (-not $b.Contains("- 지시:"))
        }
    }

    # row (a) md (ID 101): ID renders without a trailing .0 in the table.
    $rowA = @(Get-ChildItem -LiteralPath $k1Dir -Filter "101_*.md" -File)
    Check "row (a) md exists" { $rowA.Count -eq 1 }
    if ($rowA.Count -eq 1) {
        $a = Get-Content -Raw -Encoding UTF8 -LiteralPath $rowA[0].FullName
        Check "row (a) ID renders as 101 (not 101.0)" { $a.Contains("| ID | 101 |") -and (-not $a.Contains("101.0")) }
    }

    # --- filters ---
    $sevOut = Join-Path $work "sev"
    $null = Invoke-Tool @($fixtureXls, "--out", $sevOut, "--severity", "높음")
    Check "--severity 높음 writes exactly 1 md" { (Get-MdCount $sevOut) -eq 1 }
    Check "--severity 높음 leaves only the K2 folder" { ((Get-CheckerDirs $sevOut) -join "|") -eq $k2 }

    $chkOut = Join-Path $work "chk"
    $null = Invoke-Tool @($fixtureXls, "--out", $chkOut, "--checker", "missing_blank")   # case-insensitive substring
    Check "--checker (case-insensitive substring) writes 3 md" { (Get-MdCount $chkOut) -eq 3 }
    Check "--checker leaves only the K1 folder" { ((Get-CheckerDirs $chkOut) -join "|") -eq $k1 }

    $maxOut = Join-Path $work "mx"
    $null = Invoke-Tool @($fixtureXls, "--out", $maxOut, "--max", "2")
    Check "--max 2 writes 2 md" { (Get-MdCount $maxOut) -eq 2 }

    # --- idempotency: re-run default into the same dir -> identical md file set (폴더 포함 상대경로) ---
    $before = @(Get-ChildItem -LiteralPath $out -Recurse -Filter *.md -File | ForEach-Object { $_.Directory.Name + "\" + $_.Name } | Sort-Object)
    $null = Invoke-Tool @($fixtureXls, "--out", $out)
    $after = @(Get-ChildItem -LiteralPath $out -Recurse -Filter *.md -File | ForEach-Object { $_.Directory.Name + "\" + $_.Name } | Sort-Object)
    Check "re-run is idempotent (same md file set)" { ($before.Count -eq $after.Count) -and (($before -join "|") -eq ($after -join "|")) }
    Check "re-run leaves no byproducts" { ((Get-NonMdFiles $out).Count -eq 0) -and (@(Get-ChildItem -LiteralPath $out -File).Count -eq 0) }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("Sparrow XLS export E2E failed:`n  " + ($failures -join "`n  ")) }
Write-Host "Sparrow XLS export E2E passed."

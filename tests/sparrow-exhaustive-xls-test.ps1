#requires -Version 5.1
<#
    EXHAUSTIVE Sparrow [코드 규칙]·[주석·레이아웃] xls coverage measurement.

    Extracts the REAL flagged code of EVERY [코드 규칙]·[주석·레이아웃]-relevant finding in the MyApp Sparrow result .xls (none
    skipped), generates a parseable .cs snippet for each, runs the matching tool+rule (SparrowSyntaxFix /
    SparrowCommentFix) over every snippet, and reports per-finding whether the tool transformed it. Purpose:
    prove no real detected pattern slips through -- surface any real flagged code the tools fail to handle.

    This is a PARSE+TRANSFORM coverage measurement (NOT a Sparrow re-analysis, NOT a compile check -- those are
    covered by the other Sparrow suites). The tools' rewriters use CSharpSyntaxTree.ParseText (syntax only), so
    a snippet only needs to PARSE, not compile; the generator validates that with the same Roslyn version.

    The .xls is NOT in the repo (it lives in Downloads). This test SELF-SKIPS (does not fail) when the .xls is
    absent, so it is safe to wire into validate.ps1 behind an opt-in switch. Never commits the .xls.

    xls 경로 결정 순서(명시 opt-in 우선):
      1) -XlsPath <path>
      2) $env:SPARROW_TEST_XLS
      3) %USERPROFILE%\Downloads\issues_*.xls 중 최신 (자동 탐색)
         → -NoAutoDiscover 또는 $env:SPARROW_TEST_XLS_NO_AUTODISCOVER=1 로 끌 수 있다.

    [보안] stdout 은 공유물이다. 이 출력은 tests\_logs\validate-*.log 로 들어가고 CONTRIBUTING 은 실패 신고 시
    그 로그 첨부를 지시한다 → 이 레포는 곧 public 이므로, 남의 회사 소스가 남의 이슈에 실리면 안 된다.
    그래서 stdout 에는 다음이 절대 나가지 않는다:
      - 해석된 xls 의 경로/파일명(사내 리포트 파일명에는 사람 ID 가 들어 있다)
      - xls 가 가리키는 실제 소스 파일 경로/파일명
      - 검출된 소스 코드 본문
      - 사용자 계정명이 박힌 절대 경로(TEMP/레포 경로는 %TEMP%\... / <repo>\... 로 접어서 찍는다)
    stdout 에는 출처 라벨과 '건수'만 남긴다. 진단 가치는 죽이지 않는다 — NOT-TRANSFORMED 잔여의 실제
    파일:라인 + 코드 원문은 '로컬 상세 파일'(tests\_logs\exhaustive-notransformed-<stamp>.txt, .gitignore 대상)
    에 그대로 기록하고, stdout 에는 그 파일의 상대 경로와 건수만 알린다. 상세 파일 첫 줄에 "당신의 소스가
    들어 있으니 공유 전 확인하라"는 경고를 박는다.

    Run directly:   tests\sparrow-exhaustive-xls-test.ps1 [-XlsPath <path>] [-SampleCount 15] [-KeepWork] [-NoAutoDiscover]
    Via validate:   validate.ps1 -IncludeSparrowExhaustiveXls
#>
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # 명시 지정용. 비우면 $env:SPARROW_TEST_XLS → Downloads 자동 탐색 순으로 해석한다.
    # (파일명을 고정하면 회차마다 달라 늘 self-skip 으로 죽으므로 자동 탐색은 살려 둔다.)
    [string]$XlsPath,
    # 로컬 상세 파일에 체커별로 기록할 NOT-TRANSFORMED 잔여 샘플 수(0 = 무제한). stdout 에는 어차피 건수만 나간다.
    [int]$SampleCount = 15,
    [switch]$KeepWork,
    [switch]$NoAutoDiscover
)

$ErrorActionPreference = "Stop"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "dotnet SDK not found; skipping exhaustive Sparrow xls test."
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
    Write-Host "Sparrow xls not found; skipping exhaustive Sparrow xls test (the .xls is not in the repo)."
    Write-Host ("  xls 후보: <{0}>  — 지정하려면 -XlsPath 또는 `$env:SPARROW_TEST_XLS" -f $xlsOrigin)
    $global:SparrowTestSkip = ("실 xls 없음 ({0})" -f $xlsOrigin)
    return
}
# 파일명/경로 대신 '출처 라벨 + 크기'만 남긴다.
Write-Host ("  xls: <{0}>  ({1:N0} KB)" -f $xlsOrigin, ((Get-Item -LiteralPath $XlsPath).Length / 1KB))

$toolsDir   = Join-Path $RepositoryRoot "tools\_internal"
$syntaxProj = Join-Path $toolsDir "SparrowSyntaxFix\SparrowSyntaxFix.csproj"
$commentProj= Join-Path $toolsDir "SparrowCommentFix\SparrowCommentFix.csproj"
$genProj    = Join-Path $PSScriptRoot "SparrowExhaustiveXls\SparrowExhaustiveXls.csproj"
foreach ($p in @($syntaxProj, $commentProj, $genProj)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" }
}
$syntaxDll  = Join-Path $toolsDir "SparrowSyntaxFix\bin\Release\net8.0\SparrowSyntaxFix.dll"
$commentDll = Join-Path $toolsDir "SparrowCommentFix\bin\Release\net8.0\SparrowCommentFix.dll"
$genDll     = Join-Path $PSScriptRoot "SparrowExhaustiveXls\bin\Release\net8.0\SparrowExhaustiveXls.dll"

$work = Join-Path $env:TEMP ("sparrow-exhaustive-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$genOut = Join-Path $work "out"
New-Item -ItemType Directory -Force -Path $genOut | Out-Null

# stdout 에 나가는 문자열에서 사용자 계정명이 박힌 절대 경로 접두를 접는다(%TEMP%\... / <repo>\...).
# 접두 검사가 아니라 '치환'이다 — 생성기 요약처럼 "gen root:   C:\Users\<계정>\..." 로 경로가 문장 중간에
# 박혀 오는 줄도 접어야 하기 때문. 사람은 여전히 붙여넣어 찾아갈 수 있고, 공유 로그에는 계정명이 없다.
function Hide-MachinePath([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return $p }
    $out = $p
    foreach ($pair in @(@{ Prefix = $RepositoryRoot; Token = '<repo>' }, @{ Prefix = $env:TEMP; Token = '%TEMP%' })) {
        $prefix = $pair.Prefix
        if ($prefix) {
            $out = [regex]::Replace($out, [regex]::Escape($prefix), $pair.Token,
                                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    return $out
}

function Invoke-Quiet([string]$dll, [string[]]$a) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { & $dotnet.Source $dll @a 2>&1 | Out-Null } finally { $ErrorActionPreference = $prev }
    return $LASTEXITCODE
}
function Test-BytesEqual([byte[]]$A, [byte[]]$B) {
    if ($null -eq $A -or $null -eq $B) { return $false }
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}

# Copy a checker's generated snippets into a fresh working dir, run a tool+rule over them, and return the set
# of file names (f#####.cs) whose bytes CHANGED. Fresh copy each call => independent measurement per rule.
function Invoke-CheckerDiff([string]$slug, [string]$tool, [string]$rules) {
    $src = Join-Path (Join-Path $genOut "gen") $slug
    $dst = Join-Path $work ("run-" + $slug + "-" + $rules.Replace(",", "_").Replace("-", "") )
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    $files = Get-ChildItem -LiteralPath $src -Filter *.cs -File
    $before = @{}
    foreach ($f in $files) {
        $target = Join-Path $dst $f.Name
        Copy-Item -LiteralPath $f.FullName -Destination $target
        $before[$f.Name] = [System.IO.File]::ReadAllBytes($target)
    }

    if ($tool -eq "syntax") {
        # SparrowSyntaxFix expands a directory target for *.cs.
        [void](Invoke-Quiet $syntaxDll @($dst, "--rules", $rules))
    } else {
        # SparrowCommentFix does NOT expand directories; pass explicit .cs paths, batched to stay under the
        # command-line length limit. Each file is independent, so batching does not affect the result.
        $paths = @($files | ForEach-Object { Join-Path $dst $_.Name })
        $batch = 120
        for ($i = 0; $i -lt $paths.Count; $i += $batch) {
            $slice = $paths[$i..([math]::Min($i + $batch - 1, $paths.Count - 1))]
            [void](Invoke-Quiet $commentDll (@() + $slice + @("--rules", $rules)))
        }
    }

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($name in $before.Keys) {
        $after = [System.IO.File]::ReadAllBytes((Join-Path $dst $name))
        if (-not (Test-BytesEqual $before[$name] $after)) { $changed.Add($name) }
    }
    # Return a plain array; every caller wraps the result in @(...) so an empty result is an empty array,
    # never $null (which the HashSet-unroll would otherwise produce).
    return $changed.ToArray()
}

$script:completedOk = $false
try {
    Write-Host "  building tools + generator (Release)..."
    if ((Invoke-Quiet "build" @($syntaxProj, "-c", "Release", "-v", "q")) -ne 0) { throw "SparrowSyntaxFix build failed" }
    if ((Invoke-Quiet "build" @($commentProj, "-c", "Release", "-v", "q")) -ne 0) { throw "SparrowCommentFix build failed" }
    if ((Invoke-Quiet "build" @($genProj, "-c", "Release", "-v", "q")) -ne 0) { throw "generator build failed" }

    Write-Host "  generating snippets from xls (this reads all [코드 규칙]·[주석·레이아웃] findings)..."
    # 생성기 stdout 도 공유 로그로 흘러가므로 계정명이 박힌 절대 경로(gen root)를 접어서 중계한다.
    $genLines = & $dotnet.Source $genDll "--xls" $XlsPath "--out" $genOut
    $genExitCode = $LASTEXITCODE
    $genLines | ForEach-Object { Write-Host (Hide-MachinePath ([string]$_)) }
    if ($genExitCode -ne 0) { throw "generator exited $genExitCode" }

    $manifestPath = Join-Path $genOut "manifest.csv"
    $rows = Import-Csv -LiteralPath $manifestPath -Encoding UTF8
    $bySlug = $rows | Group-Object slug

    # Preserve the canonical checker order from the manifest (first appearance).
    $slugOrder = @()
    foreach ($r in $rows) { if ($slugOrder -notcontains $r.slug) { $slugOrder += $r.slug } }

    $enc = [System.Text.Encoding]::UTF8
    $report = New-Object System.Collections.Generic.List[string]
    # Emit  = 공유물(stdout + coverage-report.txt). 식별 가능한 것은 절대 넣지 않는다.
    # Detail= 로컬 상세 파일 전용. 실 파일 경로/라인/코드 원문은 오직 여기로만 간다.
    $detail = New-Object System.Collections.Generic.List[string]
    function Emit([string]$s) { $report.Add($s); Write-Host $s }
    function Detail([string]$s) { $detail.Add($s) }

    Emit ""
    Emit "================ EXHAUSTIVE SPARROW AUTOFIX XLS COVERAGE ================"
    # [보안] xls 의 경로/파일명은 찍지 않는다(사내 리포트 파일명에 사람 ID 가 들어 있고 이 출력은
    # 신고용 로그로 첨부된다). 출처 라벨 + 읽어들인 findings 건수만 남긴다.
    Emit ("xls:        <" + $xlsOrigin + ">  (findings " + @($rows).Count + "건)")
    Emit ("generated:  " + (Hide-MachinePath (Join-Path $genOut "gen")))
    Emit ("manifest:   " + (Hide-MachinePath $manifestPath))
    Emit ""
    Emit ("{0,-34} {1,6} {2,6} {3,8} {4,8} {5,8}" -f "checker(slug)", "total", "parseF", "transf", "notTr", "transf%")
    Emit ("-" * 82)

    $notTransSamples = @{}
    $checkerMeta = @{}

    foreach ($slug in $slugOrder) {
        $grp = ($bySlug | Where-Object { $_.Name -eq $slug }).Group
        $total = @($grp).Count
        $tool  = $grp[0].tool
        $rules = $grp[0].rules
        $checkerMeta[$slug] = @{ checker = $grp[0].checker; tool = $tool; rules = $rules }

        # Build name -> manifest-row map (f00001.cs == 1st generated row for this slug, in order).
        $idx = 0
        $rowByName = @{}
        foreach ($r in $grp) { $idx++; $rowByName[("f" + $idx.ToString("D5") + ".cs")] = $r }

        $parseFail = @($grp | Where-Object { $_.parse_ok -eq "0" }).Count

        if ($total -eq 0) {
            Emit ("{0,-34} {1,6} {2,6} {3,8} {4,8} {5,8}" -f $slug, 0, 0, 0, 0, "n/a")
            continue
        }

        $changedArr = @(Invoke-CheckerDiff $slug $tool $rules)
        $changed = New-Object System.Collections.Generic.HashSet[string]
        foreach ($c in $changedArr) { [void]$changed.Add($c) }
        $transf = $changed.Count
        $notTr  = $total - $transf
        $pct = if ($total -gt 0) { [math]::Round(100.0 * $transf / $total, 1) } else { 0 }
        Emit ("{0,-34} {1,6} {2,6} {3,8} {4,8} {5,7}%" -f $slug, $total, $parseFail, $transf, $notTr, $pct)

        # Collect not-transformed samples (real flagged text), preferring PARSE-OK ones first so residuals that
        # are genuine tool no-ops are not drowned out by extraction artifacts.
        $samplesOk = New-Object System.Collections.Generic.List[string]
        $samplesArtifact = New-Object System.Collections.Generic.List[string]
        foreach ($name in ($rowByName.Keys | Sort-Object)) {
            if ($changed.Contains($name)) { continue }
            $r = $rowByName[$name]
            $flagged = ""
            try { $flagged = $enc.GetString([System.Convert]::FromBase64String($r.flagged_b64)) } catch {}
            $flagged = ($flagged -replace "\s+", " ").Trim()
            $line = ("[{0}:{1}] {2}" -f $r.file, $r.line, $flagged)
            if ($r.parse_ok -eq "1") { $samplesOk.Add($line) } else { $samplesArtifact.Add("(parse-fail) " + $line) }
        }
        $notTransSamples[$slug] = @{ ok = $samplesOk; artifact = $samplesArtifact }
    }

    Emit ""
    Emit "================ PER-RULE ATTRIBUTION (코드 규칙 var checkers) ================"
    foreach ($slug in $slugOrder) {
        $meta = $checkerMeta[$slug]
        if ($meta.tool -ne "syntax") { continue }
        if ($meta.rules -notmatch ",") { continue }   # only the combined var-rule checkers
        Emit ("-- {0} ({1})" -f $slug, $meta.rules)
        foreach ($rule in $meta.rules.Split(",")) {
            $c = @(Invoke-CheckerDiff $slug "syntax" $rule)
            Emit ("     {0,-16} transformed {1}" -f $rule, $c.Count)
        }
    }

    # [보안] 여기가 예전에 사내 소스를 가장 많이 흘리던 자리다: 실 파일명 + 라인번호 + 코드 한 줄 원문을
    # 수백 줄 stdout 으로 뱉었다. 이제 stdout 에는 '체커별 잔여 건수'만 남기고, 실 경로/코드 원문은
    # 로컬 상세 파일로만 보낸다. 판정 로직과 건수는 그대로다(무엇을 검증하는지 불변).
    Emit ""
    Emit "================ NOT-TRANSFORMED RESIDUALS (건수만 — 상세는 로컬 파일) ================"
    Detail ("=== NOT-TRANSFORMED SAMPLES (real flagged lines the tool left unchanged) ===")
    Detail ""
    $totalOk = 0
    $totalArt = 0
    foreach ($slug in $slugOrder) {
        if (-not $notTransSamples.ContainsKey($slug)) { continue }
        $meta = $checkerMeta[$slug]
        $ok = $notTransSamples[$slug].ok
        $art = $notTransSamples[$slug].artifact
        if ($ok.Count -eq 0 -and $art.Count -eq 0) { continue }
        $totalOk += $ok.Count
        $totalArt += $art.Count

        # 공유 로그: 슬러그/체커 키(도구 자신의 어휘, 레포에 이미 있는 상수) + 건수만.
        Emit ("#### {0,-34} [{1}]  parse-ok 잔여 {2}건 · parse-fail 아티팩트 {3}건" -f $slug, $meta.checker, $ok.Count, $art.Count)

        # 로컬 상세: 예전 stdout 과 동일한 내용(-SampleCount 로 체커당 상한, 0 = 무제한).
        Detail ("#### {0}   [{1}]" -f $slug, $meta.checker)
        $cap = if ($SampleCount -le 0) { $ok.Count } else { $SampleCount }
        $shown = 0
        foreach ($s in $ok) { if ($shown -ge $cap) { break }; Detail ("   " + $s); $shown++ }
        if ($ok.Count -gt $cap) { Detail ("   ... (+{0} more parse-ok residuals)" -f ($ok.Count - $cap)) }
        if ($art.Count -gt 0) {
            $artCap = if ($SampleCount -le 0) { $art.Count } else { [math]::Min(3, $art.Count) }
            for ($i = 0; $i -lt $artCap; $i++) { Detail ("   " + $art[$i]) }
            if ($art.Count -gt $artCap) { Detail ("   ... (+{0} more parse-fail artifacts)" -f ($art.Count - $artCap)) }
        }
        Detail ""
    }
    Emit ("합계: parse-ok 잔여 {0}건 · parse-fail 아티팩트 {1}건" -f $totalOk, $totalArt)

    # 로컬 상세 파일. tests\_logs\ 는 .gitignore 대상이라 커밋될 수 없고, 첫 줄이 공유 전 확인을 요구한다.
    $logDir = Join-Path $RepositoryRoot "tests\_logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $detailFile = Join-Path $logDir ("exhaustive-notransformed-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".txt")
    $header = @(
        "!!! 주의: 이 파일에는 당신의(회사의) 실제 소스 파일 경로와 소스 코드 본문이 들어 있습니다.",
        "!!! 공개 레포 이슈/PR/채팅에 그대로 붙여넣지 마세요. 공유 전에 반드시 내용을 직접 확인하고,",
        "!!! 필요한 줄만 골라 익명화해서 옮기세요. (이 폴더 tests\_logs\ 는 .gitignore 대상이라 커밋되지 않습니다.)",
        ("!!! 생성: {0} · 출처 xls: <{1}>" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"), $xlsOrigin),
        ""
    )
    [System.IO.File]::WriteAllText($detailFile, (($header + $detail) -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    # 회전: 사내 소스가 든 파일이 무한히 쌓이지 않도록 최신 10개만 남긴다(이름 = 시각이라 이름 정렬 = 시간 정렬).
    foreach ($old in @(Get-ChildItem -LiteralPath $logDir -Filter "exhaustive-notransformed-*.txt" -File -ErrorAction SilentlyContinue |
                       Sort-Object Name -Descending | Select-Object -Skip 10)) {
        Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue
    }

    $reportFile = Join-Path $genOut "coverage-report.txt"
    [System.IO.File]::WriteAllText($reportFile, ($report -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host ("report written: " + (Hide-MachinePath $reportFile) + "  (stdout 과 동일 — 식별 정보 없음)")
    Write-Host ("NOT-TRANSFORMED 상세(실 경로·코드 원문, 공유 금지): " + (Hide-MachinePath $detailFile))
    Write-Host ("generated snippets kept at: " + (Hide-MachinePath (Join-Path $genOut "gen")))
    Write-Host "exhaustive Sparrow xls coverage test complete."
    $script:completedOk = $true
}
finally {
    if (-not $KeepWork) {
        # Keep the generated snippets + report for spot-checking; only clear the throwaway run copies.
        Get-ChildItem -LiteralPath $work -Directory -Filter "run-*" -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ("work dir: " + (Hide-MachinePath $work))
}

# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
# 여기까지 왔다는 것은 try 블록이 throw 없이 끝났다는 뜻이다(실패는 전부 throw 로 나간다).
if (-not $script:completedOk) { throw "exhaustive Sparrow xls test did not complete" }
exit 0

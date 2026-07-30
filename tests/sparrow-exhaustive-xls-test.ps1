#requires -Version 5.1
<#
    EXHAUSTIVE Sparrow Track A/B xls coverage measurement.

    Extracts the REAL flagged code of EVERY Track A/B-relevant finding in the MyApp Sparrow result .xls (none
    skipped), generates a parseable .cs snippet for each, runs the matching tool+rule (SparrowSyntaxFix /
    SparrowCommentFix) over every snippet, and reports per-finding whether the tool transformed it. Purpose:
    prove no real detected pattern slips through -- surface any real flagged code the tools fail to handle.

    This is a PARSE+TRANSFORM coverage measurement (NOT a Sparrow re-analysis, NOT a compile check -- those are
    covered by the other Sparrow suites). The tools' rewriters use CSharpSyntaxTree.ParseText (syntax only), so
    a snippet only needs to PARSE, not compile; the generator validates that with the same Roslyn version.

    The .xls is NOT in the repo (it lives in Downloads). This test SELF-SKIPS (does not fail) when the .xls is
    absent, so it is safe to wire into validate.ps1 behind an opt-in switch. Never commits the .xls.

    Run directly:   tests\sparrow-exhaustive-xls-test.ps1 [-XlsPath <path>] [-SampleCount 15] [-KeepWork]
    Via validate:   validate.ps1 -IncludeSparrowExhaustiveXls
#>
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    # 기본값: Downloads 의 Sparrow 결과 xls 중 가장 최근 것(파일명 고정 금지 — 회차마다 달라 self-skip 으로 죽는다).
    [string]$XlsPath = (
        @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Downloads') -Filter 'issues_*.xls' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    ),
    [int]$SampleCount = 15,
    [switch]$KeepWork
)

$ErrorActionPreference = "Stop"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping exhaustive Sparrow xls test."; return }
if ([string]::IsNullOrWhiteSpace($XlsPath) -or -not (Test-Path -LiteralPath $XlsPath)) {
    Write-Host "Sparrow xls (Downloads\issues_*.xls) not found; skipping exhaustive Sparrow xls test (the .xls is not in the repo)."
    return
}
Write-Host ("  xls: " + (Split-Path $XlsPath -Leaf))

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

try {
    Write-Host "  building tools + generator (Release)..."
    if ((Invoke-Quiet "build" @($syntaxProj, "-c", "Release", "-v", "q")) -ne 0) { throw "SparrowSyntaxFix build failed" }
    if ((Invoke-Quiet "build" @($commentProj, "-c", "Release", "-v", "q")) -ne 0) { throw "SparrowCommentFix build failed" }
    if ((Invoke-Quiet "build" @($genProj, "-c", "Release", "-v", "q")) -ne 0) { throw "generator build failed" }

    Write-Host "  generating snippets from xls (this reads all Track A/B findings)..."
    $genExit = & $dotnet.Source $genDll "--xls" $XlsPath "--out" $genOut
    $genExit | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "generator exited $LASTEXITCODE" }

    $manifestPath = Join-Path $genOut "manifest.csv"
    $rows = Import-Csv -LiteralPath $manifestPath -Encoding UTF8
    $bySlug = $rows | Group-Object slug

    # Preserve the canonical checker order from the manifest (first appearance).
    $slugOrder = @()
    foreach ($r in $rows) { if ($slugOrder -notcontains $r.slug) { $slugOrder += $r.slug } }

    $enc = [System.Text.Encoding]::UTF8
    $report = New-Object System.Collections.Generic.List[string]
    function Emit([string]$s) { $report.Add($s); Write-Host $s }

    Emit ""
    Emit "================ EXHAUSTIVE SPARROW TRACK A/B XLS COVERAGE ================"
    Emit ("xls:        " + $XlsPath)
    Emit ("generated:  " + (Join-Path $genOut "gen"))
    Emit ("manifest:   " + $manifestPath)
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
    Emit "================ PER-RULE ATTRIBUTION (Track A var checkers) ================"
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

    Emit ""
    Emit "================ NOT-TRANSFORMED SAMPLES (real flagged lines the tool left unchanged) ================"
    foreach ($slug in $slugOrder) {
        if (-not $notTransSamples.ContainsKey($slug)) { continue }
        $meta = $checkerMeta[$slug]
        $ok = $notTransSamples[$slug].ok
        $art = $notTransSamples[$slug].artifact
        if ($ok.Count -eq 0 -and $art.Count -eq 0) { continue }
        Emit ""
        Emit ("#### {0}   [{1}]" -f $slug, $meta.checker)
        $shown = 0
        foreach ($s in $ok) { if ($shown -ge $SampleCount) { break }; Emit ("   " + $s); $shown++ }
        if ($ok.Count -gt $SampleCount) { Emit ("   ... (+{0} more parse-ok residuals)" -f ($ok.Count - $SampleCount)) }
        if ($art.Count -gt 0) {
            $artShow = [math]::Min(3, $art.Count)
            for ($i = 0; $i -lt $artShow; $i++) { Emit ("   " + $art[$i]) }
            if ($art.Count -gt $artShow) { Emit ("   ... (+{0} more parse-fail artifacts)" -f ($art.Count - $artShow)) }
        }
    }

    $reportFile = Join-Path $genOut "coverage-report.txt"
    [System.IO.File]::WriteAllText($reportFile, ($report -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host ("report written: " + $reportFile)
    Write-Host ("generated snippets kept at: " + (Join-Path $genOut "gen"))
    Write-Host "exhaustive Sparrow xls coverage test complete."
}
finally {
    if (-not $KeepWork) {
        # Keep the generated snippets + report for spot-checking; only clear the throwaway run copies.
        Get-ChildItem -LiteralPath $work -Directory -Filter "run-*" -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ("work dir: " + $work)
}

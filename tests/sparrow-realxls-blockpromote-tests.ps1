#requires -Version 5.1
<#
    REAL-XLS blockpromote regression for the Sparrow CLI (SparrowCommentFix, OPT-IN rule `blockpromote`).

    `blockpromote` resolves FORMATTING...MISSING_BLANK_LINE_BEFORE_COMMENT findings whose comment is an INLINE
    single-line `/* ... */` block comment embedded in a code line, by LIFTING it OUT to its own `//` line ABOVE
    the enclosing statement (a blank line before it) and cleaning the residual code line. It generalizes the
    `trailing` rule (which promotes trailing `//` comments) to embedded `/* */` block comments.

    HONESTY: this checker has no official rule doc, so the exact Sparrow acceptance is UNVERIFIED. `blockpromote`
    is a best-effort, PARSE-SAFE, IDEMPOTENT transform the user validates by re-running Sparrow. It is OPT-IN
    (a valid rule key, but NOT in Run-SparrowCommentFix.ps1's default rule set), so a normal comment-fix run
    never triggers it -- it must be selected explicitly with `--rules blockpromote`.

    Fixtures embed the four INLINE `/* */` shapes taken from the real MyApp xls (issues_sample_6888.xls);
    undefined types (Color / SQLite*) are SEMANTIC-only, so ParseText still reports zero SYNTAX diagnostics:
      case1 mid-condition   SampleEntity.cs:336   if (/* Att.IsSingleInput&&*/ (cond))
      case2 catch-body      RunController.cs:181 catch (Exception) { /* 무시 (Cancellation 등) */ }
      case3 trailing field  Consts.cs:52             ...Color.FromRgb(255,0,255); /* Color.FromRgb(253, 8, 9);*/
      case4 mid-arglist     DataManager.cs:139   using (var cmd = new SQLiteCommand(sql, conn /* SQLiteConnection */))

    For every fixture this asserts BOTH halves:
      - 검출 (detection):  the rule FIRES on the real code  -> CLI reports a positive edit count.
      - 보완 (remediation): the promoted `// ...` line is present ABOVE the enclosing statement, the inline
                            `/* */` is removed, the residual code is clean and PARSES clean, and a second run is
                            BYTE-IDENTICAL (idempotent).
    Plus a NEGATIVE (an already-own-line `/* */` and `//` stay byte-identical) and an OPT-IN-ISOLATION check
    (the runner's default rule set does NOT promote an inline block comment).

    case2's assertions are ENCODING-AGNOSTIC on the Korean text: PS 5.1 mangles Korean literals in a no-BOM .ps1,
    so we assert the promoted line's STRUCTURE + the ASCII substring `(Cancellation`, not the exact Hangul.

    NOT run by the default validate gate (needs .NET SDK + Roslyn restore). Run via
    `validate.ps1 -IncludeSparrowRealXlsBlockPromoteTests`. Self-skips (not fails) when the .NET SDK is missing.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow real-xls blockpromote tests."; return }

$toolRoot    = Join-Path $RepositoryRoot "tools\_internal"
$commentProj = Join-Path $toolRoot "SparrowCommentFix\SparrowCommentFix.csproj"
$syntaxProj  = Join-Path $toolRoot "SparrowSyntaxFix\SparrowSyntaxFix.csproj"
foreach ($p in @($commentProj, $syntaxProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

Write-Host "  building SparrowCommentFix + SparrowSyntaxFix (Release)..."
$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
try {
    & $dotnet.Source build -c Release $syntaxProj  2>&1 | Out-Null
    & $dotnet.Source build -c Release $commentProj 2>&1 | Out-Null
} finally { $ErrorActionPreference = $prev }
$syntaxDll  = Join-Path $toolRoot "SparrowSyntaxFix\bin\Release\net8.0\SparrowSyntaxFix.dll"
$commentDll = Join-Path $toolRoot "SparrowCommentFix\bin\Release\net8.0\SparrowCommentFix.dll"
foreach ($d in @($syntaxDll, $commentDll)) { if (-not (Test-Path -LiteralPath $d)) { throw "build produced no dll: $d" } }

$work = Join-Path $env:TEMP ("sparrow-realxls-blockpromote-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { Write-Host "  [FAIL] $name"; $script:failures += $name } }
    catch { Write-Host "  [FAIL] $name ($($_.Exception.Message))"; $script:failures += "$name ($($_.Exception.Message))" }
}
# Run a tool, return @{ Out = <stdout text>; Text = <file content after> }
function Invoke-Tool([string]$dll, [string]$file, [string]$rules) {
    $p = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $out = (& $dotnet.Source $dll $file "--rules" $rules 2>&1 | Out-String) } finally { $ErrorActionPreference = $p }
    return @{ Out = $out; Text = [System.IO.File]::ReadAllText($file) }
}
# "detection": the CLI reported a positive edit count for the rule (matches "<rule> edits: N" or "rule <name>: N").
function Detected([string]$out, [int]$min = 1) {
    $m = [regex]::Matches($out, '(?:edits:\s*|rule\s+\w+:\s*)(\d+)')
    foreach ($x in $m) { if ([int]$x.Groups[1].Value -ge $min) { return $true } }
    return $false
}
# "parses clean": SyntaxFix --dry-run --rules all emits no parse-error/exception line.
function ParsesClean([string]$file) {
    $p = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $o = (& $dotnet.Source $syntaxDll $file "--dry-run" "--rules" "all" 2>&1 | Out-String) } finally { $ErrorActionPreference = $p }
    return -not ($o -match 'parse error|exception|malformed')
}
function Test-BytesEqual([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}
function New-Fixture([string]$name, [string]$content) {
    $f = Join-Path $work $name
    [System.IO.File]::WriteAllText($f, $content, $utf8NoBom)
    return $f
}

# ---- Each $src below is the INLINE `/* */` shape from the xls, wrapped minimally so it parses. ----

# case1 — SampleEntity.cs:336 (mid-condition inline block comment)
$case1Src = @"
public class T {
    void M(bool cond) {
        if (/* Att.IsSingleInput&&*/ (cond))
        {
        }
    }
}
"@
$f = New-Fixture "case1_ifcond.cs" $case1Src
$r = Invoke-Tool $commentDll $f "blockpromote"
Check "case1 if-cond: 검출 (rule fires on mid-condition /* */)"       { Detected $r.Out }
Check "case1 if-cond: 보완 (promoted // line above)"                  { $r.Text -match "(?m)^\s*// Att\.IsSingleInput&&\.\s*$" }
Check "case1 if-cond: 인라인 블록 제거됨"                             { -not ($r.Text -match '/\*') }
Check "case1 if-cond: 잔여 코드 정리 (if ((cond)))"                   { $r.Text -match 'if \(\(cond\)\)' }
Check "case1 if-cond: 출력 파싱 정상"                                 { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "blockpromote"
Check "case1 if-cond: 멱등 (2회차 무변화)"                            { $r2.Text -eq $r.Text }

# case2 — RunController.cs:181 (catch-body inline block comment; Korean -> encoding-agnostic assertions)
$case2Src = @"
using System;
public class T {
    void M() {
        try { M2(); }
        catch (Exception) { /* 무시 (Cancellation 등) */ }
    }
    void M2() { }
}
"@
$f = New-Fixture "case2_catch.cs" $case2Src
$r = Invoke-Tool $commentDll $f "blockpromote"
Check "case2 catch: 검출 (rule fires on catch-body /* */)"           { Detected $r.Out }
# encoding-agnostic: a promoted // line exists, carries the ASCII '(Cancellation', and ends with the added period.
Check "case2 catch: 보완 (promoted // line above, ASCII part kept)"   { $r.Text -match "(?m)^\s*//.*\(Cancellation.*\.\s*$" }
Check "case2 catch: 인라인 블록 제거됨"                               { -not ($r.Text -match '/\*') }
Check "case2 catch: 잔여 코드 정리 (catch (Exception) { })"           { $r.Text -match 'catch \(Exception\) \{ \}' }
Check "case2 catch: 출력 파싱 정상"                                   { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "blockpromote"
Check "case2 catch: 멱등 (2회차 무변화)"                              { $r2.Text -eq $r.Text }

# case3 — Consts.cs:52 (trailing inline block comment on a field). Color is undefined -> semantic-only, parses.
$case3Src = @"
public class T {
        public static Color RouteColor = Color.FromRgb(255, 0, 255); /* Color.FromRgb(253, 8, 9);*/
}
"@
$f = New-Fixture "case3_field.cs" $case3Src
$r = Invoke-Tool $commentDll $f "blockpromote"
Check "case3 field: 검출 (rule fires on trailing /* */)"             { Detected $r.Out }
Check "case3 field: 보완 (promoted // line above)"                   { $r.Text -match [regex]::Escape('// Color.FromRgb(253, 8, 9);.') }
Check "case3 field: 인라인 블록 제거됨"                               { -not ($r.Text -match '/\*') }
Check "case3 field: 잔여 필드 유지 (선언 그대로, 후행주석만 제거)"    { $r.Text -match 'public static Color RouteColor = Color\.FromRgb\(255, 0, 255\);' }
Check "case3 field: 출력 파싱 정상"                                   { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "blockpromote"
Check "case3 field: 멱등 (2회차 무변화)"                              { $r2.Text -eq $r.Text }

# case4 — DataManager.cs:139 (mid-arglist inline block comment). SQLite* undefined -> semantic-only, parses.
$case4Src = @"
public class T {
    void M(SQLiteConnection conn, string sql) {
        using (var cmd = new SQLiteCommand(sql, conn /* SQLiteConnection */))
        {
        }
    }
}
"@
$f = New-Fixture "case4_arglist.cs" $case4Src
$r = Invoke-Tool $commentDll $f "blockpromote"
Check "case4 arglist: 검출 (rule fires on mid-arglist /* */)"        { Detected $r.Out }
Check "case4 arglist: 보완 (promoted // line above)"                 { $r.Text -match "(?m)^\s*// SQLiteConnection\.\s*$" }
Check "case4 arglist: 인라인 블록 제거됨"                             { -not ($r.Text -match '/\*') }
Check "case4 arglist: 잔여 코드 정리 (new SQLiteCommand(sql, conn))"  { $r.Text -match 'new SQLiteCommand\(sql, conn\)\)' }
Check "case4 arglist: 출력 파싱 정상"                                 { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "blockpromote"
Check "case4 arglist: 멱등 (2회차 무변화)"                            { $r2.Text -eq $r.Text }

# NEGATIVE — an already-own-line `/* */` and a `//` comment are NOT inline -> left byte-identical (idempotency base).
$negSrc = @"
public class T {
    void M() {
        /* standalone block comment */
        // standalone line comment
        int a = 0;
    }
}
"@
$f = New-Fixture "negative_ownline.cs" $negSrc
$negBefore = [System.IO.File]::ReadAllBytes($f)
$null = Invoke-Tool $commentDll $f "blockpromote"
$negAfter = [System.IO.File]::ReadAllBytes($f)
Check "negative own-line: 검출 안 됨 + byte-identical (own-line comment은 대상 아님)" { Test-BytesEqual $negBefore $negAfter }

# OPT-IN ISOLATION — the runner's DEFAULT rule set (trailing,space,period,capitalize) must NOT promote an inline
# block comment (blockpromote runs only when explicitly selected). The block stays inline; no // line above the if.
$isoSrc = @"
public class T {
    void M(bool cond) {
        if (/* Att.IsSingleInput&&*/ (cond))
        {
        }
    }
}
"@
$f = New-Fixture "isolation_default.cs" $isoSrc
$r = Invoke-Tool $commentDll $f "trailing,space,period,capitalize"
Check "isolation: 기본 규칙셋은 인라인 블록을 유지 (still /* */)"      { $r.Text -match '/\*' }
Check "isolation: 기본 규칙셋은 // 승격줄을 만들지 않음"               { -not ($r.Text -match "(?m)^\s*//\s*Att") }

Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Sparrow real-xls blockpromote tests FAILED ($($failures.Count)):"
    foreach ($x in $failures) { Write-Host "  - $x" }
    exit 1
}
Write-Host "Sparrow real-xls blockpromote tests passed."

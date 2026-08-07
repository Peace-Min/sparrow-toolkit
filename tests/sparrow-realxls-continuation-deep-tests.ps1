#requires -Version 5.1
<#
    REAL-XLS DEEP-CONTINUATION regression for the Sparrow CLI (SparrowCommentFix `continuation` rule).

    Extends the `continuation` rule so it ALSO normalizes OVER-indented ("deep") continuation lines DOWN to the
    statement's opening line + 4 -- but ONLY when the deep indent is NOT aligned to an open delimiter. This clears
    FORMATTING.CONTINUATION_LINE.BAD_INDENTATION findings on arbitrarily-deep continuations WITHOUT churning
    intentionally delimiter-aligned code that Sparrow accepts.

    POSITIVE fixture is the VERBATIM shape flagged in the real MyApp Sparrow xls (issues_sample_6888.xls),
    Shapes.cs:171 and :197 (identical shape): a `var a =` at indent 12 whose two continuation lines sit at an
    arbitrary indent 23 (aligned to nothing) -> both pulled DOWN to opening+4 (16).

    For the positive this asserts BOTH halves:
      - 검출 (detection): the rule FIRES (positive edit count).
      - 보완 (remediation): both continuation lines land at 16 spaces, output PARSES clean, and a second run is
                             BYTE-IDENTICAL (idempotent).
    NEGATIVES assert byte-identical no-churn: a delimiter-aligned arg block, and an already-opening+4 continuation.

    NOT run by the default validate gate (needs .NET SDK + Roslyn restore). Run via
    `validate.ps1 -IncludeSparrowRealXlsContinuationDeepTests`. Self-skips (not fails) when the .NET SDK is missing.

    Source rows (file:line, checker):
      geo-deep   Shapes.cs:171 / Shapes.cs:197   FORMATTING.CONTINUATION_LINE.BAD_INDENTATION
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow real-xls continuation-deep tests."; $global:SparrowTestSkip = "dotnet SDK 없음"; return }

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

$work = Join-Path $env:TEMP ("sparrow-realxls-contdeep-" + [guid]::NewGuid().ToString("N"))
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
function New-Fixture([string]$name, [string]$content) {
    $f = Join-Path $work $name
    [System.IO.File]::WriteAllText($f, $content, $utf8NoBom)
    return $f
}

# ---- POSITIVE: the VERBATIM Shapes.cs:171/197 deep-continuation shape (only the "NNN. " prefix removed, wrapped
#      minimally so it parses). Both continuation lines are at an arbitrary indent 23 -> pulled DOWN to opening+4 (16).
$geoSrc = @"
public class T { double Haversine(double deltaPhi, double phi1, double phi2, double deltaLambda){
            var a = (System.Math.Sin(deltaPhi / 2) * System.Math.Sin(deltaPhi / 2)) +
                       (System.Math.Cos(phi1) * System.Math.Cos(phi2) *
                       System.Math.Sin(deltaLambda / 2) * System.Math.Sin(deltaLambda / 2));
            return a;
} }
"@
$f = New-Fixture "geo_deep.cs" $geoSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "continuation"
Check "geo-deep: 검출 (rule fires on arbitrary deep continuation)"       { Detected $r.Out }
Check "geo-deep: 보완 (2nd line (paren) pulled to opening+4 = 16 spaces)" { $r.Text -match "(?m)^ {16}\(System\.Math\.Cos" }
Check "geo-deep: 보완 (3rd line pulled to opening+4 = 16 spaces)"         { $r.Text -match "(?m)^ {16}System\.Math\.Sin" }
Check "geo-deep: 실제로 변경됨"                                          { $r.Text -ne $before }
Check "geo-deep: 출력 파싱 정상"                                         { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "continuation"
Check "geo-deep: 멱등 (2회차 무변화)"                                    { $r2.Text -eq $r.Text }

# ---- NEGATIVE: delimiter-aligned args (bbbb/cccc aligned under the col after `Foo(`) -> intentional alignment
#      Sparrow accepts -> byte-identical (the guard leaves them alone). MUST stay byte-for-byte unchanged.
$alignSrc = @"
public class T { void M(int aaaa, int bbbb, int cccc){
            Foo(aaaa,
                bbbb,
                cccc);
} void Foo(int a, int b, int c){} }
"@
$f = New-Fixture "delim_aligned.cs" $alignSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "continuation"
Check "delim-aligned: 무검출 (0 edits)"           { -not (Detected $r.Out) }
Check "delim-aligned: byte-identical no churn"    { $r.Text -eq $before }

# ---- NEGATIVE: a continuation ALREADY at exactly opening+4 -> no-op (no churn from the deep-down pass).
$exactSrc = @"
public class T { void M(bool a, bool b){
        if (a &&
            b)
        {
        }
} }
"@
$f = New-Fixture "exact_plus4.cs" $exactSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "continuation"
Check "exact opening+4: 무검출 (0 edits)"          { -not (Detected $r.Out) }
Check "exact opening+4: byte-identical no churn"   { $r.Text -eq $before }

Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Sparrow real-xls continuation-deep tests FAILED ($($failures.Count)):"
    foreach ($x in $failures) { Write-Host "  - $x" }
    exit 1
}
Write-Host "Sparrow real-xls continuation-deep tests passed."
# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
exit 0

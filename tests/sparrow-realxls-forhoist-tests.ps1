#requires -Version 5.1
<#
    REAL-XLS forhoist regression for the Sparrow CLI (SparrowSyntaxFix).

    The fixture embeds the EXACT multi-declarator for-init block extracted VERBATIM from the real MyApp
    Sparrow xls (issues_sample_6888.xls) for the opt-in `forhoist` rule. This shape is flagged
    SIMULTANEOUSLY by LOOP_VARIABLE + USE_ONE_DECLARATION + OBVIOUS_VARIABLE_TYPE and CANNOT be fixed in
    place (`var` forbids multiple declarators [CS0819], and a for-init is a single declaration slot that
    cannot be split). Real occurrences (all identical shape): WorkQueue.cs:181, Pipeline.cs:129,
    Worker.cs:165, Dispatcher.cs:255.

        for (int i = 0, count = queue.Count; i < count; i++)   ->   var count = queue.Count;
                                                                    for (var i = 0; i < count; i++)

    Asserts BOTH halves the user asked for:
      - 검출 (detection):   the rule FIRES on the real code  -> CLI reports a positive forhoist edit count.
      - 보완 (remediation): the transform is CORRECT        -> the hoisted `var count = queue.Count;` local
                            appears on its OWN line, the for is trimmed to `for (var i = 0; i < count; i++)`,
                            the file actually changed, output PARSES clean, and a second run is BYTE-IDENTICAL.

    NOT run by the default validate gate (needs .NET SDK + Roslyn restore). Run via
    `validate.ps1 -IncludeSparrowRealXlsForHoistTests`. Self-skips (not fails) when the .NET SDK is missing.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow real-xls forhoist tests."; return }

$toolRoot   = Join-Path $RepositoryRoot "tools\_internal"
$syntaxProj = Join-Path $toolRoot "SparrowSyntaxFix\SparrowSyntaxFix.csproj"
if (-not (Test-Path -LiteralPath $syntaxProj)) { throw "missing project: $syntaxProj" }

Write-Host "  building SparrowSyntaxFix (Release)..."
$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
try {
    & $dotnet.Source build -c Release $syntaxProj 2>&1 | Out-Null
} finally { $ErrorActionPreference = $prev }
$syntaxDll = Join-Path $toolRoot "SparrowSyntaxFix\bin\Release\net8.0\SparrowSyntaxFix.dll"
if (-not (Test-Path -LiteralPath $syntaxDll)) { throw "build produced no dll: $syntaxDll" }

$work = Join-Path $env:TEMP ("sparrow-realxls-forhoist-" + [guid]::NewGuid().ToString("N"))
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

# ---- $src is VERBATIM the real multi-declarator for-init block (only the "NNN. " prefix removed), wrapped
#      minimally so it parses, with `queue` (a .Count-bearing collection) in scope. ----

$forhoistSrc = @"
using System.Collections.Generic;
public class T { public void M(Queue<int> queue){
                for (int i = 0, count = queue.Count; i < count; i++)
                {
                    System.Console.WriteLine(i + count);
                }
} }
"@
$f = New-Fixture "forhoist.cs" $forhoistSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $syntaxDll $f "forhoist"
Check "forhoist: 검출 (rule fires on multi-declarator for-init)" { Detected $r.Out }
Check "forhoist: 보완 (count hoisted to its own var line)"        { $r.Text -match '(?m)^\s*var count = queue\.Count;\s*$' }
Check "forhoist: 보완 (for trimmed to single var declarator)"    { $r.Text -match 'for \(var i = 0; i < count; i\+\+\)' }
Check "forhoist: multi-declarator init removed"                  { -not ($r.Text -match 'int i = 0, count') }
Check "forhoist: 실제로 변경됨"                                   { $r.Text -ne $before }
Check "forhoist: 출력 파싱 정상"                                  { ParsesClean $f }
$r2 = Invoke-Tool $syntaxDll $f "forhoist"
Check "forhoist: 멱등 (2회차 무변화)"                             { $r2.Text -eq $r.Text }

Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Sparrow real-xls forhoist tests FAILED ($($failures.Count)):"
    foreach ($x in $failures) { Write-Host "  - $x" }
    exit 1
}
Write-Host "Sparrow real-xls forhoist tests passed."

#requires -Version 5.1
<#
    REAL-XLS C3 regression for the Sparrow CLI (SparrowSyntaxFix + SparrowCommentFix).

    Each fixture embeds the EXACT source block extracted VERBATIM from the real MyApp Sparrow xls
    (issues_sample_6888.xls) for the five C3 rules added/extended in v0.1.50 — only the
    leading "NNN. " line-number prefix from the xls "소스 코드" column was stripped; indentation and
    the flagged line are byte-for-byte the detected code.

    For every rule this asserts BOTH halves the user asked for:
      - 검출 (detection): the rule FIRES on the real code  -> CLI reports a positive edit count.
      - 보완 (remediation): the transform is CORRECT        -> expected output text is produced,
                             the file actually changed, output PARSES clean (no syntax break),
                             and a second run is BYTE-IDENTICAL (idempotent).

    NOT run by the default validate gate (needs .NET SDK + Roslyn restore). Run via
    `validate.ps1 -IncludeSparrowRealXlsC3Tests`. Self-skips (not fails) when the .NET SDK is missing.

    Source rows (file:line, checker):
      forvar             QueryService.cs:413   PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING
      fieldsplit         ChartViewModel.cs:183  USE_ONE_DECLARATION_PER_LINE
      emptystmt          MainView.xaml.cs:1017          USE_ONE_STATEMENT_PER_LINE
      continuation-chain ChartViewModel2.cs:444  FORMATTING.CONTINUATION_LINE.BAD_INDENTATION
      continuation-init  LineManager.cs:83    FORMATTING.CONTINUATION_LINE.BAD_INDENTATION
      period-block       Consts.cs:52                FORMATTING.COMMENT.MISSING_PERIOD
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow real-xls C3 tests."; return }

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

$work = Join-Path $env:TEMP ("sparrow-realxls-c3-" + [guid]::NewGuid().ToString("N"))
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

# ---- Each $src below is VERBATIM from the xls (only the "NNN. " prefix removed), wrapped minimally so it parses. ----

# forvar — QueryService.cs:413 (single-declarator for-init)
$forvarSrc = @"
using System.Collections.Generic;
public class DataPoint { public double X; public double Y; }
public class T { public void M(int count, List<double> xPoints, List<double> yPoints, List<DataPoint> results){
                for (int i = 0; i < count; i++)
                {
                    results.Add(new DataPoint { X = xPoints[i], Y = yPoints[i] });
                }
} }
"@
$f = New-Fixture "forvar.cs" $forvarSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $syntaxDll $f "forvar"
Check "forvar: 검출 (rule fires on real for-init)"        { Detected $r.Out }
Check "forvar: 보완 (int i -> var i)"                     { $r.Text -match 'for \(var i = 0; i < count; i\+\+\)' }
Check "forvar: 실제로 변경됨"                              { $r.Text -ne $before }
Check "forvar: 출력 파싱 정상"                            { ParsesClean $f }
$r2 = Invoke-Tool $syntaxDll $f "forvar"
Check "forvar: 멱등 (2회차 무변화)"                       { $r2.Text -eq $r.Text }

# fieldsplit — ChartViewModel.cs:183 (multi-declarator field, with leading comment)
$fieldSrc = @"
using System.Windows.Threading;
public class T {
        private DispatcherTimer _dockingResizeTimer;

        // 가장 최근 순수 데이터 범위. 도킹/리사이즈 시 재계산용.
        private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;
}
"@
$f = New-Fixture "fieldsplit.cs" $fieldSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $syntaxDll $f "fieldsplit"
Check "fieldsplit: 검출 (rule fires on 4-way field)"      { Detected $r.Out }
Check "fieldsplit: 보완 (4 single-field lines)"           { ($r.Text -match 'private double _rawXMin;') -and ($r.Text -match 'private double _rawXMax;') -and ($r.Text -match 'private double _rawYMin;') -and ($r.Text -match 'private double _rawYMax;') }
# comment survival asserted encoding-agnostically (PS 5.1 mangles Korean literals in a no-BOM .ps1):
# the leading // comment line must still exist above the split fields.
Check "fieldsplit: 선행 주석 보존 (// line survives)"     { ([regex]::Matches($r.Text, '(?m)^\s*//')).Count -ge 1 }
Check "fieldsplit: 출력 파싱 정상"                        { ParsesClean $f }
$r2 = Invoke-Tool $syntaxDll $f "fieldsplit"
Check "fieldsplit: 멱등 (2회차 무변화)"                   { $r2.Text -eq $r.Text }

# emptystmt — MainView.xaml.cs:1017 (double semicolon)
$emptySrc = @"
using System.Windows.Controls;
using System.Collections;
public class T { void RemoveChild_Click(object s, object e){}
 public void M(IEnumerable items){
  foreach(var ci in items){
   if (ci is MenuItem cim)
   {
       cim.Click += RemoveChild_Click; ;
   }
  }
 }
}
"@
$f = New-Fixture "emptystmt.cs" $emptySrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $syntaxDll $f "emptystmt"
Check "emptystmt: 검출 (rule fires on '; ;')"             { Detected $r.Out }
Check "emptystmt: 보완 (redundant ';' removed)"           { ($r.Text -match 'cim\.Click \+= RemoveChild_Click;') -and (-not ($r.Text -match 'RemoveChild_Click; ;')) }
Check "emptystmt: 출력 파싱 정상"                         { ParsesClean $f }
$r2 = Invoke-Tool $syntaxDll $f "emptystmt"
Check "emptystmt: 멱등 (2회차 무변화)"                    { $r2.Text -eq $r.Text }

# continuation-chain — ChartViewModel2.cs:444 (method chain at receiver indent)
$chainSrc = @"
public class T { void M(){
                {
                        var frames = _seriesIndexByKey
                        .OrderBy(kvp => kvp.Value)
                        .Select(kvp => new AddPointDTO(kvp.Value, null, null))
                        .ToList();
                }
} }
"@
$f = New-Fixture "cont_chain.cs" $chainSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "continuation"
Check "continuation-chain: 검출 (rule fires on chain)"    { Detected $r.Out }
Check "continuation-chain: 보완 (.OrderBy pulled +4 = 28 spaces)" { $r.Text -match "(?m)^ {28}\.OrderBy\(" }
Check "continuation-chain: .ToList도 +4"                  { $r.Text -match "(?m)^ {28}\.ToList\(" }
Check "continuation-chain: 출력 파싱 정상"                { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "continuation"
Check "continuation-chain: 멱등 (2회차 무변화)"           { $r2.Text -eq $r.Text }

# continuation-init — LineManager.cs:83 (collection initializer brace/elements shallow)
$initSrc = @"
public class T { void M(){
            _line.AddPoints(new[]
            {
            new SeriesPoint { X = xAxis.Minimum, Y = _y.Value },
            new SeriesPoint { X = xAxis.Maximum, Y = _y.Value }
            });
} }
"@
$f = New-Fixture "cont_init.cs" $initSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "continuation"
Check "continuation-init: 검출 (rule fires on initializer)" { Detected $r.Out }
Check "continuation-init: 보완 (brace pulled +4 = 16 spaces)" { $r.Text -match "(?m)^ {16}\{" }
Check "continuation-init: 요소도 +4 (16 spaces)"          { $r.Text -match "(?m)^ {16}new SeriesPoint" }
Check "continuation-init: 실제로 변경됨"                  { $r.Text -ne $before }
Check "continuation-init: 출력 파싱 정상"                 { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "continuation"
Check "continuation-init: 멱등 (2회차 무변화)"            { $r2.Text -eq $r.Text }

# period-block — Consts.cs:52 (trailing inline /* */ block comment)
$perSrc = @"
public class T {
        public static Color RouteColor = Color.FromRgb(255, 0, 255); /* Color.FromRgb(253, 8, 9);*/
}
"@
$f = New-Fixture "period_block.cs" $perSrc
$before = [System.IO.File]::ReadAllText($f)
$r = Invoke-Tool $commentDll $f "period"
Check "period-block: 검출 (rule fires on inline /* */)"   { Detected $r.Out }
Check "period-block: 보완 (period added inside block)"    { $r.Text -match [regex]::Escape('/* Color.FromRgb(253, 8, 9);.*/') }
Check "period-block: 여전히 블록 (// 로 변환 안 됨)"       { -not ($r.Text -match '//\s*Color\.FromRgb') }
Check "period-block: 출력 파싱 정상"                      { ParsesClean $f }
$r2 = Invoke-Tool $commentDll $f "period"
Check "period-block: 멱등 (2회차 무변화)"                 { $r2.Text -eq $r.Text }

Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Sparrow real-xls C3 tests FAILED ($($failures.Count)):"
    foreach ($x in $failures) { Write-Host "  - $x" }
    exit 1
}
Write-Host "Sparrow real-xls C3 tests passed."

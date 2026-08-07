#requires -Version 5.1
<#
    GROUNDED real-pattern regression for the Sparrow [코드 규칙] (SparrowSyntaxFix) + [주석·레이아웃] (SparrowCommentFix)
    pipeline. Unlike sparrow-loop-tests.ps1 (which targets three specific fixed rules), this harness seeds ONE
    COMPILABLE net8.0 file carrying the REAL code shapes flagged in the actual MyApp Sparrow xls, runs the full
    canonical two-tool pipeline over it, and asserts:

      0) 0 compile errors BEFORE any transform      (the seed is valid real-shaped code, not synthetic gibberish).
      1) The full pipeline (SyntaxFix SAFE+foreachcast, then CommentFix all) runs at exit 0.
      2) 0 compile errors AFTER the pipeline         (the transforms NEVER break compilation -- incl. the inline
         /* */ period case and the foreachcast rewrite).
      3) Convergence/idempotency: two MORE full pipeline passes leave the file BYTE-IDENTICAL to pass 1
         (converged; no drift, no rule oscillation).
      4) Per-pattern: every real MyApp shape produced its expected transform (or, for the objectvar-safe
         IDictionary<>=ExpandoObject case, stayed UNCHANGED because narrowing would break compile).

    NOT run by the default validate gate (needs the .NET SDK + Roslyn restore). Run via
    `validate.ps1 -IncludeSparrowRealPatternTests`. Self-skips (not fails) when the .NET SDK is missing.

    net8.0 note: the seed uses System.Xml (XmlNode/XmlNodeList) + System.Data (DataColumn/DataColumnCollection)
    so it compiles on PLAIN net8.0 -- no WPF/UIElement. PS 5.1 notes: @() before .Count; no &&/ternary; files
    read as raw BYTES for byte-identity; ASCII-only source (no BOM needed).
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow real-pattern tests."; $global:SparrowTestSkip = "dotnet SDK 없음"; return }

$commentProj = Join-Path $RepositoryRoot "tools\_internal\SparrowCommentFix\SparrowCommentFix.csproj"
$syntaxProj  = Join-Path $RepositoryRoot "tools\_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj"
foreach ($p in @($commentProj, $syntaxProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

$commentDll = Join-Path $RepositoryRoot "tools\_internal\SparrowCommentFix\bin\Release\net8.0\SparrowCommentFix.dll"
$syntaxDll  = Join-Path $RepositoryRoot "tools\_internal\SparrowSyntaxFix\bin\Release\net8.0\SparrowSyntaxFix.dll"

# canonical runner rule sets (the runner's order): SyntaxFix = SAFE rules + foreachcast (NO narrowing/localconst/
# objectinitializer -- narrowing would break the IDictionary<>=ExpandoObject case that is left compiling on purpose).
$syntaxRules  = "nullvar,objectvar-safe,obviousvar,arrayvar-safe,parens,foreachcast"
$commentRules = "flatten,trailing,space,period,capitalize,memberblank,onedeclaration,onestatement,continuation,linqalign"

$work = Join-Path $env:TEMP ("sparrow-realpattern-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { Write-Host "  [FAIL] $name"; $script:failures += $name } }
    catch { Write-Host "  [FAIL] $name ($($_.Exception.Message))"; $script:failures += "$name ($($_.Exception.Message))" }
}
function Test-BytesEqual([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}
function Invoke-Comment([string[]]$ToolArgs) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { & $dotnet.Source $commentDll @ToolArgs 2>&1 | Out-Null } finally { $ErrorActionPreference = $prev }
    return $LASTEXITCODE
}
function Invoke-Syntax([string[]]$ToolArgs) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { & $dotnet.Source $syntaxDll @ToolArgs 2>&1 | Out-Null } finally { $ErrorActionPreference = $prev }
    return $LASTEXITCODE
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Compile a single .cs in an ISOLATED dir (one .cs only -> no duplicate-type across before/after copies).
function Assert-Compiles([string]$srcFile, [string]$label) {
    $dir = Join-Path $work ("build-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -LiteralPath $srcFile -Destination (Join-Path $dir "Sample.cs")
    $proj = Join-Path $dir "P.csproj"
    [System.IO.File]::WriteAllText($proj, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>false</ImplicitUsings>
    <OutputType>Library</OutputType>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
  </PropertyGroup>
</Project>
"@, $utf8NoBom)
    $out = (& $dotnet.Source build $proj -c Release -v q 2>&1 | Out-String)
    $ok = ($LASTEXITCODE -eq 0)
    if (-not $ok) { Write-Host $out }
    Check $label { $ok }
    return $ok
}

# The seed: every REAL MyApp-flagged shape, in ONE file that compiles at 0 errors on plain net8.0.
$seed = @'
using System.Xml;
using System.Data;

namespace Sparrow.RealPattern
{
    public class SampleComponent { public string Name; }
    public class SampleEntity { public int Id; }

    public interface IThing {
        uint QueryCount(int[] a);
        // 20210407 comment
        System.Data.DataTable QueryData(int[] a);
        System.Data.DataTable QueryMore();
    }

    public class Demo
    {
        private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;

        void DoA() { }
        void DoB() { }

        void Run()
        {
            SampleComponent sampleComponent = null;
            SampleEntity parentData = new SampleEntity();
            System.Collections.Generic.IDictionary<string, object> expando = new System.Dynamic.ExpandoObject();
            string szName = "test";
            int nCount = 27;
            string szKey = "k";
            int nIndex = 3;
            int bb = 1;

            if (nIndex > 0 && nIndex <= nCount - 1) { }
            if (!string.IsNullOrEmpty(szKey) && sampleComponent != null) { }

            XmlDocument xmlDoc = new XmlDocument();
            foreach (XmlNode node in xmlDoc.ChildNodes) { var nm = node.Name; }

            DataTable dt = new DataTable();
            foreach (DataColumn col in dt.Columns) { var cn = col.ColumnName; }

            //test
            int aa = 0; //note
            // Miss Distance(Min/Max/Avg)
            // -- section start --
            if (/* att.IsSingleInput&&*/ (nCount == 1)) { }
            // <variableSource sourceID="x">
            // .foo bar

            var sql = "SELECT " +
"FROM x";
            var packed = (aa << 24) |
(bb << 16);

            DoA(); DoB();

            _rawXMin = _rawXMax = _rawYMin = _rawYMax = 0;
            var e = expando; var p = parentData; var s = szName;
        }
    }
}
'@

try {
    Write-Host "  building SparrowCommentFix + SparrowSyntaxFix (Release)..."
    & $dotnet.Source build $commentProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowCommentFix build failed" }
    & $dotnet.Source build $syntaxProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowSyntaxFix build failed" }

    # -- 0) seed + 0-errors BEFORE --------------------------------------------------------------------
    $seedFile = Join-Path $work "Seed.cs"
    [System.IO.File]::WriteAllText($seedFile, $seed, $utf8NoBom)
    Assert-Compiles $seedFile "0 errors BEFORE any transform (seed is valid real-shaped code)" | Out-Null

    # -- 1) run the full pipeline on a working copy (canonical order: SyntaxFix then CommentFix) --------
    $wf = Join-Path $work "Work.cs"
    Copy-Item -LiteralPath $seedFile -Destination $wf
    Check "pipeline: SyntaxFix (safe+foreachcast) exit 0" { (Invoke-Syntax @($wf, "--rules", $syntaxRules)) -eq 0 }
    Check "pipeline: CommentFix (all) exit 0"            { (Invoke-Comment @($wf, "--rules", $commentRules)) -eq 0 }
    $after1 = [System.IO.File]::ReadAllBytes($wf)

    # -- 2) 0-errors AFTER ----------------------------------------------------------------------------
    Assert-Compiles $wf "0 errors AFTER full pipeline (transforms never break compilation)" | Out-Null

    # -- 3) convergence/idempotency: 2 MORE full passes -> byte-identical to pass 1 --------------------
    for ($pass = 2; $pass -le 3; $pass++) {
        Invoke-Syntax @($wf, "--rules", $syntaxRules)  | Out-Null
        Invoke-Comment @($wf, "--rules", $commentRules) | Out-Null
        $again = [System.IO.File]::ReadAllBytes($wf)
        Check "convergence: pass $pass byte-identical to pass 1 (no drift/oscillation)" { Test-BytesEqual $after1 $again }
    }

    # -- 4) per-pattern assertions (final text) + before->after table ----------------------------------
    $final = [System.IO.File]::ReadAllText($wf, $utf8NoBom)
    $nl = "`r`n"; if (-not $final.Contains("`r`n")) { $nl = "`n" }
    function Has([string]$s) { return $final.Contains($s) }

    Write-Host ""
    Write-Host "  --- per-pattern (real MyApp shapes) ---"

    # [코드 규칙]
    Check "A nullvar: SampleComponent=null -> var (SampleComponent)null"        { Has "var sampleComponent = (SampleComponent)null;" }
    Check "A objectvar-safe: new SampleEntity() -> var"                  { Has "var parentData = new SampleEntity();" }
    Check "A objectvar-safe SKIP: IDictionary=ExpandoObject UNCHANGED"        { Has "System.Collections.Generic.IDictionary<string, object> expando = new System.Dynamic.ExpandoObject();" }
    Check "A obviousvar: string szName -> var"                               { Has 'var szName = "test";' }
    Check "A obviousvar: int nCount -> var"                                   { Has "var nCount = 27;" }
    Check "A parens: relational operands parenthesized"                       { Has "if ((nIndex > 0) && (nIndex <= nCount - 1)) { }" }
    Check "A parens(unary): !expr and != operands parenthesized"             { Has "if ((!string.IsNullOrEmpty(szKey)) && (sampleComponent != null)) { }" }
    Check "A foreachcast(XmlNode, member-access): Cast<XmlNode>"              { Has "foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(xmlDoc.ChildNodes))" }
    Check "A foreachcast(DataColumn): Cast<DataColumn>"                       { Has "foreach (var col in System.Linq.Enumerable.Cast<DataColumn>(dt.Columns))" }

    # [주석·레이아웃]
    Check "B space: //test -> // Test. (space + cap + period)"                { Has "// Test." }
    Check "B trailing: //note promoted ABOVE as // Note."                     { Has "// Note.$nl            var aa = 0;" }
    Check "B period ')'-ending: Miss Distance(...)."                          { Has "// Miss Distance(Min/Max/Avg)." }
    Check "B period divider-with-text: -- section start --."                  { Has "// -- section start --." }
    Check "B period inline block-comment stays /* */ (att -> Att.)"           { Has "if (/* Att.IsSingleInput&&.*/ (nCount == 1)) { }" }
    Check "B capitalize XML-markup dead comment: VariableSource ..."          { Has '// VariableSource sourceID="x">.' }
    Check "B capitalize leading-dot: // .foo bar -> // Foo bar."              { Has "// Foo bar." }
    Check "B memberblank: blank BEFORE comment between interface members"     { Has ("uint QueryCount(int[] a);" + $nl + $nl + "        // 20210407 comment.") }
    Check "B memberblank: blank between last two members"                     { Has ("System.Data.DataTable QueryData(int[] a);" + $nl + $nl + "        System.Data.DataTable QueryMore();") }
    Check "B continuation string-concat: '+' cont line reindented stmt+4"     { Has ('var sql = "SELECT " +' + $nl + '                "FROM x";') }
    Check "B continuation bitwise: '|' cont line reindented stmt+4"           { Has ("var packed = (aa << 24) |" + $nl + "                (bb << 16);") }
    Check "B onedeclaration: 4-field decl split to 4 lines"                   { (Has "private double _rawXMin;") -and (Has "private double _rawXMax;") -and (Has "private double _rawYMin;") -and (Has "private double _rawYMax;") }
    Check "B onestatement: DoA(); DoB(); split to separate lines"            { (Has ("DoA();" + $nl)) -and (Has ("DoB();" + $nl)) -and (-not (Has "DoA(); DoB();")) }

    Write-Host ""
    Write-Host "  --- before -> after (captured lines) ---"
    $rows = @(
        @("nullvar",            "SampleComponent sampleComponent = null;",                 "var sampleComponent = (SampleComponent)null;"),
        @("objectvar-safe",     "SampleEntity parentData = new SampleEntity();", "var parentData = new SampleEntity();"),
        @("objectvar-safe SKIP","IDictionary<...> expando = new ExpandoObject();",         "(unchanged)"),
        @("obviousvar",         'string szName = "test"; / int nCount = 27;',              'var szName = "test"; / var nCount = 27;'),
        @("parens",             "if (nIndex > 0 && nIndex <= nCount - 1)",                 "if ((nIndex > 0) && (nIndex <= nCount - 1))"),
        @("parens(unary)",      "if (!IsNullOrEmpty(szKey) && sampleComponent != null)",  "if ((!...(szKey)) && (sampleComponent != null))"),
        @("foreachcast(Xml)",   "foreach (XmlNode node in xmlDoc.ChildNodes)",             "foreach (var node in ...Cast<XmlNode>(xmlDoc.ChildNodes))"),
        @("foreachcast(DataCol)","foreach (DataColumn col in dt.Columns)",                 "foreach (var col in ...Cast<DataColumn>(dt.Columns))"),
        @("space",              "//test",                                                  "// Test."),
        @("trailing",           "int aa = 0; //note",                                      "// Note. (promoted above); var aa = 0;"),
        @("period ')'",         "// Miss Distance(Min/Max/Avg)",                           "// Miss Distance(Min/Max/Avg)."),
        @("period divider",     "// -- section start --",                                  "// -- section start --."),
        @("period inline block","/* att.IsSingleInput&&*/",                                "/* Att.IsSingleInput&&.*/ (still block)"),
        @("capitalize XML",     '// <variableSource sourceID="x">',                        '// VariableSource sourceID="x">.'),
        @("capitalize dot",     "// .foo bar",                                             "// Foo bar."),
        @("memberblank",        "member; // comment; member (no blanks)",                  "blanks between EVERY member, blank BEFORE comment"),
        @("continuation +",     'var sql = ""SELECT "" +\n""FROM x"";',                    "'+' cont line reindented to stmt+4"),
        @("continuation |",     "var packed = (aa << 24) |\n(bb << 16);",                  "'|' cont line reindented to stmt+4"),
        @("onedeclaration",     "private double _rawXMin,_rawXMax,_rawYMin,_rawYMax;",     "split to 4 single-field lines"),
        @("onestatement",       "DoA(); DoB();",                                           "DoA(); / DoB(); (separate lines)")
    )
    foreach ($r in $rows) { Write-Host ("    {0,-22} | {1,-52} -> {2}" -f $r[0], $r[1], $r[2]) }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("Sparrow real-pattern tests failed:`n  " + ($failures -join "`n  ")) }
Write-Host "Sparrow real-pattern tests passed."
# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
exit 0

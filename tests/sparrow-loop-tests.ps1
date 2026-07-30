#requires -Version 5.1
<#
    "충분한 루프 테스트" for the three deterministic-rule fixes (memberblank, continuation, foreachcast).
    NOT run by the default validate gate (needs the .NET SDK + Roslyn/WPF restore). Run manually or via
    `validate.ps1 -IncludeSparrowLoopTests`. Self-skips (not fails) when the .NET SDK is missing.

    Covers three things beyond the per-rule fixture harnesses:
      1. Idempotency loops: apply each fixed rule to a real-pattern file, then apply it 3 MORE times ->
         byte-identical after the first application (no drift / no double edits).
      2. Compile verification: the foreachcast output (and the multi-rule convergence file) build with
         `dotnet build` at 0 errors -- the transforms must never break compilation.
      3. Cross-rule convergence: ONE realistic .cs file carrying all three patterns PLUS a few other comment
         patterns (space/period/capitalize). Run the full comment-rule set then the SyntaxFix rules, run the
         whole pipeline again -> the second pass is byte-identical (converged) and the final file compiles.

    PS 5.1 notes: collections wrapped in @() before .Count; no &&/ternary; files read as raw BYTES for the
    byte-identity checks (the TOOLS write bytes via .NET). This file is ASCII-only -> BOM not required.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping Sparrow loop tests."; return }

$commentProj = Join-Path $RepositoryRoot "tools\_internal\SparrowCommentFix\SparrowCommentFix.csproj"
$syntaxProj  = Join-Path $RepositoryRoot "tools\_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj"
foreach ($p in @($commentProj, $syntaxProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

$commentDll = Join-Path $RepositoryRoot "tools\_internal\SparrowCommentFix\bin\Release\net8.0\SparrowCommentFix.dll"
$syntaxDll  = Join-Path $RepositoryRoot "tools\_internal\SparrowSyntaxFix\bin\Release\net8.0\SparrowSyntaxFix.dll"

$work = Join-Path $env:TEMP ("sparrow-loop-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { $script:failures += $name } }
    catch { $script:failures += "$name ($($_.Exception.Message))" }
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
function New-File([string]$Name, [string]$Content) {
    $path = Join-Path $work $Name
    [System.IO.File]::WriteAllText($path, $Content, $utf8NoBom)
    return $path
}

try {
    Write-Host "  building SparrowCommentFix + SparrowSyntaxFix (Release)..."
    & $dotnet.Source build $commentProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowCommentFix build failed" }
    & $dotnet.Source build $syntaxProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowSyntaxFix build failed" }

    # ============================================================================================
    # 1) IDEMPOTENCY LOOPS -- apply once, then 3 MORE times; byte-identical after the first application.
    # ============================================================================================

    # -- memberblank (comment-before-member interface) --
    $mbSrc = @'
public interface I {
  uint PreProcessDataCount(int[] a);
  // 20210407 userB CA1026.
  DataTable PreProcessData(int[] a, uint b, uint c);
  DataTable PreProcessAddVariableInfo();
  DataTable DataFilterInfoList();
}
'@
    $mbFile = New-File "loop_memberblank.cs" $mbSrc
    Check "memberblank loop: first apply exit 0" { (Invoke-Comment @($mbFile, "--rules", "memberblank")) -eq 0 }
    $mb1 = [System.IO.File]::ReadAllBytes($mbFile)
    for ($n = 0; $n -lt 3; $n++) { Invoke-Comment @($mbFile, "--rules", "memberblank") | Out-Null }
    $mb4 = [System.IO.File]::ReadAllBytes($mbFile)
    Check "memberblank loop: byte-identical after 3 more applies" { Test-BytesEqual $mb1 $mb4 }

    # -- continuation (operator-led: +, |, <<) --
    $ctSrc = @'
class C {
    void M() {
        sql = $"SELECT {a} " +
        $"FROM {b} ";
        x = (color.A << 24) |
        (color.R << 16);
        d = System.Math.Abs(left.R - right.R) +
        System.Math.Abs(left.G - right.G);
    }
}
'@
    $ctFile = New-File "loop_continuation.cs" $ctSrc
    Check "continuation loop: first apply exit 0" { (Invoke-Comment @($ctFile, "--rules", "continuation")) -eq 0 }
    $ct1 = [System.IO.File]::ReadAllBytes($ctFile)
    for ($n = 0; $n -lt 3; $n++) { Invoke-Comment @($ctFile, "--rules", "continuation") | Out-Null }
    $ct4 = [System.IO.File]::ReadAllBytes($ctFile)
    Check "continuation loop: byte-identical after 3 more applies" { Test-BytesEqual $ct1 $ct4 }

    # -- foreachcast (non-XmlNode explicit element types) --
    $fcSrc = @'
using System.Collections.Generic;
using System.Data;
using System.Windows.Controls;
class TreeNodeData { }
class Holder { public List<TreeNodeData> Items { get; } = new List<TreeNodeData>(); }
class C {
    void M(DataTable srcTable, TabControl tab, Holder delObject) {
        foreach (DataColumn c in srcTable.Columns) { _ = c.ColumnName; }
        foreach (TabItem obj in tab.Items) { _ = obj; }
        foreach (TreeNodeData x in delObject.Items) { _ = x; }
    }
}
'@
    $fcFile = New-File "loop_foreachcast.cs" $fcSrc
    Check "foreachcast loop: first apply exit 0" { (Invoke-Syntax @($fcFile, "--rules", "foreachcast")) -eq 0 }
    $fc1 = [System.IO.File]::ReadAllBytes($fcFile)
    for ($n = 0; $n -lt 3; $n++) { Invoke-Syntax @($fcFile, "--rules", "foreachcast") | Out-Null }
    $fc4 = [System.IO.File]::ReadAllBytes($fcFile)
    Check "foreachcast loop: byte-identical after 3 more applies" { Test-BytesEqual $fc1 $fc4 }
    $fcText = [System.IO.File]::ReadAllText($fcFile, $utf8NoBom)
    Check "foreachcast loop: DataColumn converted" { $fcText.Contains("foreach (var c in System.Linq.Enumerable.Cast<DataColumn>(srcTable.Columns))") }
    Check "foreachcast loop: TabItem converted" { $fcText.Contains("foreach (var obj in System.Linq.Enumerable.Cast<TabItem>(tab.Items))") }
    Check "foreachcast loop: generic-List member-access converted" { $fcText.Contains("foreach (var x in System.Linq.Enumerable.Cast<TreeNodeData>(delObject.Items))") }

    # ============================================================================================
    # 2) COMPILE VERIFICATION of the foreachcast output (WPF + System.Data). 0 errors required.
    # ============================================================================================
    function Assert-Compiles([string]$dir, [string]$label) {
        $proj = Join-Path $dir "P.csproj"
        [System.IO.File]::WriteAllText($proj, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
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
    }
    $fcBuildDir = Join-Path $work "fcbuild"
    New-Item -ItemType Directory -Force -Path $fcBuildDir | Out-Null
    Copy-Item -LiteralPath $fcFile -Destination (Join-Path $fcBuildDir "Sample.cs")
    Assert-Compiles $fcBuildDir "foreachcast output compiles (0 errors)"

    # ============================================================================================
    # 3) CROSS-RULE CONVERGENCE -- one file, all three patterns + other comment rules. Run comment-all then
    #    syntax-all; run the whole pipeline again -> second pass byte-identical; final file compiles.
    # ============================================================================================
    $convSrc = @'
using System.Collections.Generic;
using System.Data;
using System.Windows.Controls;

public interface IReport {
    string PrepareData(string name);
    // 20210407 userB marker
    bool SetWriteCSVFile(string path);
    bool Flush();
}

class TreeNodeData { }
class Holder { public List<TreeNodeData> Items { get; } = new List<TreeNodeData>(); }

class C {
    //badcase
    // hello
    string Build(DataTable srcTable, TabControl tab, Holder delObject, int a, int b) {
        string sql = $"SELECT {a} " +
        $"FROM {b} ";
        int x = (a << 8) |
        (b << 4);
        foreach (DataColumn c in srcTable.Columns) { _ = c.ColumnName; }
        foreach (TabItem obj in tab.Items) { _ = obj; }
        foreach (TreeNodeData m in delObject.Items) { _ = m; }
        return sql + x;
    }
}
'@
    $convDir = Join-Path $work "conv"
    New-Item -ItemType Directory -Force -Path $convDir | Out-Null
    $convFile = Join-Path $convDir "Sample.cs"
    [System.IO.File]::WriteAllText($convFile, $convSrc, $utf8NoBom)

    # pass 1
    Check "convergence: pass1 comment-all exit 0" { (Invoke-Comment @($convFile, "--rules", "all")) -eq 0 }
    Check "convergence: pass1 syntax-all exit 0" { (Invoke-Syntax @($convFile, "--rules", "all")) -eq 0 }
    $convAfter1 = [System.IO.File]::ReadAllBytes($convFile)
    # pass 2 (whole pipeline again)
    Check "convergence: pass2 comment-all exit 0" { (Invoke-Comment @($convFile, "--rules", "all")) -eq 0 }
    Check "convergence: pass2 syntax-all exit 0" { (Invoke-Syntax @($convFile, "--rules", "all")) -eq 0 }
    $convAfter2 = [System.IO.File]::ReadAllBytes($convFile)
    Check "convergence: second pipeline pass byte-identical (converged/idempotent)" { Test-BytesEqual $convAfter1 $convAfter2 }

    $convText = [System.IO.File]::ReadAllText($convFile, $utf8NoBom)
    # spot-check every rule actually fired and settled
    Check "convergence: memberblank separated interface members" { $convText.Contains("string PrepareData(string name);`r`n`r`n") -or $convText.Contains("string PrepareData(string name);`n`n") }
    Check "convergence: continuation '+' reindented" { $convText.Contains("`"SELECT {a} `" +`r`n            `$`"FROM {b} `";") -or $convText.Contains("`"SELECT {a} `" +`n            `$`"FROM {b} `";") }
    Check "convergence: continuation '|' reindented" { $convText.Contains("(a << 8) |`r`n            (b << 4);") -or $convText.Contains("(a << 8) |`n            (b << 4);") }
    Check "convergence: foreachcast DataColumn converted" { $convText.Contains("System.Linq.Enumerable.Cast<DataColumn>(srcTable.Columns)") }
    Check "convergence: foreachcast TabItem converted" { $convText.Contains("System.Linq.Enumerable.Cast<TabItem>(tab.Items)") }
    Check "convergence: foreachcast generic-List converted" { $convText.Contains("System.Linq.Enumerable.Cast<TreeNodeData>(delObject.Items)") }
    Check "convergence: comment 'hello' capitalized + periodized" { $convText.Contains("// Hello.") }

    Assert-Compiles $convDir "convergence: final multi-rule file compiles (0 errors)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("Sparrow loop tests failed:`n  " + ($failures -join "`n  ")) }
Write-Host "Sparrow loop tests passed."

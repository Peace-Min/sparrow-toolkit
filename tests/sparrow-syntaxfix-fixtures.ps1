#requires -Version 5.1
<#
    Opt-in end-to-end smoke test for SparrowSyntaxFix. NOT run by the default validate gate (needs the
    .NET SDK + a Roslyn restore -- env/time heavy). Run it manually, or via `validate.ps1 -IncludeSyntaxFixE2E`.

    It builds the FixtureTests harness + the tool, runs the in-memory before->after harness (the exact real
    Sparrow cases), then drives the real CLI over an on-disk UTF-8-BOM + CRLF file containing the real
    6869-style findings to assert: nullvar + objectvar-safe + objectvar-narrowing + objectinitializer +
    arrayvar-safe + arrayvar-narrowing + foreachcast + obviousvar + localconst + parens applied,
    the rewritten test project builds, BOM + CRLF preserved, generated `.Designer.cs`
    skipped, --dry-run writes nothing, and a re-run is byte-identical (idempotent). Skips cleanly (not
    fails) when the .NET SDK is missing.

    PS 5.1 notes honored here: collections wrapped in @() before .Count; no &&/ternary/null-coalescing;
    files read as raw BYTES for BOM/idempotency checks (the TOOL writes bytes via .NET, not via PowerShell).
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping SparrowSyntaxFix E2E."; return }

$toolDir = Join-Path $RepositoryRoot "tools\_internal\SparrowSyntaxFix"
$toolProj = Join-Path $toolDir "SparrowSyntaxFix.csproj"
$fixtureProj = Join-Path $toolDir "FixtureTests\FixtureTests.csproj"
foreach ($p in @($toolProj, $fixtureProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

$work = Join-Path $env:TEMP ("sparrow-syntaxfix-e2e-" + [guid]::NewGuid().ToString("N"))
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

try {
    Write-Host "  building FixtureTests + SparrowSyntaxFix (Release)..."
    & $dotnet.Source build $fixtureProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FixtureTests build failed" }
    & $dotnet.Source build $toolProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowSyntaxFix build failed" }

    # 1) in-memory before->after harness (exact real Sparrow cases). Surface its output only on failure.
    $fxOut = (& $dotnet.Source run --project $fixtureProj -c Release --no-build 2>&1 | Out-String)
    $fxExit = $LASTEXITCODE
    if ($fxExit -ne 0) { Write-Host $fxOut }
    Check "C# fixture harness exits 0 (all before->after cases pass)" { $fxExit -eq 0 }

    # 2) real on-disk project/file: UTF-8 BOM + CRLF; all Track-A Roslyn rules apply to real 6869-style patterns.
    $srcDir = Join-Path $work "src"
    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
    $nl = "`r`n"
    $lines = @(
        "using System;",
        "using System.Collections.Generic;",
        "using System.Xml;",
        "",
        "interface IFoo { }",
        "class Foo : IFoo { }",
        "class PropData { }",
        "class InitTarget { public int A { get; set; } public int B { get; set; } }",
        "class SampleComponent { }",
        "class SampleEntity { }",
        "class PatternOnly : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() { return new PatternEnumerator(); } }",
        "class PatternEnumerator : System.Collections.IEnumerator { public object Current { get { return new FooNode(); } } public bool MoveNext() { return false; } public void Reset() { } }",
        "class FooNode { }",
        "",
        "class C",
        "{",
        "    void M()",
        "    {",
        "        const string szDescriptName = ""Description"";",
        "        double markerH = 20;",
        "        object boxed = ""x"";",
        "        int? pageSize = 0;",
        "        SampleComponent sampleComponent = null;",
        "        SampleEntity player;",
        "        player = new SampleEntity();",
        "        PropData keep = new PropData();",
        "        IFoo iface = new Foo();",
        "        XmlDocument doc = new XmlDocument();",
        "        XmlNodeList clsNodes = doc.ChildNodes;",
        "        InitTarget init = new InitTarget();",
        "        init.A = 1;",
        "        init.B = 2;",
        "        int[] values = new int[] { 1, 2, 3 };",
        "        object[] names = new string[] { ""A"", ""B"" };",
        "        object[] riskyNames = new string[] { null };",
        "        Delegate[] handlers = new Action[] { Target };",
        "        Delegate[] handlers2 = new Action[] { (Target) };",
        "        foreach (XmlNode node in clsNodes)",
        "        {",
        "            _ = node.Name;",
        "        }",
        "        PatternOnly pattern = new PatternOnly();",
        "        foreach (FooNode item in pattern)",
        "        {",
        "            _ = item;",
        "        }",
        "        int nIndex = 1;",
        "        int nCount = 3;",
        "        if (nIndex > 0 && nIndex <= nCount - 1)",
        "        {",
        "        }",
        "        Console.WriteLine(szDescriptName + markerH + pageSize + sampleComponent + player + keep + iface + init + values + names + riskyNames + handlers + handlers2);",
        "    }",
        "    void Target() { }",
        "}")
    $content = ($lines -join $nl) + $nl
    $bomEnc = New-Object System.Text.UTF8Encoding($true)   # emit BOM
    $target = Join-Path $srcDir "Sample.cs"
    [System.IO.File]::WriteAllText($target, $content, $bomEnc)

    $proj = Join-Path $srcDir "SyntaxFixture.csproj"
    $projText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>false</ImplicitUsings>
    <OutputType>Library</OutputType>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Sample.cs" />
  </ItemGroup>
</Project>
"@
    [System.IO.File]::WriteAllText($proj, $projText, (New-Object System.Text.UTF8Encoding($false)))

    # a generated file that MUST be skipped by name
    $gen = Join-Path $srcDir "Widget.Designer.cs"
    [System.IO.File]::WriteAllText($gen, $content, $bomEnc)

    # --dry-run must not modify anything
    $before = [System.IO.File]::ReadAllBytes($target)
    $dryExit = Invoke-Tool @($srcDir, "--rules", "all", "--dry-run")
    Check "dry-run exits 0" { $dryExit -eq 0 }
    $afterDry = [System.IO.File]::ReadAllBytes($target)
    Check "dry-run leaves file byte-identical" { @(Compare-Object $before $afterDry -SyncWindow 0).Count -eq 0 }

    # apply
    $applyExit = Invoke-Tool @($srcDir, "--rules", "all")
    Check "apply exits 0" { $applyExit -eq 0 }

    $bytes = [System.IO.File]::ReadAllBytes($target)
    Check "BOM preserved (EF BB BF)" { ($bytes.Length -ge 3) -and ($bytes[0] -eq 0xEF) -and ($bytes[1] -eq 0xBB) -and ($bytes[2] -eq 0xBF) }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)   # .NET strips the BOM when decoding
    Check "localconst applied" { $text.Contains("var szDescriptName = ""Description"";") }
    Check "obviousvar numeric cast applied" { $text.Contains("var markerH = (double)20;") }
    Check "obviousvar object literal narrowing skipped" { $text.Contains("object boxed = ""x"";") }
    Check "obviousvar nullable numeric cast applied" { $text.Contains("var pageSize = (int?)0;") }
    Check "nullvar initializer applied" { $text.Contains("var sampleComponent = (SampleComponent)null;") }
    Check "nullvar no-initializer applied" { $text.Contains("var player = (SampleEntity)null;") }
    Check "objectvar-safe applied" { $text.Contains("var keep = new PropData();") }
    Check "objectvar-narrowing applied" { $text.Contains("var iface = new Foo();") }
    Check "objectinitializer applied" { $text.Contains("var init = new InitTarget { A = 1, B = 2 };") }
    Check "arrayvar-safe applied" { $text.Contains("int[] values = { 1, 2, 3 };") }
    Check "arrayvar-narrowing applied" { $text.Contains("var names = new[] { ""A"", ""B"" };") }
    Check "arrayvar-narrowing null inference risk skipped" { $text.Contains("object[] riskyNames = new string[] { null };") }
    Check "arrayvar-narrowing method group target typing skipped" { $text.Contains("Delegate[] handlers = new Action[] { Target };") }
    Check "arrayvar-narrowing parenthesized method group target typing skipped" { $text.Contains("Delegate[] handlers2 = new Action[] { (Target) };") }
    Check "foreachcast applied" { $text.Contains("foreach (var node in System.Linq.Enumerable.Cast<XmlNode>(clsNodes))") }
    # Generalized beyond XmlNode: an explicit element type over ANY IEnumerable collection now converts
    # (formerly skipped). PatternOnly is IEnumerable, so Cast<FooNode>(pattern) compiles.
    Check "foreachcast explicit-type over IEnumerable converts" { $text.Contains("foreach (var item in System.Linq.Enumerable.Cast<FooNode>(pattern))") }
    Check "parens applied" { $text.Contains("(nIndex > 0) && (nIndex <= nCount - 1)") }

    $buildOut = (& $dotnet.Source build $proj -c Release -v q 2>&1 | Out-String)
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) { Write-Host $buildOut }
    Check "rewritten real-pattern test project builds" { $buildExit -eq 0 }

    $lfCount = @([regex]::Matches($text, "`n")).Count
    $crlfCount = @([regex]::Matches($text, "`r`n")).Count
    Check "CRLF preserved (every LF is part of a CRLF)" { $lfCount -eq $crlfCount }

    $genText = [System.IO.File]::ReadAllText($gen)
    Check "generated .Designer.cs skipped" { $genText.Contains("SampleComponent sampleComponent = null;") }

    # 3) idempotency: a second apply changes nothing (byte-identical)
    $firstBytes = [System.IO.File]::ReadAllBytes($target)
    $reExit = Invoke-Tool @($srcDir, "--rules", "all")
    Check "re-apply exits 0" { $reExit -eq 0 }
    $secondBytes = [System.IO.File]::ReadAllBytes($target)
    Check "idempotent (byte-identical second run)" { @(Compare-Object $firstBytes $secondBytes -SyncWindow 0).Count -eq 0 }

    # 4) --rules selection: nullvar-only leaves the && condition and object creation alone
    $selDir = Join-Path $work "sel"
    New-Item -ItemType Directory -Force -Path $selDir | Out-Null
    $selTarget = Join-Path $selDir "Sel.cs"
    [System.IO.File]::WriteAllText($selTarget, $content, $bomEnc)
    $null = Invoke-Tool @($selDir, "--rules", "nullvar")
    $selText = [System.IO.File]::ReadAllText($selTarget)
    Check "--rules nullvar applies nullvar" { $selText.Contains("var sampleComponent = (SampleComponent)null;") }
    Check "--rules nullvar leaves object creation alone" { $selText.Contains("PropData keep = new PropData();") }
    Check "--rules nullvar leaves foreach alone" { $selText.Contains("foreach (XmlNode node in clsNodes)") }
    Check "--rules nullvar leaves parens alone" { $selText.Contains("if (nIndex > 0 && nIndex <= nCount - 1)") }

    # 5) one-call runner parses cleanly
    $runner = Join-Path $PSScriptRoot "..\tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1"
    Check "Run-SparrowSyntaxFix.ps1 exists" { Test-Path -LiteralPath $runner }
    Check "Run-SparrowSyntaxFix.ps1 parses (no syntax error)" {
        $perr = $null
        [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $runner).Path, [ref]$null, [ref]$perr) | Out-Null
        (-not $perr) -or ($perr.Count -eq 0)
    }

    # 6) OPT-IN new rules (forvar / fieldsplit / emptystmt): each applied by name on a dedicated on-disk file,
    #    with before->after, idempotency, and the key guards. These rules are NOT in the default set.
    $newDir = Join-Path $work "newrules"
    New-Item -ItemType Directory -Force -Path $newDir | Out-Null
    $newLines = @(
        "class C",
        "{",
        "    private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;",
        "    private const int KA = 1, KB = 2;",
        "    void M(int count, System.Collections.Generic.List<int> q)",
        "    {",
        "        for (int i = 0; i < count; i++) { }",
        "        for (int a = 0, b = q.Count; a < b; a++) { }",
        "        for (int j = GetX(); j < count; j++) { }",
        "        System.EventHandler h = null; h += OnE; ;",
        "        for (;;) { break; }",
        "        int local1 = 0, local2 = 0;",
        "        System.Console.WriteLine(local1 + local2 + KA + KB);",
        "    }",
        "    int GetX() { return 0; }",
        "    void OnE(object s, System.EventArgs e) { }",
        "}")
    $newTarget = Join-Path $newDir "NewRules.cs"
    [System.IO.File]::WriteAllText($newTarget, (($newLines -join $nl) + $nl), $bomEnc)

    # forvar
    Check "forvar: exit 0" { (Invoke-Tool @($newTarget, "--rules", "forvar")) -eq 0 }
    $nrv = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($newTarget))
    Check "forvar: single-declarator int for-init -> var" { $nrv.Contains("for (var i = 0; i < count; i++)") }
    Check "forvar: multi-declarator for-init untouched (CS0819 guard)" { $nrv.Contains("for (int a = 0, b = q.Count; a < b; a++)") }
    Check "forvar: method-call for-init untouched" { $nrv.Contains("for (int j = GetX(); j < count; j++)") }
    Check "forvar: does NOT split fields (opt-in isolation)" { $nrv.Contains("private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;") }

    # fieldsplit
    Check "fieldsplit: exit 0" { (Invoke-Tool @($newTarget, "--rules", "fieldsplit")) -eq 0 }
    $nrf = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($newTarget))
    Check "fieldsplit: 4-way field split same indent" { $nrf.Contains("    private double _rawXMin;`r`n    private double _rawXMax;`r`n    private double _rawYMin;`r`n    private double _rawYMax;") -or $nrf.Contains("    private double _rawXMin;`n    private double _rawXMax;`n    private double _rawYMin;`n    private double _rawYMax;") }
    Check "fieldsplit: const field untouched" { $nrf.Contains("private const int KA = 1, KB = 2;") }
    Check "fieldsplit: local multi-declarator untouched (fields only)" { $nrf.Contains("int local1 = 0, local2 = 0;") }

    # emptystmt
    Check "emptystmt: exit 0" { (Invoke-Tool @($newTarget, "--rules", "emptystmt")) -eq 0 }
    $nre = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($newTarget))
    Check "emptystmt: double-semicolon collapsed to one" { $nre.Contains("h += OnE;`r`n") -or $nre.Contains("h += OnE;`n") }
    Check "emptystmt: no stray double-semicolon remains" { -not $nre.Contains("OnE; ;") }
    Check "emptystmt: for(;;) empty clauses untouched" { $nre.Contains("for (;;) { break; }") }

    # idempotency: applying all three opt-in rules a second time changes nothing.
    $newOnce = [System.IO.File]::ReadAllBytes($newTarget)
    Check "new rules: second forvar,fieldsplit,emptystmt exit 0" { (Invoke-Tool @($newTarget, "--rules", "forvar,fieldsplit,emptystmt")) -eq 0 }
    $newTwice = [System.IO.File]::ReadAllBytes($newTarget)
    Check "new rules: byte-identical second run (idempotent)" { @(Compare-Object $newOnce $newTwice -SyncWindow 0).Count -eq 0 }

    # rewritten file with all three opt-in rules applied still compiles.
    $newProj = Join-Path $newDir "NewRules.csproj"
    [System.IO.File]::WriteAllText($newProj, $projText.Replace("Sample.cs", "NewRules.cs"), (New-Object System.Text.UTF8Encoding($false)))
    $newBuildOut = (& $dotnet.Source build $newProj -c Release -v q 2>&1 | Out-String)
    $newBuildExit = $LASTEXITCODE
    if ($newBuildExit -ne 0) { Write-Host $newBuildOut }
    Check "new rules: rewritten project builds" { $newBuildExit -eq 0 }

    # default rule set must NOT include the opt-in rules (isolation): default run leaves all three patterns intact.
    $defDir = Join-Path $work "defaults"
    New-Item -ItemType Directory -Force -Path $defDir | Out-Null
    $defTarget = Join-Path $defDir "Def.cs"
    [System.IO.File]::WriteAllText($defTarget, (($newLines -join $nl) + $nl), $bomEnc)
    $null = Invoke-Tool @($defTarget)   # no --rules => default subset
    $defText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($defTarget))
    Check "default set: forvar NOT applied" { $defText.Contains("for (int i = 0; i < count; i++)") }
    Check "default set: fieldsplit NOT applied" { $defText.Contains("private double _rawXMin, _rawXMax, _rawYMin, _rawYMax;") }
    Check "default set: emptystmt NOT applied" { $defText.Contains("h += OnE; ;") }

    # 7) COMPILE GATE (Run-SparrowSyntaxFix.ps1 -VerifyCmd): in a throwaway git repo, a rule whose gate command
    #    FAILS must have its edits reverted (working tree clean, no new commit); a rule whose gate PASSES must be
    #    committed. Uses -ExePath to the freshly built dll so the STALE publish\ exe is not preferred.
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) {
        Write-Host "  (git not found; skipping compile-gate test)"
    }
    else {
        $enc = New-Object System.Text.UTF8Encoding($false)
        $freshDll = Join-Path $toolDir "bin\Release\net8.0\SparrowSyntaxFix.dll"
        Check "compile-gate: fresh tool dll exists" { Test-Path -LiteralPath $freshDll }

        $gateRepo = Join-Path $work "gate"
        New-Item -ItemType Directory -Force -Path $gateRepo | Out-Null
        $gateCs = Join-Path $gateRepo "Gate.cs"
        $gateSrc = ("class C" + $nl + "{" + $nl + "    bool M(bool a, bool b)" + $nl + "    {" + $nl + "        bool ok = a && b;" + $nl + "        return ok;" + $nl + "    }" + $nl + "}" + $nl)
        [System.IO.File]::WriteAllText($gateCs, $gateSrc, $enc)

        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & $git.Source -C $gateRepo init -q 2>&1 | Out-Null
            & $git.Source -C $gateRepo config user.email "t@example.com" 2>&1 | Out-Null
            & $git.Source -C $gateRepo config user.name "Test" 2>&1 | Out-Null
            & $git.Source -C $gateRepo config commit.gpgsign false 2>&1 | Out-Null
            & $git.Source -C $gateRepo add -A 2>&1 | Out-Null
            & $git.Source -C $gateRepo commit -q -m "init" 2>&1 | Out-Null

            # (a) gate FAILS -> edits reverted, no new commit.
            & $runner -Solution $gateRepo -Rules parens -Commit -ExePath $freshDll -LogDir $work -VerifyCmd 'powershell -NoProfile -Command "exit 1"' *>&1 | Out-Null
            $statusFail = ((& $git.Source -C $gateRepo status --porcelain) | Out-String).Trim()
            $countFail = ((& $git.Source -C $gateRepo rev-list --count HEAD) | Out-String).Trim()
            $textFail = [System.IO.File]::ReadAllText($gateCs)
            Check "compile-gate FAIL: working tree clean (rule edits reverted)" { $statusFail -eq "" }
            Check "compile-gate FAIL: no new commit (still 1)" { $countFail -eq "1" }
            Check "compile-gate FAIL: file content unchanged (parens NOT applied)" { $textFail.Contains("bool ok = a && b;") }

            # (b) gate PASSES -> commit created, edit applied.
            & $runner -Solution $gateRepo -Rules parens -Commit -ExePath $freshDll -LogDir $work -VerifyCmd 'powershell -NoProfile -Command "exit 0"' *>&1 | Out-Null
            $countPass = ((& $git.Source -C $gateRepo rev-list --count HEAD) | Out-String).Trim()
            $textPass = [System.IO.File]::ReadAllText($gateCs)
            Check "compile-gate PASS: new commit created (now 2)" { $countPass -eq "2" }
            Check "compile-gate PASS: parens applied" { $textPass.Contains("(a) && (b)") }
        }
        finally { $ErrorActionPreference = $prevEap }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("SparrowSyntaxFix E2E failed:`n  " + ($failures -join "`n  ")) }
Write-Host "SparrowSyntaxFix E2E passed."

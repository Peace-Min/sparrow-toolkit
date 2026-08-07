#requires -Version 5.1
<#
    Opt-in end-to-end smoke test for SparrowCommentFix. NOT run by the default validate gate (needs the
    .NET SDK + a Roslyn restore -- env/time heavy). Run it manually, or via `validate.ps1 -IncludeCommentE2E`.

    It builds the tool, writes synthetic .cs fixtures to a temp dir, runs the tool per rule, and asserts the
    ACTIVE rules (comment + layout) before/after, the string-literal SAFETY guarantee (`//` inside a string is
    never touched), idempotency, --dry-run (writes nothing), --files-from CSV parsing, and that the two
    NOT-active rules (blankline / asterisk) and any unknown key all exit 2. Skips cleanly (not
    fails) when the .NET SDK is missing.

    `--rules all` means all active comment + layout rules; runner defaults are narrower.

    PS 5.1 notes honored here: collections wrapped in @() before .Count; no &&/ternary/null-coalescing;
    fixtures/results read with -Encoding UTF8 (the TOOL writes UTF-8 via .NET, not via PowerShell);
    before/after assertions use .Contains (ordinal/case-sensitive; -match is case-insensitive in PS).
    This script carries Korean literals -> it MUST stay UTF-8 WITH BOM so PS 5.1 parses it correctly.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping SparrowCommentFix E2E."; return }

$toolDir = Join-Path $RepositoryRoot "tools\_internal\SparrowCommentFix"
$toolProj = Join-Path $toolDir "SparrowCommentFix.csproj"
if (-not (Test-Path -LiteralPath $toolProj)) { throw "missing project: $toolProj" }

$work = Join-Path $env:TEMP ("commentfix-e2e-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Write-Host "  [ok]   $name" } else { $script:failures += $name } }
    catch { $script:failures += "$name ($($_.Exception.Message))" }
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

function New-Fixture {
    param([string]$Name, [string]$Content, [System.Text.Encoding]$Encoding = $utf8NoBom)
    $path = Join-Path $work $Name
    [System.IO.File]::WriteAllText($path, $Content, $Encoding)
    return $path
}

function Read-Text {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path, $utf8NoBom)
}

function Invoke-Tool {
    param([string[]]$ToolArgs)
    # EAP=Continue locally: a run that fails on purpose (exit 2) writes to stderr, and native stderr under
    # EAP=Stop with 2>&1 becomes a terminating error (see docs/architecture.md). We want the exit code, not a throw.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $dotnet.Source run --project $toolProj -c Release --no-build -- @ToolArgs 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $prev }
    return $LASTEXITCODE
}

function Test-BytesEqual {
    param([byte[]]$A, [byte[]]$B)
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}

try {
    Write-Host "  building SparrowCommentFix (Release)..."
    & $dotnet.Source build $toolProj -c Release -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SparrowCommentFix build failed" }

    # --- rule: flatten (6+7+8: block/Doxygen comment -> line comments, capitalized, periodized) ---
    $flattenSrc = @'
class C {
    void M() {
        /**.
         * @brief delta event marker 반환
         *
         * @param annot annotation 객체
         * @returns delta event marker 객체
         */
        int a = 0;
    }
}
'@
    $flattenFile = New-Fixture "flatten.cs" $flattenSrc
    Check "flatten: exit 0" { (Invoke-Tool @($flattenFile, "--rules", "flatten")) -eq 0 }
    $fl = Read-Text $flattenFile
    Check "flatten: @brief line normalized" { $fl.Contains("// Delta event marker 반환.") }
    Check "flatten: @param line normalized" { $fl.Contains("// Param annot annotation 객체.") }
    Check "flatten: @returns line normalized" { $fl.Contains("// Returns delta event marker 객체.") }
    Check "flatten: block delimiters removed" { (-not $fl.Contains("/**")) -and (-not $fl.Contains("*/")) -and (-not $fl.Contains(" * @")) }
    Check "flatten: no punctuation-only comment emitted" { (-not $fl.Contains("// .")) }

    # --- rule: trailing (9: move inline comment above code and normalize comment style) ---
    $trailingSrc = @'
class C {
    void M() {
        int count = 0; //ABC
        string url = "http://example.com"; //url
    }
}
'@
    $trailingFile = New-Fixture "trailing.cs" $trailingSrc
    Check "trailing: exit 0" { (Invoke-Tool @($trailingFile, "--rules", "trailing")) -eq 0 }
    $tr = Read-Text $trailingFile
    Check "trailing: comment moved above first code line" { $tr.Contains("        // ABC.`r`n        int count = 0;") -or $tr.Contains("        // ABC.`n        int count = 0;") }
    Check "trailing: string literal URL intact and comment moved" { $tr.Contains('"http://example.com"') -and ($tr.Contains("// Url.`r`n        string url") -or $tr.Contains("// Url.`n        string url")) }

    # flatten negative: block comments embedded in code are not standalone comment lines and must not be changed.
    $flatNegSrc = @'
class C {
    void M() {
        int x = /* note */ 1;
        Foo(/* note */ x);
    }
}
'@
    $flatNegFile = New-Fixture "flatten_negative.cs" $flatNegSrc
    $flatNegBefore = [System.IO.File]::ReadAllBytes($flatNegFile)
    Check "flatten negative: exit 0" { (Invoke-Tool @($flatNegFile, "--rules", "flatten")) -eq 0 }
    $flatNegAfter = [System.IO.File]::ReadAllBytes($flatNegFile)
    Check "flatten negative: embedded block comments byte-identical" { Test-BytesEqual $flatNegBefore $flatNegAfter }

    # trailing negative: closing-brace comments and suppression comments stay in place.
    $trailNegSrc = @'
class C {
    void M() {
        if (true) { } // namespace-ish
        int x = 0; // NOSONAR
    }
}
'@
    $trailNegFile = New-Fixture "trailing_negative.cs" $trailNegSrc
    $trailNegBefore = [System.IO.File]::ReadAllBytes($trailNegFile)
    Check "trailing negative: exit 0" { (Invoke-Tool @($trailNegFile, "--rules", "trailing")) -eq 0 }
    $trailNegAfter = [System.IO.File]::ReadAllBytes($trailNegFile)
    Check "trailing negative: unsafe comments byte-identical" { Test-BytesEqual $trailNegBefore $trailNegAfter }

    $protectedSrc = @'
class C {
    void M() {
        /**
         * @code
         * int x = 0;
         * @endcode
         */
        int x = 0; //NOSONAR
        /** See @ref Foo */
        /** \code */
    }
}
'@
    $protectedFile = New-Fixture "protected_all.cs" $protectedSrc
    $protectedBefore = [System.IO.File]::ReadAllBytes($protectedFile)
    Check "protected comments: --rules all exit 0" { (Invoke-Tool @($protectedFile, "--rules", "all")) -eq 0 }
    $protectedAfter = [System.IO.File]::ReadAllBytes($protectedFile)
    Check "protected comments: --rules all byte-identical" { Test-BytesEqual $protectedBefore $protectedAfter }

    $tagBoundarySrc = @'
class C {
    void M() {
        /** @briefly not a real brief */
        /** @parameter not a real param */
    }
}
'@
    $tagBoundaryFile = New-Fixture "tag_boundary.cs" $tagBoundarySrc
    $tagBoundaryBefore = [System.IO.File]::ReadAllBytes($tagBoundaryFile)
    Check "tag boundary: exit 0" { (Invoke-Tool @($tagBoundaryFile, "--rules", "all")) -eq 0 }
    $tagBoundaryAfter = [System.IO.File]::ReadAllBytes($tagBoundaryFile)
    Check "tag boundary: unsupported prefixes byte-identical" { Test-BytesEqual $tagBoundaryBefore $tagBoundaryAfter }

    $lineDoxygenSrc = @'
class C {
    void M() {
        /// @unknown custom command
        /// See @unknown custom command
        // \brief native doxygen command
        int x = 0; // \brief trailing command
    }
}
'@
    $lineDoxygenFile = New-Fixture "line_doxygen.cs" $lineDoxygenSrc
    $lineDoxygenBefore = [System.IO.File]::ReadAllBytes($lineDoxygenFile)
    Check "line-form Doxygen protection: exit 0" { (Invoke-Tool @($lineDoxygenFile, "--rules", "all")) -eq 0 }
    $lineDoxygenAfter = [System.IO.File]::ReadAllBytes($lineDoxygenFile)
    Check "line-form Doxygen protection: byte-identical" { Test-BytesEqual $lineDoxygenBefore $lineDoxygenAfter }

    $parseErrorSrc = @'
class C {
    void M( {
        //bad
    }
}
'@
    $parseErrorFile = New-Fixture "parse_error.cs" $parseErrorSrc
    $parseErrorBefore = [System.IO.File]::ReadAllBytes($parseErrorFile)
    Check "parse error: --rules all exit 0" { (Invoke-Tool @($parseErrorFile, "--rules", "all")) -eq 0 }
    $parseErrorAfter = [System.IO.File]::ReadAllBytes($parseErrorFile)
    Check "parse error: byte-identical skip" { Test-BytesEqual $parseErrorBefore $parseErrorAfter }

    # --- layout: memberblank ---
    $memberSrc = @'
interface IReport {
    string PrepareData(string name);
    bool SetWriteCSVFile(string path);
}
'@
    $memberFile = New-Fixture "memberblank.cs" $memberSrc
    Check "memberblank: exit 0" { (Invoke-Tool @($memberFile, "--rules", "memberblank")) -eq 0 }
    $mb = Read-Text $memberFile
    Check "memberblank: interface methods separated" { $mb.Contains("    string PrepareData(string name);`r`n`r`n    bool SetWriteCSVFile") -or $mb.Contains("    string PrepareData(string name);`n`n    bool SetWriteCSVFile") }
    $memberOnce = [System.IO.File]::ReadAllBytes($memberFile)
    Check "memberblank: second run exit 0" { (Invoke-Tool @($memberFile, "--rules", "memberblank")) -eq 0 }
    $memberTwice = [System.IO.File]::ReadAllBytes($memberFile)
    Check "memberblank: second run byte-identical" { Test-BytesEqual $memberOnce $memberTwice }

    $compactMemberSrc = @'
class C { void A() { } void B() { } }
'@
    $compactMemberFile = New-Fixture "memberblank_compact.cs" $compactMemberSrc
    $compactMemberBefore = [System.IO.File]::ReadAllBytes($compactMemberFile)
    Check "memberblank compact: first run exit 0" { (Invoke-Tool @($compactMemberFile, "--rules", "memberblank")) -eq 0 }
    $compactMemberAfter1 = [System.IO.File]::ReadAllBytes($compactMemberFile)
    Check "memberblank compact: first run byte-identical" { Test-BytesEqual $compactMemberBefore $compactMemberAfter1 }
    Check "memberblank compact: second run exit 0" { (Invoke-Tool @($compactMemberFile, "--rules", "memberblank")) -eq 0 }
    $compactMemberAfter2 = [System.IO.File]::ReadAllBytes($compactMemberFile)
    Check "memberblank compact: second run byte-identical" { Test-BytesEqual $compactMemberAfter1 $compactMemberAfter2 }

    # --- memberblank: comment-before-member + several consecutive members (real interface pattern) ---
    # A leading comment BELONGS to the member below it, so the blank goes BEFORE the comment. Every pair of
    # consecutive members after the first must be separated -> here 3 blanks (before members 2, 3, 4).
    $memberCommentSrc = @'
public interface I {
  uint PreProcessDataCount(int[] a);
  // 20210407 userB CA1026.
  DataTable PreProcessData(int[] a, uint b, uint c);
  DataTable PreProcessAddVariableInfo();
  DataTable DataFilterInfoList();
}
'@
    $memberCommentFile = New-Fixture "memberblank_comment.cs" $memberCommentSrc
    Check "memberblank comment: exit 0" { (Invoke-Tool @($memberCommentFile, "--rules", "memberblank")) -eq 0 }
    $mbc = Read-Text $memberCommentFile
    # blank inserted BEFORE the leading comment (comment stays glued to its member below it)
    Check "memberblank comment: blank before comment block (not between comment and member)" { $mbc.Contains("PreProcessDataCount(int[] a);`r`n`r`n  // 20210407 userB CA1026.`r`n  DataTable PreProcessData") -or $mbc.Contains("PreProcessDataCount(int[] a);`n`n  // 20210407 userB CA1026.`n  DataTable PreProcessData") }
    Check "memberblank comment: blank before 3rd member" { $mbc.Contains("uint b, uint c);`r`n`r`n  DataTable PreProcessAddVariableInfo") -or $mbc.Contains("uint b, uint c);`n`n  DataTable PreProcessAddVariableInfo") }
    Check "memberblank comment: blank before 4th member" { $mbc.Contains("PreProcessAddVariableInfo();`r`n`r`n  DataTable DataFilterInfoList") -or $mbc.Contains("PreProcessAddVariableInfo();`n`n  DataTable DataFilterInfoList") }
    Check "memberblank comment: no blank before FIRST member (right after {)" { $mbc.Contains("public interface I {`r`n  uint PreProcessDataCount") -or $mbc.Contains("public interface I {`n  uint PreProcessDataCount") }
    Check "memberblank comment: no double blanks" { (-not $mbc.Contains("`r`n`r`n`r`n")) -and (-not $mbc.Contains("`n`n`n")) }
    # idempotency: apply 3 MORE times -> byte-identical after the first application.
    $mbc1 = [System.IO.File]::ReadAllBytes($memberCommentFile)
    Invoke-Tool @($memberCommentFile, "--rules", "memberblank") | Out-Null
    Invoke-Tool @($memberCommentFile, "--rules", "memberblank") | Out-Null
    Invoke-Tool @($memberCommentFile, "--rules", "memberblank") | Out-Null
    $mbc4 = [System.IO.File]::ReadAllBytes($memberCommentFile)
    Check "memberblank comment: byte-identical after 3 more runs (idempotent)" { Test-BytesEqual $mbc1 $mbc4 }

    # --- layout: onedeclaration ---
    $declSrc = @'
class C {
    public double X, Y, Z;
    void M() {
        string objectName, subPath;
        double initialX_Min = 0, initialX_Max = 0;
    }
}
'@
    $declFile = New-Fixture "onedeclaration.cs" $declSrc
    Check "onedeclaration: exit 0" { (Invoke-Tool @($declFile, "--rules", "onedeclaration")) -eq 0 }
    $dc = Read-Text $declFile
    Check "onedeclaration: fields split" { $dc.Contains("    public double X;`r`n    public double Y;`r`n    public double Z;") -or $dc.Contains("    public double X;`n    public double Y;`n    public double Z;") }
    Check "onedeclaration: locals split" { $dc.Contains("        string objectName;`r`n        string subPath;") -or $dc.Contains("        string objectName;`n        string subPath;") }
    Check "onedeclaration: initialized locals split" { $dc.Contains("        double initialX_Min = 0;`r`n        double initialX_Max = 0;") -or $dc.Contains("        double initialX_Min = 0;`n        double initialX_Max = 0;") }

    # --- layout: onestatement ---
    $stmtSrc = @'
class C {
    void M() {
        X = x; Y = y;
        if (ok) { A = false; B = false; }
    }
}
'@
    $stmtFile = New-Fixture "onestatement.cs" $stmtSrc
    Check "onestatement: exit 0" { (Invoke-Tool @($stmtFile, "--rules", "onestatement")) -eq 0 }
    $st = Read-Text $stmtFile
    Check "onestatement: adjacent assignments split" { $st.Contains("        X = x;`r`n        Y = y;") -or $st.Contains("        X = x;`n        Y = y;") }
    Check "onestatement: single-line if block expanded" { $st.Contains("if (ok)`r`n        {") -or $st.Contains("if (ok)`n        {") -or $st.Contains("if (ok) {`r`n            A = false;") -or $st.Contains("if (ok) {`n            A = false;") }

    $nestedStmtSrc = @'
class C {
    void M() {
        if (a) { if (b) { A(); B(); } C(); }
    }
}
'@
    $nestedStmtFile = New-Fixture "onestatement_nested.cs" $nestedStmtSrc
    Check "onestatement nested: exit 0 without overlapping edits" { (Invoke-Tool @($nestedStmtFile, "--rules", "onestatement")) -eq 0 }
    $nestedStmtText = Read-Text $nestedStmtFile
    Check "onestatement nested: file remains parseable text" { $nestedStmtText.Contains("if (a)") -and $nestedStmtText.Contains("if (b)") }

    # --- layout: continuation ---
    $contSrc = @'
class C {
    void M() {
        InfoXML = new XElement("playerInfo",
        new XAttribute("symbolICOPS", value),
        new XAttribute("movepath_opt_1", value));
        if ((A) &&
           (B))
        {
        }
    }
}
'@
    $contFile = New-Fixture "continuation.cs" $contSrc
    Check "continuation: exit 0" { (Invoke-Tool @($contFile, "--rules", "continuation")) -eq 0 }
    $ct = Read-Text $contFile
    Check "continuation: arguments indented one level" { $ct.Contains("        InfoXML = new XElement(`"playerInfo`",`r`n            new XAttribute") -or $ct.Contains("        InfoXML = new XElement(`"playerInfo`",`n            new XAttribute") }
    Check "continuation: binary right operand indented one level" { $ct.Contains("        if ((A) &&`r`n            (B))") -or $ct.Contains("        if ((A) &&`n            (B))") }

    # over-indented ARGUMENT continuation whose indent (16) is neither opening+4 (12) nor aligned to the open
    # `(` (col 37) -> arbitrary deep indent -> normalized DOWN to opening+4. (Delimiter-aligned deep args are
    # instead protected by the guard; see the dedicated aligned-negative below.)
    $contOverSrc = @'
class C {
    void M() {
        InfoXML = new XElement("playerInfo",
                new XAttribute("symbolICOPS", value));
    }
}
'@
    $contOverFile = New-Fixture "continuation_overindented.cs" $contOverSrc
    Check "continuation overindented: exit 0" { (Invoke-Tool @($contOverFile, "--rules", "continuation")) -eq 0 }
    $contOver = Read-Text $contOverFile
    Check "continuation overindented: arbitrary deep arg pulled DOWN to opening+4 (12)" { $contOver.Contains("        InfoXML = new XElement(`"playerInfo`",`r`n            new XAttribute") -or $contOver.Contains("        InfoXML = new XElement(`"playerInfo`",`n            new XAttribute") }
    $contOver1 = [System.IO.File]::ReadAllBytes($contOverFile)
    Invoke-Tool @($contOverFile, "--rules", "continuation") | Out-Null
    $contOver2 = [System.IO.File]::ReadAllBytes($contOverFile)
    Check "continuation overindented: idempotent (2nd run byte-identical)" { Test-BytesEqual $contOver1 $contOver2 }

    # --- continuation: OPERATOR-LED style (dominant in MyApp): the continuation line begins with `&&`/`||`,
    #     not the operand. The indent fix must target the operator token and set it to if-indent + 4. ---
    $contOpLedSrc = @'
public class C {
    public void M(int a) {
        if (20 > 10
        && 30 > 10)
        { System.Console.WriteLine("x"); }
        if (a > 0
        || a < 5)
        { }
    }
}
'@
    $contOpLedFile = New-Fixture "continuation_opled.cs" $contOpLedSrc
    Check "continuation op-led: exit 0" { (Invoke-Tool @($contOpLedFile, "--rules", "continuation")) -eq 0 }
    $col = Read-Text $contOpLedFile
    Check "continuation op-led: && line indented to if-indent+4" { $col.Contains("        if (20 > 10`r`n            && 30 > 10)") -or $col.Contains("        if (20 > 10`n            && 30 > 10)") }
    Check "continuation op-led: || line indented to if-indent+4" { $col.Contains("        if (a > 0`r`n            || a < 5)") -or $col.Contains("        if (a > 0`n            || a < 5)") }
    # idempotency: a second run makes zero further changes (byte-identical).
    $contOpLed1 = [System.IO.File]::ReadAllBytes($contOpLedFile)
    Check "continuation op-led: second run exit 0" { (Invoke-Tool @($contOpLedFile, "--rules", "continuation")) -eq 0 }
    $contOpLed2 = [System.IO.File]::ReadAllBytes($contOpLedFile)
    Check "continuation op-led: second run byte-identical (idempotent)" { Test-BytesEqual $contOpLed1 $contOpLed2 }

    # already-correct op-led continuation: `&&` already at if-indent+4 -> no change, no churn.
    $contOkSrc = @'
public class C {
    public void M() {
        if (20 > 10
            && 30 > 10)
        { }
    }
}
'@
    $contOkFile = New-Fixture "continuation_opled_ok.cs" $contOkSrc
    $contOkBefore = [System.IO.File]::ReadAllBytes($contOkFile)
    Check "continuation op-led already-correct: exit 0" { (Invoke-Tool @($contOkFile, "--rules", "continuation")) -eq 0 }
    $contOkAfter = [System.IO.File]::ReadAllBytes($contOkFile)
    Check "continuation op-led already-correct: byte-identical no churn" { Test-BytesEqual $contOkBefore $contOkAfter }

    # --- continuation: ALL binary operators (arithmetic / string-concat / bitwise / shift), not just &&/|| ---
    # Real MyApp continuations led by +, |, << etc. must re-indent to statement-indent + 4, same as &&/||.
    $contOpsSrc = @'
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
    $contOpsFile = New-Fixture "continuation_ops.cs" $contOpsSrc
    Check "continuation ops: exit 0" { (Invoke-Tool @($contOpsFile, "--rules", "continuation")) -eq 0 }
    $cops = Read-Text $contOpsFile
    Check "continuation ops: string-concat '+' continuation indented to stmt+4" { $cops.Contains("        sql = `$`"SELECT {a} `" +`r`n            `$`"FROM {b} `";") -or $cops.Contains("        sql = `$`"SELECT {a} `" +`n            `$`"FROM {b} `";") }
    Check "continuation ops: bitwise '|' continuation indented to stmt+4" { $cops.Contains("        x = (color.A << 24) |`r`n            (color.R << 16);") -or $cops.Contains("        x = (color.A << 24) |`n            (color.R << 16);") }
    Check "continuation ops: arithmetic '+' continuation indented to stmt+4" { $cops.Contains("right.R) +`r`n            System.Math.Abs(left.G") -or $cops.Contains("right.R) +`n            System.Math.Abs(left.G") }
    # idempotency: apply 3 MORE times -> byte-identical after the first application.
    $cops1 = [System.IO.File]::ReadAllBytes($contOpsFile)
    Invoke-Tool @($contOpsFile, "--rules", "continuation") | Out-Null
    Invoke-Tool @($contOpsFile, "--rules", "continuation") | Out-Null
    Invoke-Tool @($contOpsFile, "--rules", "continuation") | Out-Null
    $cops4 = [System.IO.File]::ReadAllBytes($contOpsFile)
    Check "continuation ops: byte-identical after 3 more runs (idempotent)" { Test-BytesEqual $cops1 $cops4 }

    # conservative: an already-correct operator continuation (already at stmt+4) must not churn.
    $contOpsOkSrc = @'
class C {
    void M() {
        d = a +
            b;
    }
}
'@
    $contOpsOkFile = New-Fixture "continuation_ops_ok.cs" $contOpsOkSrc
    $contOpsOkBefore = [System.IO.File]::ReadAllBytes($contOpsOkFile)
    Check "continuation ops already-correct: exit 0" { (Invoke-Tool @($contOpsOkFile, "--rules", "continuation")) -eq 0 }
    $contOpsOkAfter = [System.IO.File]::ReadAllBytes($contOpsOkFile)
    Check "continuation ops already-correct: byte-identical no churn" { Test-BytesEqual $contOpsOkBefore $contOpsOkAfter }

    # over-indented OPERATOR continuation with NO enclosing delimiter (all parens balanced on the opening line):
    # its deep indent (20) can align to nothing -> arbitrary -> normalized DOWN to opening+4 (12).
    $contOpsOverSrc = @'
class C {
    void M() {
        d = a +
                    b;
    }
}
'@
    $contOpsOverFile = New-Fixture "continuation_ops_over.cs" $contOpsOverSrc
    Check "continuation ops over-indented: exit 0" { (Invoke-Tool @($contOpsOverFile, "--rules", "continuation")) -eq 0 }
    $contOpsOver = Read-Text $contOpsOverFile
    Check "continuation ops over-indented: arbitrary deep operand pulled DOWN to opening+4 (12)" { $contOpsOver.Contains("        d = a +`r`n            b;") -or $contOpsOver.Contains("        d = a +`n            b;") }
    $contOpsOver1 = [System.IO.File]::ReadAllBytes($contOpsOverFile)
    Invoke-Tool @($contOpsOverFile, "--rules", "continuation") | Out-Null
    $contOpsOver2 = [System.IO.File]::ReadAllBytes($contOpsOverFile)
    Check "continuation ops over-indented: idempotent (2nd run byte-identical)" { Test-BytesEqual $contOpsOver1 $contOpsOver2 }

    # --- continuation: METHOD-CHAIN + INITIALIZER extension (per FORMATTING.CONTINUATION_LINE.BAD_INDENTATION) ---
    # A fluent `.Method()` line at the receiver's indent, and an initializer `{`/elements/`}` at or below the
    # `new` line, are pulled to opening-indent + 4. Deeper lines are left alone (conservative).
    $contChainSrc = @'
class C {
    void M() {
        var frames = _seriesIndexByKey
        .OrderBy(kv => kv.Value)
        .ToList();
        _line.AddPoints(new[]
        {
        new Pt(1),
        new Pt(2)
        });
    }
    object _seriesIndexByKey; object _line;
}
'@
    $contChainFile = New-Fixture "continuation_chain.cs" $contChainSrc
    Check "continuation chain: exit 0" { (Invoke-Tool @($contChainFile, "--rules", "continuation")) -eq 0 }
    $cch = Read-Text $contChainFile
    Check "continuation chain: .OrderBy pulled to receiver-indent+4" { $cch.Contains("        var frames = _seriesIndexByKey`r`n            .OrderBy(kv => kv.Value)") -or $cch.Contains("        var frames = _seriesIndexByKey`n            .OrderBy(kv => kv.Value)") }
    Check "continuation chain: .ToList pulled to receiver-indent+4" { $cch.Contains("            .ToList();") }
    Check "continuation chain: initializer brace+elements pulled to new-indent+4" { $cch.Contains("        _line.AddPoints(new[]`r`n            {`r`n            new Pt(1),`r`n            new Pt(2)`r`n            });") -or $cch.Contains("        _line.AddPoints(new[]`n            {`n            new Pt(1),`n            new Pt(2)`n            });") }
    # idempotency: apply 3 MORE times -> byte-identical after the first application.
    $cch1 = [System.IO.File]::ReadAllBytes($contChainFile)
    Invoke-Tool @($contChainFile, "--rules", "continuation") | Out-Null
    Invoke-Tool @($contChainFile, "--rules", "continuation") | Out-Null
    Invoke-Tool @($contChainFile, "--rules", "continuation") | Out-Null
    $cch4 = [System.IO.File]::ReadAllBytes($contChainFile)
    Check "continuation chain: byte-identical after 3 more runs (idempotent)" { Test-BytesEqual $cch1 $cch4 }

    # initializer whose brace is SHALLOWER than a deeply-indented opening line -> shift to opening-indent + 4.
    $contInitSrc = @'
class C {
    void Outer() {
        if (true) {
            if (true) {
                var dummy = new System.Collections.Generic.List<int>()
            {
                1
            };
            }
        }
    }
}
'@
    $contInitFile = New-Fixture "continuation_init_shallow.cs" $contInitSrc
    Check "continuation init-shallow: exit 0" { (Invoke-Tool @($contInitFile, "--rules", "continuation")) -eq 0 }
    $cin = Read-Text $contInitFile
    Check "continuation init-shallow: shallow brace shifted to opening-indent+4 (20)" { $cin.Contains("                var dummy = new System.Collections.Generic.List<int>()`r`n                    {") -or $cin.Contains("                var dummy = new System.Collections.Generic.List<int>()`n                    {") }

    # GUARD (binary category): a continuation line deeper than opening+4 that is DELIMITER-ALIGNED -- here `* 2.0`
    # sits at col 16, the column immediately after the still-open `return (` paren -> intentional alignment Sparrow
    # accepts -> left byte-identical (NOT de-indented to opening+4). Contrast the arbitrary-deep cases above.
    $contDeepSrc = @'
class C {
    double Geo(double a) {
        return (System.Math.Cos(a)
                * 2.0);
    }
}
'@
    $contDeepFile = New-Fixture "continuation_deep.cs" $contDeepSrc
    $contDeepBefore = [System.IO.File]::ReadAllBytes($contDeepFile)
    Check "continuation deep-aligned: exit 0" { (Invoke-Tool @($contDeepFile, "--rules", "continuation")) -eq 0 }
    $contDeepAfter = [System.IO.File]::ReadAllBytes($contDeepFile)
    Check "continuation deep-aligned: delimiter-aligned deep line byte-identical (guard, no de-indent)" { Test-BytesEqual $contDeepBefore $contDeepAfter }

    # --- continuation: DEEP-DOWN normalization on the REAL Shapes.cs:171/197 shape (issues_sample xls) ---
    # `var a =` opens at indent 12; both continuation lines sit at an arbitrary 23 (aligned to nothing: line 1's
    # parens are balanced, and line 2's indent is not the col after its own open `(` at 23) -> both pulled DOWN
    # to opening+4 (16). This is the finding CONTINUATION_LINE.BAD_INDENTATION cleared by the deep-down extension.
    $contGeoSrc = @'
class C {
    double Haversine(double deltaPhi, double phi1, double phi2, double deltaLambda) {
            var a = (System.Math.Sin(deltaPhi / 2) * System.Math.Sin(deltaPhi / 2)) +
                       (System.Math.Cos(phi1) * System.Math.Cos(phi2) *
                       System.Math.Sin(deltaLambda / 2) * System.Math.Sin(deltaLambda / 2));
            return a;
    }
}
'@
    $contGeoFile = New-Fixture "continuation_geo_deep.cs" $contGeoSrc
    Check "continuation geo-deep: exit 0" { (Invoke-Tool @($contGeoFile, "--rules", "continuation")) -eq 0 }
    $contGeo = Read-Text $contGeoFile
    Check "continuation geo-deep: 2nd line (System.Math.Cos paren) pulled DOWN to opening+4 (16)" { $contGeo -match "(?m)^ {16}\(System\.Math\.Cos" }
    Check "continuation geo-deep: 3rd line (System.Math.Sin) pulled DOWN to opening+4 (16)"      { $contGeo -match "(?m)^ {16}System\.Math\.Sin" }
    $contGeo1 = [System.IO.File]::ReadAllBytes($contGeoFile)
    Invoke-Tool @($contGeoFile, "--rules", "continuation") | Out-Null
    Invoke-Tool @($contGeoFile, "--rules", "continuation") | Out-Null
    $contGeo3 = [System.IO.File]::ReadAllBytes($contGeoFile)
    Check "continuation geo-deep: idempotent (byte-identical after 2 more runs)" { Test-BytesEqual $contGeo1 $contGeo3 }

    # GUARD (arg category): args aligned under the open `(` (col 16 == col after `Foo(`) are intentional alignment
    # Sparrow accepts -> byte-identical, never pulled to opening+4.
    $contAlignSrc = @'
class C {
    void M(int aaaa, int bbbb, int cccc) {
            Foo(aaaa,
                bbbb,
                cccc);
    }
    void Foo(int a, int b, int c) { }
}
'@
    $contAlignFile = New-Fixture "continuation_delim_aligned.cs" $contAlignSrc
    $contAlignBefore = [System.IO.File]::ReadAllBytes($contAlignFile)
    Check "continuation delim-aligned: exit 0" { (Invoke-Tool @($contAlignFile, "--rules", "continuation")) -eq 0 }
    $contAlignAfter = [System.IO.File]::ReadAllBytes($contAlignFile)
    Check "continuation delim-aligned: aligned args byte-identical (guard, no churn)" { Test-BytesEqual $contAlignBefore $contAlignAfter }

    # NEGATIVE: a continuation already at exactly opening+4 -> no-op (no churn from the deep-down pass).
    $contExactSrc = @'
class C {
    void M(bool a, bool b) {
        if (a &&
            b)
        {
        }
    }
}
'@
    $contExactFile = New-Fixture "continuation_exact.cs" $contExactSrc
    $contExactBefore = [System.IO.File]::ReadAllBytes($contExactFile)
    Check "continuation exact opening+4: exit 0" { (Invoke-Tool @($contExactFile, "--rules", "continuation")) -eq 0 }
    $contExactAfter = [System.IO.File]::ReadAllBytes($contExactFile)
    Check "continuation exact opening+4: byte-identical no churn" { Test-BytesEqual $contExactBefore $contExactAfter }

    # --- layout: linqalign ---
    $linqSrc = @'
using System.Linq;
class C {
    void M() {
        var orderedEvents = (from entry in sourceEvents
                                                where entry.Event != null
                                                orderby entry.Event.Timestamp
                                                select entry).ToList();
    }
}
'@
    $linqFile = New-Fixture "linqalign.cs" $linqSrc
    Check "linqalign: exit 0" { (Invoke-Tool @($linqFile, "--rules", "linqalign")) -eq 0 }
    $lq = Read-Text $linqFile
    Check "linqalign: clauses aligned to from" { $lq.Contains("        var orderedEvents = (from entry in sourceEvents`r`n                             where entry.Event != null`r`n                             orderby entry.Event.Timestamp`r`n                             select entry)") -or $lq.Contains("        var orderedEvents = (from entry in sourceEvents`n                             where entry.Event != null`n                             orderby entry.Event.Timestamp`n                             select entry)") }

    # --- rule: space ---
    $spaceSrc = @'
class C {
    void M() {
        //foo
        int a = 0;
        ///bar
        int b = 0;
        ////
        int c = 0;
        // ok
        int d = 0;
    }
}
'@
    $spaceFile = New-Fixture "space.cs" $spaceSrc
    Check "space: exit 0" { (Invoke-Tool @($spaceFile, "--rules", "space")) -eq 0 }
    $sp = Read-Text $spaceFile
    Check "space: //foo -> // foo" { $sp.Contains("// foo") -and (-not $sp.Contains("//foo")) }
    Check "space: ///bar -> /// bar" { $sp.Contains("/// bar") -and (-not $sp.Contains("///bar")) }
    Check "space: //// unchanged" { $sp.Contains("////") }
    Check "space: // ok unchanged (no double space)" { $sp.Contains("// ok") -and (-not $sp.Contains("//  ok")) }

    # --- rule: period ---
    $periodSrc = @'
class C {
    void M() {
        // hello
        // 안녕
        // done.
        // ok!
        // ----
        // 3)
        // Miss Distance(Min/Max/Avg)
        // -- 시작 --
        if (/* att.IsSingleInput&&*/ (a == 1)) { }
        int a = 0;
    }
}
'@
    $periodFile = New-Fixture "period.cs" $periodSrc
    Check "period: exit 0" { (Invoke-Tool @($periodFile, "--rules", "period")) -eq 0 }
    $pd = Read-Text $periodFile
    Check "period: // hello -> // hello." { $pd.Contains("// hello.") }
    Check "period: // 안녕 -> // 안녕. (Hangul letter qualifies)" { $pd.Contains("// 안녕.") }
    Check "period: // done. unchanged (no second period)" { $pd.Contains("// done.") -and (-not $pd.Contains("// done..")) }
    Check "period: // ok! unchanged" { $pd.Contains("// ok!") -and (-not $pd.Contains("// ok!.")) }
    Check "period: // ---- pure divider unchanged (no letter/digit)" { $pd.Contains("// ----") -and (-not $pd.Contains("// ----.")) }
    # Goal = pass Sparrow: any comment with text NOT ending in . ? ! gets a period, regardless of last char.
    Check "period: // 3) -> // 3). (ends ')' non-terminal)" { $pd.Contains("// 3).") }
    Check "period: ')'-ending prose gets period" { $pd.Contains("// Miss Distance(Min/Max/Avg).") }
    Check "period: divider-with-text gets period" { $pd.Contains("// -- 시작 --.") }
    Check "period: inline /* */ commented code gets period, stays block" { $pd.Contains("att.IsSingleInput&&.*/") }

    # --- rule: capitalize (LOWERCASE_FIRST_LETTER: strip leading punctuation, uppercase ASCII a-z first letter) ---
    # Positives use REAL-shaped findings: `<` XML markup, leading `.`/`[`, no-space `//`, and an INLINE `/* */`
    # block comment inside an `if (...)` that must stay a block comment (never converted to `//`).
    $capSrc = @'
class C {
    // <variableSource sourceID="x">
    // .foo bar
    // [tag] note
    //badcase
    void M() {
        if (a/* att.IsSingleInput&& */)
        {
        }
    }
}
'@
    $capFile = New-Fixture "capitalize.cs" $capSrc
    Check "capitalize: exit 0" { (Invoke-Tool @($capFile, "--rules", "capitalize")) -eq 0 }
    $cap = Read-Text $capFile
    Check "capitalize: <variableSource -> // VariableSource (strip <, cap v)" { $cap.Contains('// VariableSource sourceID="x">') -and (-not $cap.Contains("// <variableSource")) }
    Check "capitalize: .foo -> // Foo (strip ., cap f)" { $cap.Contains("// Foo bar") -and (-not $cap.Contains("// .foo")) }
    Check "capitalize: [tag] -> // Tag] (strip [, cap t)" { $cap.Contains("// Tag] note") -and (-not $cap.Contains("// [tag]")) }
    Check "capitalize: //badcase -> //Badcase (no space added)" { $cap.Contains("//Badcase") -and (-not $cap.Contains("// Badcase")) -and (-not $cap.Contains("//badcase")) }
    Check "capitalize: inline block stays block AND is capitalized (never converted to //)" { $cap.Contains("if (a/* Att.IsSingleInput&& */)") }
    # idempotency: second run makes zero further changes (byte-identical).
    $capOnce = [System.IO.File]::ReadAllBytes($capFile)
    Check "capitalize: second run exit 0" { (Invoke-Tool @($capFile, "--rules", "capitalize")) -eq 0 }
    $capTwice = [System.IO.File]::ReadAllBytes($capFile)
    Check "capitalize: second run byte-identical (idempotent)" { Test-BytesEqual $capOnce $capTwice }

    # capitalize NEGATIVES (must stay byte-identical): `///` XML doc, `/**` Doxygen, Korean-leading, no-letter
    # divider, and an already-capitalized comment.
    $capNegSrc = @'
class C {
    /// <summary>
    ///   deg(º)과(와) 유사한 지역화된 문자열을 찾습니다.
    /** @brief something */
    void M() {
        // 한글 주석입니다
        // ==== divider
        // Already capitalized
        int a = 0;
    }
}
'@
    $capNegFile = New-Fixture "capitalize_negative.cs" $capNegSrc
    $capNegBefore = [System.IO.File]::ReadAllBytes($capNegFile)
    Check "capitalize negative: exit 0" { (Invoke-Tool @($capNegFile, "--rules", "capitalize")) -eq 0 }
    $capNegAfter = [System.IO.File]::ReadAllBytes($capNegFile)
    Check "capitalize negative: /// /** Korean no-letter already-cap all byte-identical" { Test-BytesEqual $capNegBefore $capNegAfter }

    # --- rule: blockpromote (OPT-IN, UNVERIFIED: inline single-line /* */ -> // line above the enclosing stmt) ---
    # Generalizes `trailing` from `//` to embedded `/* */`. Two shapes: a mid-condition block inside an `if`, and a
    # catch-body block (Korean). Assert the promoted // line lands above the enclosing statement with a blank line,
    # the inline block is removed + residual code collapsed, and it is idempotent.
    $blockPromoteSrc = @'
class C {
    void M(bool cond) {
        if (/* Att.IsSingleInput&&*/ (cond))
        {
        }
    }
}
'@
    $blockPromoteFile = New-Fixture "blockpromote.cs" $blockPromoteSrc
    Check "blockpromote: exit 0" { (Invoke-Tool @($blockPromoteFile, "--rules", "blockpromote")) -eq 0 }
    $bp = Read-Text $blockPromoteFile
    Check "blockpromote: promoted // line above the if, blank line before" { $bp.Contains("`r`n`r`n        // Att.IsSingleInput&&.`r`n        if ((cond))") -or $bp.Contains("`n`n        // Att.IsSingleInput&&.`n        if ((cond))") }
    Check "blockpromote: inline block removed + whitespace collapsed (( (cond) -> ((cond))" { $bp.Contains("if ((cond))") -and (-not $bp.Contains("/*")) }
    # idempotency: the promoted // line is own-line now -> a second run is byte-identical.
    $bp1 = [System.IO.File]::ReadAllBytes($blockPromoteFile)
    Check "blockpromote: second run exit 0" { (Invoke-Tool @($blockPromoteFile, "--rules", "blockpromote")) -eq 0 }
    $bp2 = [System.IO.File]::ReadAllBytes($blockPromoteFile)
    Check "blockpromote: second run byte-identical (idempotent)" { Test-BytesEqual $bp1 $bp2 }

    # blockpromote catch-body (Korean content preserved through the lift-out).
    $blockPromoteCatchSrc = @'
using System;
class C {
    void M() {
        try { M2(); }
        catch (Exception) { /* 무시 (Cancellation 등) */ }
    }
    void M2() { }
}
'@
    $blockPromoteCatchFile = New-Fixture "blockpromote_catch.cs" $blockPromoteCatchSrc
    Check "blockpromote catch: exit 0" { (Invoke-Tool @($blockPromoteCatchFile, "--rules", "blockpromote")) -eq 0 }
    $bpc = Read-Text $blockPromoteCatchFile
    Check "blockpromote catch: promoted // line above catch (Korean kept)" { $bpc.Contains("// 무시 (Cancellation 등).") }
    Check "blockpromote catch: catch body emptied + block removed" { $bpc.Contains("catch (Exception) { }") -and (-not $bpc.Contains("/*")) }

    # blockpromote NEGATIVE: an already-own-line /* */ (and a // line) are NOT inline -> byte-identical.
    $blockPromoteNegSrc = @'
class C {
    void M() {
        /* standalone block comment */
        // standalone line comment
        int a = 0;
    }
}
'@
    $blockPromoteNegFile = New-Fixture "blockpromote_negative.cs" $blockPromoteNegSrc
    $bpnBefore = [System.IO.File]::ReadAllBytes($blockPromoteNegFile)
    Check "blockpromote negative: exit 0" { (Invoke-Tool @($blockPromoteNegFile, "--rules", "blockpromote")) -eq 0 }
    $bpnAfter = [System.IO.File]::ReadAllBytes($blockPromoteNegFile)
    Check "blockpromote negative: own-line comments byte-identical" { Test-BytesEqual $bpnBefore $bpnAfter }

    # --- SAFETY: `//` inside string literals is never a comment -> --rules all leaves the file byte-identical ---
    $safeSrc = @'
class C {
    string M() {
        var s = "http://example.com";
        var t = "a//b";
        return s + t;
    }
}
'@
    $safeFile = New-Fixture "safety.cs" $safeSrc
    $safeBefore = [System.IO.File]::ReadAllBytes($safeFile)
    Check "safety: --rules all exit 0" { (Invoke-Tool @($safeFile, "--rules", "all")) -eq 0 }
    $safeAfter = [System.IO.File]::ReadAllBytes($safeFile)
    Check "safety: file byte-identical (string-literal // untouched)" { Test-BytesEqual $safeBefore $safeAfter }
    $safeText = Read-Text $safeFile
    Check "safety: string http://example.com intact" { $safeText.Contains('"http://example.com"') }
    Check "safety: string a//b intact" { $safeText.Contains('"a//b"') }

    # --- IDEMPOTENCY: running --rules all twice yields identical bytes on the second run ---
    $idemSrc = @'
class C
{
    void M()
    {
        int x = 0;
        //hello
        /** @brief delta event marker 반환 */
        int y = 1; //abc
        X = x; Y = y;
        var s = "http://x";
    }
}
'@
    $idemFile = New-Fixture "idem.cs" $idemSrc
    Check "idempotency: first --rules all exit 0" { (Invoke-Tool @($idemFile, "--rules", "all")) -eq 0 }
    $idemAfter1 = [System.IO.File]::ReadAllBytes($idemFile)
    Check "idempotency: second --rules all exit 0" { (Invoke-Tool @($idemFile, "--rules", "all")) -eq 0 }
    $idemAfter2 = [System.IO.File]::ReadAllBytes($idemFile)
    Check "idempotency: second run is byte-identical to first" { Test-BytesEqual $idemAfter1 $idemAfter2 }

    # --- --dry-run: computes changes but writes nothing ---
    $drySrc = @'
class C
{
    void M()
    {
        int x = 0;
        //hello
    }
}
'@
    $dryFile = New-Fixture "dry.cs" $drySrc
    $dryBefore = [System.IO.File]::ReadAllBytes($dryFile)
    Check "dry-run: exit 0" { (Invoke-Tool @($dryFile, "--rules", "all", "--dry-run")) -eq 0 }
    $dryAfter = [System.IO.File]::ReadAllBytes($dryFile)
    Check "dry-run: file unchanged (nothing written)" { Test-BytesEqual $dryBefore $dryAfter }

    # --- --files-from index.csv: distinct 파일명 column drives which file is edited ---
    $ffSrc = @'
class C {
    void M() {
        //x
        int a = 0;
    }
}
'@
    $null = New-Fixture "ff_fixture.cs" $ffSrc
    # index.csv WITH BOM; the 위험도 AND 체커명 columns are quoted and contain commas to prove CSV parsing
    # (a naive Split(',') would misread the 파일명 column that sits AFTER the quoted comma-bearing 위험도).
    $csv = @'
md_file,ID,체커 키,위험도,파일명,라인,이슈 상태,체커명
items/1.md,101,FORMATTING.COMMENT.MISSING_SPACE_AFTER_DELIMITER,"낮음,높음",ff_fixture.cs,7,미해결,"사용되지 않는 객체, 암시적 타입"
'@
    $csvPath = Join-Path $work "index.csv"
    [System.IO.File]::WriteAllText($csvPath, $csv, $utf8Bom)
    Check "files-from: exit 0" { (Invoke-Tool @("--files-from", $csvPath, "--root", $work, "--rules", "space")) -eq 0 }
    $ffText = Read-Text (Join-Path $work "ff_fixture.cs")
    Check "files-from: named fixture edited (//x -> // x)" { $ffText.Contains("// x") -and (-not $ffText.Contains("//x")) }

    # --- capitalize is now ACTIVE -> exit 0; blankline/asterisk stay removed/deferred -> exit 2 ---
    Check "capitalize rule -> exit 0 (re-enabled)" { (Invoke-Tool @($spaceFile, "--rules", "capitalize")) -eq 0 }
    Check "blockpromote rule -> exit 0 (opt-in registered key)" { (Invoke-Tool @($spaceFile, "--rules", "blockpromote")) -eq 0 }
    Check "blankline rule -> exit 2 (removed)" { (Invoke-Tool @($spaceFile, "--rules", "blankline")) -eq 2 }
    Check "asterisk rule -> exit 2 (deferred)" { (Invoke-Tool @($spaceFile, "--rules", "asterisk")) -eq 2 }
    Check "unknown rule -> exit 2" { (Invoke-Tool @($spaceFile, "--rules", "bogus")) -eq 2 }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { throw ("SparrowCommentFix E2E failed:`n  " + ($failures -join "`n  ")) }
Write-Host "SparrowCommentFix E2E passed."

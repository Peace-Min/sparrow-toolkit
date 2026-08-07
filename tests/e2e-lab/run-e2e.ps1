#requires -Version 5.1
<#
    run-e2e.ps1 — END-TO-END integration test for the Sparrow (파수) [XLS 분리] export pipeline.

    Exercises the REAL pieces (parser exe + tools\Compare-Sparrow.ps1) against a realistic mini C#
    project with planted defects and generated Sparrow-style .xls. 선행 문서(체커 가이드/프롬프트)는
    필요하지 않다 — 익스포터는 xls만 읽는다.

    Pipeline proven: 파싱(parser, 체커별 md 분리) -> 수정+빌드(G1) -> G2 게이트.
    Prints PASS/FAIL per check; exits nonzero if any check fails.
#>
param([string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$TestsDir  = Split-Path -Parent $ScriptDir                      # tests
$SkillDir  = if ($RepositoryRoot) { $RepositoryRoot } else { Split-Path -Parent $TestsDir }   # 레포 루트
$Compare   = Join-Path $SkillDir 'tools\Compare-Sparrow.ps1'
$ParserExe = Join-Path $SkillDir 'tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe'

if (-not (Test-Path -LiteralPath $ParserExe)) {
    $parserProject = Join-Path $SkillDir 'tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj'
    $localParser = Join-Path (Split-Path -Parent $parserProject) 'bin\Release\net8.0\SparrowXlsExport.exe'
    if (-not (Test-Path -LiteralPath $localParser)) {
        & dotnet build $parserProject -c Release | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "SparrowXlsExport build failed: $parserProject" }
    }
    $ParserExe = $localParser
}

$Out       = Join-Path $ScriptDir '_out'
$BeforeXls = Join-Path $ScriptDir 'sample-before.xls'
$AfterXls  = Join-Path $ScriptDir 'sample-after.xls'

# ---- tiny assert harness --------------------------------------------------
$script:Fails = 0
$script:Checks = 0
function Check {
    param([string]$Name, [bool]$Cond, [string]$Detail = '')
    $script:Checks++
    if ($Cond) {
        Write-Host ("  [PASS] {0}" -f $Name)
    } else {
        $script:Fails++
        Write-Host ("  [FAIL] {0}{1}" -f $Name, $(if ($Detail) { "  -- $Detail" } else { '' }))
    }
}
function Read-TextNoBom {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $t = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($t.Length -gt 0 -and [int][char]$t[0] -eq 0xFEFF) { $t = $t.Substring(1) }
    return $t
}

Write-Host "==================== Sparrow [XLS 분리] E2E ===================="
Write-Host ("ScriptDir : {0}" -f $ScriptDir)
Write-Host ("Parser    : {0}" -f $ParserExe)
Write-Host ("Compare   : {0}" -f $Compare)

# preconditions
Check "parser exe exists"        (Test-Path -LiteralPath $ParserExe) $ParserExe
Check "Compare-Sparrow.ps1 exists" (Test-Path -LiteralPath $Compare) $Compare

# clean out dir
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
[void](New-Item -ItemType Directory -Force -Path $Out)

# ---- generate golden xls (NPOI) -------------------------------------------
Write-Host "`n---- gen-xls: generate golden sample-before.xls / sample-after.xls ----"
& dotnet run --project (Join-Path $ScriptDir 'gen-xls') -c Release -- $ScriptDir 2>&1 | Out-Null
Check "gen-xls exit 0"           ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
Check "sample-before.xls created" (Test-Path -LiteralPath $BeforeXls) $BeforeXls
Check "sample-after.xls created"  (Test-Path -LiteralPath $AfterXls) $AfterXls

# ============================================================ A. 파싱
Write-Host "`n==== A. 파싱 (parser -> <체커 키>\{ID}_{파일명}_{라인}.md) ===="
$parsed = Join-Path $Out 'parsed'
& $ParserExe $BeforeXls --out $parsed | Out-Null
$parseExit = $LASTEXITCODE
Check "A: parser exit 0"         ($parseExit -eq 0) "exit=$parseExit"
$mdCount = 0
if (Test-Path -LiteralPath $parsed) { $mdCount = @(Get-ChildItem -LiteralPath $parsed -Recurse -Filter *.md -File).Count }
Check "A: 5 item .md files"      ($mdCount -eq 5) "found=$mdCount"

# 체커별 분리 산출물: 최상위가 체커키 디렉토리이고, 그 안의 항목 md 는 {ID}_{파일명}_{라인}.md.
$expectKeys = @('FORWARD_NULL','RESOURCE_LEAK','EMPTY_CATCH_BLOCK','OVERLY_BROAD_CATCH','NULL_RETURN_STD')
$checkerDirs = @()
if (Test-Path -LiteralPath $parsed) { $checkerDirs = @(Get-ChildItem -LiteralPath $parsed -Directory | Select-Object -ExpandProperty Name | Sort-Object) }
$allKeys = $true
foreach ($k in $expectKeys) { if ($checkerDirs -notcontains $k) { $allKeys = $false } }
Check "A: 체커키 폴더 5개 생성 (폴더명 = 체커 키)" ($allKeys -and ($checkerDirs.Count -eq 5)) ($checkerDirs -join ', ')

$perDirOk = $true
foreach ($k in $expectKeys) {
    $d = Join-Path $parsed $k
    if (-not (Test-Path -LiteralPath $d)) { $perDirOk = $false; continue }
    if (@(Get-ChildItem -LiteralPath $d -Filter *.md -File).Count -ne 1) { $perDirOk = $false }
}
Check "A: 체커별 폴더에 검출 건수만큼 md (각 1건)" $perDirOk

# 파일명 규칙 = {ID}_{파일명}_{라인}.md (체커키는 폴더명이므로 파일명에서 빠지고, 잘리지 않는다).
$itemFiles = @(Get-ChildItem -LiteralPath $parsed -Recurse -Filter *.md -File)
$nameRuleOk = $true
foreach ($f in $itemFiles) {
    if ($f.Name -notmatch '^\d+_[^\\/]+\.cs_\d+\.md$') { $nameRuleOk = $false }
    if ($f.Name -match ('_' + [regex]::Escape($f.Directory.Name) + '_')) { $nameRuleOk = $false }
}
Check "A: item md 파일명 = {ID}_{파일명}_{라인}.md (체커키 미포함)" $nameRuleOk (@($itemFiles | ForEach-Object { $_.Name }) -join ', ')

# 부산물 금지: items\ · index.csv · checkers.md · 요약/지침 md 는 하나도 만들지 않는다.
$rootFiles = @(Get-ChildItem -LiteralPath $parsed -File)
$nonMd = @(Get-ChildItem -LiteralPath $parsed -Recurse -File | Where-Object { $_.Extension -ne '.md' })
Check "A: items\ 폴더 없음"        (-not (Test-Path -LiteralPath (Join-Path $parsed 'items')))
Check "A: index.csv 없음"          (-not (Test-Path -LiteralPath (Join-Path $parsed 'index.csv')))
Check "A: checkers.md 없음"        (-not (Test-Path -LiteralPath (Join-Path $parsed 'checkers.md')))
Check "A: 출력 루트에 파일 0개 (요약/지침 md 포함 부산물 없음)" ($rootFiles.Count -eq 0) (@($rootFiles | ForEach-Object { $_.Name }) -join ', ')
Check "A: md 외 파일 0개"          ($nonMd.Count -eq 0) (@($nonMd | ForEach-Object { $_.Name }) -join ', ')

# 항목 md 는 수정 위치(파일/라인)와 대상 소스를 담는 자기완결 단위여야 한다.
$fnDir = Join-Path $parsed 'FORWARD_NULL'
$fnItem = $null
if (Test-Path -LiteralPath $fnDir) { $fnItem = @(Get-ChildItem -LiteralPath $fnDir -Filter *.md -File) | Select-Object -First 1 }
$fnText = if ($fnItem) { Read-TextNoBom $fnItem.FullName } else { '' }
Check "A: 항목 md 에 파일/라인 필드" (($fnText -match '\|\s*파일명\s*\|') -and ($fnText -match '\|\s*라인\s*\|'))
Check "A: 항목 md 에 대상 소스 코드" ($fnText -match 'node\.Value')

# ============================================================ B. 합성 수정 적용 + 빌드 (G1)
Write-Host "`n==== B. 합성 수정 적용 + 빌드 (G1) ===="
# multi-line fix bodies built by joining with LF (never CRLF) so they match the LF-normalized source.
$fnBefore  = '            return node.Value;'
$fnAfter   = (@('            if (node == null) return -1;','            return node.Value;') -join "`n")

$rlBefore  = (@(
    '            var fs = new FileStream(path, FileMode.Open);',
    '            var buf = new byte[16];',
    '            fs.Read(buf, 0, buf.Length);',
    '            fs.Close();',
    '            return buf;') -join "`n")
$rlAfter   = (@(
    '            var buf = new byte[16];',
    '            using (var fs = new FileStream(path, FileMode.Open))',
    '            {',
    '                fs.Read(buf, 0, buf.Length);',
    '            }',
    '            return buf;') -join "`n")

$ecBefore  = '            catch { }'
$ecAfter   = (@(
    '            catch (Exception ex)',
    '            {',
    '                Console.Error.WriteLine("DoWork failed: " + ex.Message);',
    '                throw;',
    '            }') -join "`n")

$nrBefore  = '            return Activator.CreateInstance(t);'
$nrAfter   = (@('            if (t == null) return null;','            return Activator.CreateInstance(t);') -join "`n")

$repairs = @(
    @{ Checker = 'FORWARD_NULL'; File = 'NullDeref.cs'; Before = $fnBefore; After = $fnAfter },
    @{ Checker = 'RESOURCE_LEAK'; File = 'LeakFile.cs'; Before = $rlBefore; After = $rlAfter },
    @{ Checker = 'EMPTY_CATCH_BLOCK'; File = 'SwallowEx.cs'; Before = $ecBefore; After = $ecAfter },
    @{ Checker = 'NULL_RETURN_STD'; File = 'BclNull.cs'; Before = $nrBefore; After = $nrAfter }
)

$fixedApp = Join-Path $Out 'SampleApp-fixed'
Copy-Item -LiteralPath (Join-Path $ScriptDir 'SampleApp') -Destination $fixedApp -Recurse -Force
# remove any copied build outputs to force a clean build
foreach ($d in @('bin','obj')) { $p = Join-Path $fixedApp $d; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

$applied = 0
foreach ($r in $repairs) {
    $target = Join-Path $fixedApp ([string]$r.File)
    $before = [string]$r.Before
    $after  = [string]$r.After
    $okTarget = Test-Path -LiteralPath $target
    if (-not $okTarget) { Check ("B: target exists {0}" -f $r.File) $false $target; continue }
    # normalize to LF so multi-line before matches exactly
    $src = [System.IO.File]::ReadAllText($target) -replace "`r`n", "`n"
    $occ = ($src.Length - $src.Replace($before, '').Length)
    $count = if ($before.Length -gt 0) { [math]::Round($occ / $before.Length) } else { 0 }
    Check ("B: '{0}' before found exactly once in {1}" -f $r.Checker, $r.File) ($count -eq 1) "count=$count"
    if ($count -eq 1) {
        $src = $src.Replace($before, $after)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($target, $src, $utf8)
        $applied++
    }
}
Check "B: 4 fixes applied"         ($applied -eq 4) "applied=$applied"

$csproj = Join-Path $fixedApp 'SampleApp.csproj'
Write-Host "  building fixed SampleApp (dotnet build, LangVersion 7.3) ..."
$buildOut = & dotnet build $csproj -c Debug --nologo 2>&1
$buildExit = $LASTEXITCODE
Check "B: dotnet build SUCCESS (G1)" ($buildExit -eq 0) "exit=$buildExit"
if ($buildExit -ne 0) { $buildOut | ForEach-Object { Write-Host ("      | " + $_) } }

# ============================================================ C. G2 게이트
Write-Host "`n==== C. G2 게이트 (Compare-Sparrow.ps1: 검출 소멸 + 신규 0) ===="
# positive: before vs after, FORWARD_NULL 소멸 + 신규 0 => PASS (exit 0)
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Compare -Before $BeforeXls -After $AfterXls -Checker FORWARD_NULL -Exe $ParserExe | Out-Null
$g2pos = $LASTEXITCODE
Check "C: G2 PASS (before vs after, FORWARD_NULL eliminated) exit 0" ($g2pos -eq 0) "exit=$g2pos"

# negative control: before vs before, FORWARD_NULL still present => FAIL (exit 1)
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Compare -Before $BeforeXls -After $BeforeXls -Checker FORWARD_NULL -Exe $ParserExe | Out-Null
$g2neg = $LASTEXITCODE
Check "C: G2 discriminates (before vs before) exit 1" ($g2neg -eq 1) "exit=$g2neg"

# ============================================================ summary
Write-Host "`n============================================================"
Write-Host ("checks: {0}   fails: {1}" -f $script:Checks, $script:Fails)
if ($script:Fails -eq 0) {
    Write-Host "== E2E PASS =="
    exit 0
} else {
    Write-Host ("== E2E FAIL ({0}) ==" -f $script:Fails)
    exit 1
}

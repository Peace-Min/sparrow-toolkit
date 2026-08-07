#requires -Version 5.1
<#
    sparrow-toolkit 독립 검증 러너.
    기본(스위치 없음): 도구/테스트 소스 존재 확인 + 모든 PowerShell 테스트 구문검사(빌드 없음, 빠름).
    opt-in 스위치: 해당 E2E 테스트를 실제 빌드+실행(.NET SDK 필요). -All 로 전체 opt-in.

    전체 출력은 콘솔에 그대로 나가면서 -LogDir(기본 tests\_logs)의 validate-<stamp>.log 에도 남는다(Tee).
    실패하면 마지막에 그 파일 경로를 안내하므로, 문제 신고 시 그 한 파일만 첨부하면 된다. 최신 10개만 보관.

    마지막에 opt-in 테스트 집계(실행/스킵/실패)를 찍는다. 하나라도 실패하면 실패한 테스트 이름을 모아
    출력하고 0 이 아닌 코드로 종료한다. 스킵은 이름과 사유를 함께 찍으므로 ".NET SDK 없는 PC 에서
    아무 단정도 안 돌고 통과" 상황이 육안으로 구분된다.

    (peace-skillbank 의 공유 validate.ps1 에서 sparrow 부분만 추출·루트상대화한 자기완결 버전.)
#>
param(
    [string]$RepositoryRoot = $PSScriptRoot,
    [switch]$All,
    [switch]$IncludeSparrowE2E,                     # SparrowXlsExport (Track C export)
    [switch]$IncludeSparrowMappingTests,            # SparrowXlsExport --guides (checker→rule self-contained 부착)
    [switch]$IncludeSyntaxFixE2E,                   # SparrowSyntaxFix (Track A)
    [switch]$IncludeCommentE2E,                     # SparrowCommentFix (Track B)
    [switch]$IncludeSparrowLoopTests,               # cross-rule loop / idempotency / compile
    [switch]$IncludeSparrowRealPatternTests,        # grounded real-MyApp-pattern pipeline regression
    [switch]$IncludeSparrowRealXlsC3Tests,          # real-xls C3 detect+fix
    [switch]$IncludeSparrowRealXlsForHoistTests,    # real-xls forhoist detect+fix
    [switch]$IncludeSparrowRealXlsContinuationDeepTests, # real-xls deep-continuation normalize
    [switch]$IncludeSparrowRealXlsBlockPromoteTests,# real-xls blockpromote detect+fix
    [switch]$IncludeSparrowRealXlsScopeLoopTests,   # real-xls Track C scope-selection loop (cross-PC)
    [switch]$IncludeSparrowExhaustiveXls,           # exhaustive Track A/B over the REAL MyApp xls
    [switch]$IncludeG2GateTests,                    # Compare-Sparrow G2 게이트 시나리오
    [switch]$IncludeCoreTests,                      # SparrowXlsExport.Core\CoreTests (Core 출력 계약 + Track C 부산물 0)
    [switch]$IncludeTrackCE2E,                      # tests\e2e-lab: 익스포터 -> 수정+빌드(G1) -> G2 파이프라인
    [switch]$IncludeGuiUiaTests,                    # tools\SparrowRunner.Gui: Track C 매핑 패널 UIA 하네스(창이 잠깐 뜸)
    [string]$LogDir                                 # 트랜스크립트 폴더 (기본 <repo>\tests\_logs)
)

$ErrorActionPreference = "Stop"
if ($All) {
    $IncludeSparrowE2E = $IncludeSyntaxFixE2E = $IncludeCommentE2E = $true
    $IncludeSparrowMappingTests = $true
    $IncludeSparrowLoopTests = $IncludeSparrowRealPatternTests = $true
    $IncludeSparrowRealXlsC3Tests = $IncludeSparrowRealXlsForHoistTests = $true
    $IncludeSparrowRealXlsContinuationDeepTests = $IncludeSparrowRealXlsBlockPromoteTests = $true
    $IncludeSparrowRealXlsScopeLoopTests = $IncludeSparrowExhaustiveXls = $true
    $IncludeG2GateTests = $true
    # CoreTests 는 합성 픽스처만 쓰는 in-process 하네스(실 xls 불필요)라 -All 에 항상 포함한다.
    # "Track C 부산물 0" 의 가장 강한 단정이 여기 있으므로 PR 게이트 밖에 두면 안 된다.
    $IncludeCoreTests = $true
    # GUI UIA 하네스는 -All 에 포함한다. 실제 WPF 창을 잠깐 띄워 UIA 로 구동하고 종료한다(앱이 스스로 자기 창을
    # tests\_logs\uia-*\shots\ 에 PNG 로 렌더한다 — 외부 스크린샷 도구는 쓰지 않는다).
    # 임시 --guides-dir 로 실 캐시를 건드리지 않으며, .NET SDK/UIA/데스크톱 세션이 없으면 self-skip 이라 안전하다.
    $IncludeGuiUiaTests = $true
    # 주의: -IncludeTrackCE2E 는 -All 에 포함하지 않는다. e2e-lab 은 커밋된 골든 fixture
    # (sample-before/after.xls)를 재생성하므로 작업 트리를 더럽힐 수 있다. 필요할 때만 명시 실행.
}

# ---- 0. 진단 트랜스크립트(콘솔 유지 + 파일 기록) ----
# 실패 원인을 사후에 판단할 수 있도록 이 실행의 전체 출력을 한 파일로 남긴다. 기록 실패는 검증을 막지 않는다.
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $RepositoryRoot "tests\_logs" }
$KeepTranscripts = 10
$transcriptPath = $null
try {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $transcriptPath = Join-Path $LogDir ("validate-" + (Get-Date).ToString("yyyyMMdd-HHmmss") + ".log")
    Start-Transcript -Path $transcriptPath -Force | Out-Null
    # 회전: 이름(=시각) 내림차순으로 최신 $KeepTranscripts 개만 남긴다(현재 파일 포함).
    foreach ($old in @(Get-ChildItem -LiteralPath $LogDir -Filter "validate-*.log" -File -ErrorAction SilentlyContinue |
                       Sort-Object Name -Descending | Select-Object -Skip $KeepTranscripts)) {
        Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue
    }
}
catch {
    Write-Host "  [warn] 트랜스크립트를 시작할 수 없습니다(콘솔 출력만): $($_.Exception.Message)"
    $transcriptPath = $null
}

try {

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}
function Test-PowerShellSyntax {
    param([Parameter(Mandatory)][string]$Path)
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors) | Out-Null
    Assert-Condition ($errors.Count -eq 0) "PowerShell parse failed: $Path"
}
function J([string]$rel) { Join-Path $RepositoryRoot $rel }

$pass = 0
function Ok([string]$m) { Write-Host "  [ok]   $m"; $script:pass++ }

if ($transcriptPath) { Write-Host "  진단 트랜스크립트: $transcriptPath" }

# ---- 1. 핵심 도구/러너 소스 존재 ----
$toolSources = @(
    "tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj",
    "tools\_internal\SparrowXlsExport\Program.cs",
    "tools\_internal\SparrowXlsExport\FixtureGen\FixtureGen.csproj",
    "tools\_internal\SparrowXlsExport.Core\SparrowXlsExport.Core.csproj",
    "tools\_internal\SparrowXlsExport.Core\SparrowExporter.cs",
    "tools\_internal\SparrowXlsExport.Core\CheckerRuleMapper.cs",
    "tools\_internal\SparrowXlsExport.Core\CheckerRuleStore.cs",
    "tools\_internal\SparrowXlsExport.Core\TrackCRunReport.cs",
    "tools\_internal\SparrowXlsExport.Core\CoreTests\CoreTests.csproj",
    "tools\_internal\SparrowXlsExport.Core\CoreTests\Program.cs",
    "tools\_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj",
    "tools\_internal\SparrowSyntaxFix\Program.cs",
    "tools\_internal\SparrowSyntaxFix\RewriteEngine.cs",
    "tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1",
    "tools\_internal\SparrowCommentFix\SparrowCommentFix.csproj",
    "tools\_internal\SparrowCommentFix\Program.cs",
    "tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1",
    "tools\Run-SparrowAll.ps1",
    "tools\publish-airgap.ps1",
    "tools\Compare-Sparrow.ps1",
    "tools\SparrowRunner.Gui\MainWindow.xaml.cs",
    "tools\SparrowRunner.Gui\MainWindow.xaml",
    "tools\SparrowRunner.Gui\CheckerMappingRow.cs",
    "tools\SparrowRunner.Gui\CheckerAssignmentRow.cs",
    "tools\SparrowRunner.Gui\RuleManagerWindow.xaml",
    "tools\SparrowRunner.Gui\RuleManagerWindow.xaml.cs",
    "tools\SparrowRunner.Gui\SessionLog.cs",
    "tools\SparrowRunner.Gui\WindowSnapshot.cs",
    "tools\SparrowRunner.Gui\SourceScopeDiscovery.cs",
    "tools\SparrowRunner.Gui\SourceScopeNode.cs",
    "tools\SparrowRunner.Gui\ScopeManifestWriter.cs",
    "tools\SparrowRunner.Gui\XlsScopeDiscovery.cs",
    "tests\SparrowGuiUiaFixture\SparrowGuiUiaFixture.csproj",
    "tests\SparrowGuiUiaFixture\Program.cs",
    "SparrowRunner.Gui\SparrowRunner.Gui.sln",
    "SKILL.md"
)
foreach ($s in $toolSources) {
    Assert-Condition (Test-Path -LiteralPath (J $s)) "Missing tool source: $s"
}
Ok ("도구/러너 소스 {0}개 존재" -f $toolSources.Count)

# ---- 2. sparrow 테스트 구문검사 + 러너 구문검사 ----
$tests = @{
    E2E              = "tests\sparrow-xlsexport-fixtures.ps1"
    Mapping          = "tests\sparrow-mapping-tests.ps1"
    SyntaxFix        = "tests\sparrow-syntaxfix-fixtures.ps1"
    Comment          = "tests\sparrow-commentfix-fixtures.ps1"
    Loop             = "tests\sparrow-loop-tests.ps1"
    RealPattern      = "tests\sparrow-realpattern-tests.ps1"
    RealXlsC3        = "tests\sparrow-realxls-c3-tests.ps1"
    RealXlsForHoist  = "tests\sparrow-realxls-forhoist-tests.ps1"
    RealXlsContDeep  = "tests\sparrow-realxls-continuation-deep-tests.ps1"
    RealXlsBlockProm = "tests\sparrow-realxls-blockpromote-tests.ps1"
    RealXlsScopeLoop = "tests\sparrow-realxls-scope-loop-tests.ps1"
    Exhaustive       = "tests\sparrow-exhaustive-xls-test.ps1"
    G2Gate           = "tests\g2-gate-tests.ps1"
    CoreTests        = "tests\coretests-run.ps1"
    TrackCE2E        = "tests\e2e-lab\run-e2e.ps1"
    GuiUia           = "tests\gui-uia-tests.ps1"
}
foreach ($k in $tests.Keys) {
    $p = J $tests[$k]
    Assert-Condition (Test-Path -LiteralPath $p) "Missing test: $($tests[$k])"
    Test-PowerShellSyntax -Path $p
}
Test-PowerShellSyntax -Path (J "tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1")
Test-PowerShellSyntax -Path (J "tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1")
Test-PowerShellSyntax -Path (J "tools\Compare-Sparrow.ps1")
Ok ("테스트 {0}개 + 러너/게이트 3개 구문검사 통과" -f $tests.Count)

# ---- 3. opt-in E2E 실행 ----
#
# 자식 테스트 신호 규약(모든 tests\*.ps1 이 이 규약을 지킨다):
#   성공  : 마지막에 exit 0.  ← 필수. 그냥 끝내면 스크립트 안에서 마지막으로 호출한 네이티브 명령의
#           $LASTEXITCODE 가 그대로 남는다(예: commentfix 픽스처는 "알 수 없는 규칙 -> exit 2" 를
#           단정하고 끝나므로 통과인데도 2 가 남는다) → 거짓 실패가 된다.
#   실패  : throw  또는  exit <0 아닌 값>.  둘 다 실제로 쓰이고 있어 둘 다 잡아야 한다.
#   스킵  : $global:SparrowTestSkip 에 사유 문자열을 넣고 return (dotnet SDK/실 xls/UIA 부재 등).
#
# 왜 이렇게 잡는가:
#   - throw 는 $ErrorActionPreference='Stop' 아래에서 부모로 전파되므로 try/catch 로 받는다.
#   - exit 는 다르다. & 로 부른 자식 스크립트의 exit 는 부모를 종료시키지도 throw 하지도 않고
#     $LASTEXITCODE 만 남긴다(실측 확인). 예전 Run-Test 는 이 값을 보지 않아서, exit 1 로만 실패를
#     알리는 테스트(g2-gate / realxls-* / scope-loop)의 실패가 전부 조용히 버려졌다.
#   - 스킵을 exit 코드로 신호하지 않고 전역 변수로 받는 이유: 스킵은 스크립트 중간(네이티브 호출 뒤)에서도
#     일어나므로 exit 코드만으로는 "스킵"과 "직전 네이티브 명령의 잔여 코드"를 구분할 수 없다.
#     그래서 스킵 판정을 exit 코드 판정보다 먼저 한다.
#
# 하나 실패해도 즉시 중단하지 않고 나머지를 계속 돌린 뒤 마지막에 모아서 보고한다:
# 한 번의 -All 로 전체 상태를 얻는 편이 진단에 유리하고(특히 GUI/실 xls 처럼 비싼 항목),
# 게이트로서의 성질(실패 시 0 아닌 종료)은 마지막 요약이 동일하게 강제하기 때문이다.
$script:ranCount = 0
$script:skipCount = 0
$script:failedTests = New-Object System.Collections.Generic.List[string]
$script:skippedTests = New-Object System.Collections.Generic.List[string]

function Run-Test([bool]$enabled, [string]$rel) {
    if (-not $enabled) { return }
    Write-Host "  >>> $rel"
    $global:SparrowTestSkip = $null
    $global:LASTEXITCODE = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $failReason = $null
    $skipReason = $null
    try {
        & (J $rel) -RepositoryRoot $RepositoryRoot
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        if ($global:SparrowTestSkip) { $skipReason = [string]$global:SparrowTestSkip }
        elseif ($code -ne 0) { $failReason = ("exit code {0}" -f $code) }
    }
    catch {
        $failReason = ($_.Exception.Message -replace "`r?`n", " / ")
    }
    finally {
        $sw.Stop()
        $global:SparrowTestSkip = $null
    }

    if ($skipReason) {
        $script:skipCount++
        $script:skippedTests.Add(("{0}  —  {1}" -f $rel, $skipReason))
        Write-Host ("  [SKIP] {0}  —  {1}" -f $rel, $skipReason)
    }
    elseif ($failReason) {
        $script:ranCount++
        $script:failedTests.Add(("{0}  —  {1}" -f $rel, $failReason))
        Write-Host ("  [FAIL] {0}  —  {1}" -f $rel, $failReason)
    }
    else {
        $script:ranCount++
        Write-Host ("  [pass] {0}  ({1:N1}s)" -f $rel, $sw.Elapsed.TotalSeconds)
    }
}
Run-Test $IncludeSparrowE2E                       $tests.E2E
Run-Test $IncludeSparrowMappingTests               $tests.Mapping
Run-Test $IncludeSyntaxFixE2E                      $tests.SyntaxFix
Run-Test $IncludeCommentE2E                        $tests.Comment
Run-Test $IncludeSparrowLoopTests                  $tests.Loop
Run-Test $IncludeSparrowRealPatternTests           $tests.RealPattern
Run-Test $IncludeSparrowRealXlsC3Tests             $tests.RealXlsC3
Run-Test $IncludeSparrowRealXlsForHoistTests       $tests.RealXlsForHoist
Run-Test $IncludeSparrowRealXlsContinuationDeepTests $tests.RealXlsContDeep
Run-Test $IncludeSparrowRealXlsBlockPromoteTests   $tests.RealXlsBlockProm
Run-Test $IncludeSparrowRealXlsScopeLoopTests      $tests.RealXlsScopeLoop
Run-Test $IncludeSparrowExhaustiveXls              $tests.Exhaustive
Run-Test $IncludeG2GateTests                       $tests.G2Gate
Run-Test $IncludeCoreTests                         $tests.CoreTests
Run-Test $IncludeTrackCE2E                         $tests.TrackCE2E
Run-Test $IncludeGuiUiaTests                       $tests.GuiUia

# ---- 4. 집계 요약 ----
# "아무것도 안 돌았는데 통과" 를 육안으로 구분할 수 있어야 한다. 스킵은 이름 + 사유까지 찍는다.
Write-Host ""
Write-Host ("---- opt-in 테스트 집계: 실행 {0} · 스킵 {1} · 실패 {2} ----" -f `
            $script:ranCount, $script:skipCount, $script:failedTests.Count)
if ($script:skippedTests.Count -gt 0) {
    Write-Host "   [스킵]"
    foreach ($s in $script:skippedTests) { Write-Host ("     - {0}" -f $s) }
}
if ($script:failedTests.Count -gt 0) {
    Write-Host "   [실패]"
    foreach ($f in $script:failedTests) { Write-Host ("     - {0}" -f $f) }
}
if (($script:ranCount + $script:skipCount) -eq 0) {
    Write-Host "   (opt-in 테스트를 하나도 켜지 않았습니다 — 정적 검사만 수행. 전체는 -All)"
}
elseif ($script:ranCount -eq 0) {
    Write-Host "   [주의] opt-in 테스트가 전부 스킵됐습니다 — E2E 단정이 0개 실행됐습니다."
    Write-Host "          (.NET SDK / 실 xls / 데스크톱 세션이 없는 PC 입니다. 이 실행은 게이트가 아닙니다.)"
}

Write-Host ""
if ($script:failedTests.Count -gt 0) {
    throw ("opt-in 테스트 실패 {0}건: " -f $script:failedTests.Count) + (($script:failedTests -join " ; "))
}
Write-Host ("== validate 통과 (정적 검사군 {0} · E2E 실행 {1} · 스킵 {2} · 실패 0) ==" -f `
            $pass, $script:ranCount, $script:skipCount)
if ($transcriptPath) { Write-Host ("   트랜스크립트: {0}" -f $transcriptPath) }

}
catch {
    # 실패 원인은 콘솔에도 남지만, 사후 분석/신고용으로 어떤 파일을 첨부해야 하는지 마지막에 명시한다.
    Write-Host ""
    Write-Host ("== validate 실패 ==")
    Write-Host ("   원인: {0}" -f ($_.Exception.Message -replace "`r?`n", " / "))
    if ($script:failedTests -and $script:failedTests.Count -gt 0) {
        Write-Host ("   실패한 테스트 {0}건:" -f $script:failedTests.Count)
        foreach ($f in $script:failedTests) { Write-Host ("     - {0}" -f $f) }
    }
    if ($script:skippedTests -and $script:skippedTests.Count -gt 0) {
        Write-Host ("   스킵된 테스트 {0}건:" -f $script:skippedTests.Count)
        foreach ($s in $script:skippedTests) { Write-Host ("     - {0}" -f $s) }
    }
    if ($transcriptPath) { Write-Host ("   전체 출력 로그(신고 시 첨부): {0}" -f $transcriptPath) }
    Write-Host ("   GUI UIA 하네스를 돌렸다면 진단 폴더도 함께: {0}\uia-*" -f $LogDir)
    throw
}
finally {
    if ($transcriptPath) { try { Stop-Transcript | Out-Null } catch { } }
}

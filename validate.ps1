#requires -Version 5.1
<#
    sparrow-toolkit 독립 검증 러너.
    기본(스위치 없음): 도구/테스트 소스 존재 확인 + 모든 PowerShell 테스트 구문검사(빌드 없음, 빠름).
    opt-in 스위치: 해당 E2E 테스트를 실제 빌드+실행(.NET SDK 필요). -All 로 전체 opt-in.

    전체 출력은 콘솔에 그대로 나가면서 -LogDir(기본 tests\_logs)의 validate-<stamp>.log 에도 남는다(Tee).
    실패하면 마지막에 그 파일 경로를 안내하므로, 문제 신고 시 그 한 파일만 첨부하면 된다. 최신 10개만 보관.

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
function Run-Test([bool]$enabled, [string]$rel) {
    if (-not $enabled) { return }
    Write-Host "  >>> $rel"
    & (J $rel) -RepositoryRoot $RepositoryRoot
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
Run-Test $IncludeTrackCE2E                         $tests.TrackCE2E
Run-Test $IncludeGuiUiaTests                       $tests.GuiUia

Write-Host ""
Write-Host ("== validate 통과 ({0} 검사군) ==" -f $pass)
if ($transcriptPath) { Write-Host ("   트랜스크립트: {0}" -f $transcriptPath) }

}
catch {
    # 실패 원인은 콘솔에도 남지만, 사후 분석/신고용으로 어떤 파일을 첨부해야 하는지 마지막에 명시한다.
    Write-Host ""
    Write-Host ("== validate 실패 ==")
    if ($transcriptPath) { Write-Host ("   전체 출력 로그(신고 시 첨부): {0}" -f $transcriptPath) }
    Write-Host ("   GUI UIA 하네스를 돌렸다면 진단 폴더도 함께: {0}\uia-*" -f $LogDir)
    throw
}
finally {
    if ($transcriptPath) { try { Stop-Transcript | Out-Null } catch { } }
}

#requires -Version 5.1
<#
    GUI UIA harness for tools\SparrowRunner.Gui — the 대분류 2분할 화면([코드 자동수정 (C#)] / [XLS 분리 (모든 언어)]),
    the XLS-derived 범위 트리, and the Track C 체커 규칙 관리 model (named rule library + explicit checker
    assignments in a SEPARATE window, with NO name-based auto-mapping).

    Drives the REAL WPF windows through UI Automation (System.Windows.Automation, an OS API — PS 5.1 attaches to
    the net8 process). It launches the GUI with an xls prefilled AND --open-rule-manager, so after
    the load-time census the [XLS 분리] 대분류 is auto-selected and the [체커 규칙 관리] window is opened
    automatically with the detected checkers loaded. It then verifies, in order:

      0) 대분류 분할: --trackc-xls 로 기동하면 SectionXlsTab 이 선택되어 있고, 그 화면에는 xls/출력/규칙관리/범위
         트리가 렌더되지만 A/B 전용 컨트롤(대상 프로젝트 경로 입력 · 로컬 소스 트리)은 아예 없다(오해 방지).
      1) 관리창 자동 오픈: the separate "체커 규칙 관리" window is present (found by title in the same process).
      2) 자동매핑 없음(핵심): a rule file NAMED exactly like a checker key (RESOURCE_LEAK.md) sits in the library,
         yet EVERY 체커 매핑 ComboBox shows "— 없음 —" (nothing assigned) and _assignments.json does NOT exist.
         The 규칙 라이브러리 list shows that rule.
      3) 규칙 CRUD(생성): [새 규칙] → RuleNameBox/RuleEditor SetValue → [규칙 저장] → the new rule appears in
         RuleList and its "<이름>.md" file is created.
      4) 직접 지정: pick the new rule in EMPTY_CATCH_BLOCK's assignCombo (ExpandCollapse + SelectionItem) →
         [지정 저장] → _assignments.json records that checker → rule.
      5) 실행 부착: close the manager, back on the main window Invoke RunButton → in the output, ONLY the assigned
         checker (EMPTY_CATCH_BLOCK, multi-item) gets "## 매핑 규칙"; the key-named-but-UNASSIGNED checker
         (RESOURCE_LEAK) and the unrelated one (FORWARD_NULL) stay pure.
      6) 지정 기억: reopen the manager → EMPTY_CATCH_BLOCK's combo is pre-selected to the rule; RESOURCE_LEAK still
         "— 없음 —".
      7) 규칙 CRUD(삭제): create a throwaway rule, delete it, confirm RuleList shrinks and the file is gone.
      P) 실 규모 트리 UX(회귀 방지): the fixture now mirrors a REAL Sparrow xls — every 경로 shares a deep 6-level
         single-child prefix, under which sit 3 분기 폴더 / 9 디렉토리 / 13 파일 (한글 폴더 2개 · 30자 넘는 파일명 2개).
         Asserted: (1) the tree FOLDS the common prefix away — its ROOT nodes are the 분기 폴더, not 'D:', and the
         공통 경로 캡션(XlsScopeCommonPath) shows the folded path; (2) every TreeItem's UIA Name is the node's display
         name (NO 'SourceScopeNode' ToString fallback) and the 한글/긴 이름 are readable through UIA; (3) 가상화가 꺼져
         있어 전 노드가 자동화 트리에 나온다(리프 수 == 픽스처 파일 수); (4) 가로 스크롤이 필요 없고 보이는 노드가
         전부 트리 폭 안에 있다(이름 잘림·가로 스크롤 회귀 방지).
      X) XLS 범위 트리: the tree is built from the xls's OWN 경로 (ListPaths — no local source scan), so checking ONE
         folder node (ModuleA\core) and re-running exports EXACTLY that folder's items into a second output dir — even
         the SAME checker's items in other folders must not appear (경로 필터이지 체커 필터가 아님을 증명).
         요약(XlsScopeSummary)도 그 선택으로 갱신된다.
      T) 대분류 전환: SectionFixTab 을 SelectionItemPattern 으로 고르면 A/B 화면(대상 경로 + 로컬 소스 트리 + 규칙
         체크박스)이 나타나고, XLS 전용 컨트롤은 사라진다. [코드 자동수정] 화면의 하위 탭은 둘([코드 규칙]/
         [주석·레이아웃])이고 라벨/순서가 정확하다.
      U) 실사용자 언어 · 옵션 제거 · 커밋 안 함(핵심): 화면은 내부 트랙(A/B/C) 대신 하는 일로 부른다 —
         하위 탭은 [코드 규칙]/[주석·레이아웃], 실행 버튼은 '코드 규칙 수정 실행'/'주석·레이아웃 수정 실행',
         요약바는 '코드 규칙 · 선택 N개'. 두 화면 전체 텍스트(요소 Name + ValuePattern 값)에 'Track A'/'Track B'
         문구가 하나도 없어야 하고, 제거한 옵션 2종(DryRunCheck/IncludeGeneratedCheck)과 옵션 탭
         (OptionsTab)은 UIA 트리에 아예 없어야 한다(내부 식별자 TrackATab/TrackBTab 등은 그대로 유지되므로
         라벨은 id 로 찾아 Name 을 읽어 확인한다). 규칙별 커밋(CommitCheck)은 롤백 단위이자 러너 컴파일
         게이트의 전제라 유지한다 — A/B 화면에만 보이고 기본 꺼짐이며, 토글하면 요약이 커밋 모드로 바뀐다.
         마지막으로 소스 계약: GUI 는 커밋 체크 상태에 따라 -Commit / -NoCommit 을 넘기고
         (-DryRun/-IncludeGenerated 는 미전달) 실행 후 '…개 파일 수정됨 …' 안내를 출력하며,
         CLI 러너의 -Commit/-NoCommit/-DryRun/-VerifyCmd/-IncludeGenerated 는 전부 살아 있다.
      8) clean 종료 · 실 캐시(references\checkers) 미오염 (--guides-dir 임시 폴더 override, 전후 스냅샷 대조).
      9) 진단 로그: the GUI wrote a session transcript (with its 시작 헤더) and a machine-readable Track C run
         report whose numbers match what the run actually produced.
      L) 레이아웃 회귀(수치 단정): every key element renders non-degenerate (w>0/h>0/IsOffscreen=false), sits INSIDE
         its window's rectangle (= 잘림 없음), the 규칙 에디터 is at least $MIN_RULE_EDITOR_H tall, 목록/에디터
         rectangles do not intersect, and the 관리창 itself is at least ${MIN_MGR_W}x${MIN_MGR_H}.

      S) 창 스냅샷: the GUI, launched with --screenshot-dir, RENDERS ITS OWN WINDOWS to PNG (RenderTargetBitmap at
         the real DPI). At the same stages as the tree dumps this harness also drops a `capture.request` file, so the
         active window is photographed at that exact moment. Asserts ≥ $MIN_SHOTS valid PNGs (signature + IHDR,
         > $MIN_SHOT_BYTES bytes = not blank/transparent) whose pixel size matches the UIA window Rect (±$SHOT_SCALE_TOL).

    WHY the layout block exists: nobody can screenshot this WPF window from the outside (custom, non-installed exe →
    it cannot be put on an OS automation allow-list). So the UIA tree dump (per element: ControlType/AutomationId/
    Name/Rect/IsOffscreen/IsEnabled/Value) is the numeric EYES — 잘림, 0-크기, 화면 밖, 겹침 must all be decidable
    from numbers — and the app's OWN PNG snapshots are the literal eyes. Every run writes, under tests\_logs\uia-<stamp>\:
      result.log                     — the full PASS/FAIL transcript (same as console, with expected/actual values)
      tree-<n>-<stage>-iter<i>.txt   — a full UIA tree dump per stage (main loaded / manager open / rule saved /
                                       assignment saved / after run)
      shots\iter<i>\*.png            — the app's own window renders: automatic (main loaded / manager open / after
                                       run) + one per capture.request (req-main = XLS 분리 화면 / req-manager /
                                       req-assign-saved / req-xls-scoped = 범위 체크 상태 / req-fix-section = A/B 화면)
      gui-logs\                      — the app's OWN session transcript + Track C run report for the same run
      FAILURE-CONTEXT-iter<i>.txt    — only on failure: the failed checks + a fresh tree dump at that moment
    Only the newest $KEEP_UIA_LOGS run folders are kept (tests\_logs is gitignored).

    Runs the whole contract twice (-Iterations 2) for stability. Environment-unsupported (no .NET SDK / UIA / no
    desktop session) self-skips (not a failure). On a supported desktop it actually shows the windows briefly.
#>
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [int]$Iterations = 2,
    [string]$LogRoot = (Join-Path $PSScriptRoot "_logs")
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ---- 레이아웃 회귀 임계값 -------------------------------------------------
# 스크린샷 대신 이 수치들이 UI 품질의 판정 기준이다. 위반 시 실제 수치를 메시지에 담아 FAIL 한다.
$MIN_RULE_EDITOR_H = 120     # 규칙 에디터가 찌그러지지 않았는지(높이 하한)
$MIN_MGR_W         = 900     # 관리창 최소 너비
$MIN_MGR_H         = 560     # 관리창 최소 높이
$RECT_TOLERANCE    = 2       # 창 경계 포함 판정 허용 오차(px, 렌더 라운딩)
# ---- 트리 덤프/로그 회전 --------------------------------------------------
$MAX_TREE_NODES    = 4000    # 덤프 노드 상한(폭주 방지)
$MAX_TREE_DEPTH    = 40      # 덤프 깊이 상한
$VAL_TRUNC         = 120     # ValuePattern 값 절단 길이
$KEEP_UIA_LOGS     = 10      # tests\_logs\uia-* 보관 개수
# ---- 창 스냅샷(PNG) -------------------------------------------------------
# 앱이 --screenshot-dir 로 스스로 렌더한 실제 창 이미지. 트리 덤프가 '수치로 보는 눈'이라면 이건 '실제 눈'이다.
$MIN_SHOTS         = 5       # 반복당 최소 PNG 장수(XLS 화면 / 관리창 / 지정 저장 후 / 자동 지점들 / A·B 화면)
$MIN_SHOT_BYTES    = 10240   # 빈/투명/깨진 이미지 방어(정상 캡처는 수십 KB 이상)
$SHOT_TIMEOUT_SEC  = 20      # capture.request → PNG 생성 대기
$SHOT_SCALE_TOL    = 0.10    # PNG 픽셀 크기 vs UIA 창 Rect 허용 오차(±10%)
# UIA BoundingRectangle 도 물리 픽셀이므로 정상 구현이면 비율 1.0 이다(앱이 실제 DPI 로 렌더하니 배율이 상쇄된다).
# 96 고정 렌더 회귀는 HiDPI 에서 비율이 1/배율 로 떨어져 걸린다. HiDPI 데스크톱에서 UIA 가 논리 좌표를 주는
# 환경도 있으므로 시스템 DPI 배율(AppliedDPI/96)도 허용값에 넣는다.
$SHOT_SCALE_EXPECTED = @(1.0)
try {
    $appliedDpi = (Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI
    if ($appliedDpi -and ([double]$appliedDpi) -gt 0) {
        $s = [double]$appliedDpi / 96.0
        if ([math]::Abs($s - 1.0) -gt 0.01) { $SHOT_SCALE_EXPECTED += $s }
    }
} catch { }

# ---- self-skip guards -----------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Host "dotnet SDK not found; skipping GUI UIA tests."; $global:SparrowTestSkip = "dotnet SDK 없음"; return }

try {
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
    Add-Type -AssemblyName WindowsBase -ErrorAction Stop
}
catch {
    Write-Host "UI Automation assemblies unavailable; skipping GUI UIA tests. ($($_.Exception.Message))"
    $global:SparrowTestSkip = "UI Automation 어셈블리 없음"
    return
}

$guiProj = Join-Path $RepositoryRoot 'tools\SparrowRunner.Gui\SparrowRunner.Gui.csproj'
$fixProj = Join-Path $RepositoryRoot 'tests\SparrowGuiUiaFixture\SparrowGuiUiaFixture.csproj'
$realCache = Join-Path $RepositoryRoot 'references\checkers'
foreach ($p in @($guiProj, $fixProj)) { if (-not (Test-Path -LiteralPath $p)) { throw "missing project: $p" } }

# Checker keys the fixture emits, mapped to their roles in the NEW (assignment) model.
$K_ASSIGN   = 'EMPTY_CATCH_BLOCK'   # 5 items — we create a rule and assign it here (multi-item attach proof)
$K_PURE     = 'FORWARD_NULL'        # 4 items — no rule, stays pure
$K_KEYNAMED = 'RESOURCE_LEAK'       # 4 items — a library rule is NAMED like this key but is NEVER assigned
$NONE       = '— 없음 —'            # assignCombo sentinel = unassigned

$NEW_RULE   = '빈catch조치'          # created via CRUD, assigned to $K_ASSIGN
$NEW_MARK   = '빈 catch 를 남기지 않는다'
$TMP_RULE   = '임시규칙'             # created then deleted (CRUD delete proof)

# 픽스처의 검출 경로(실 xls 모사): 모두가 깊은 공통 접두를 공유하고, 그 아래 분기 폴더 3개로 갈라진다.
#   <접두>\ModuleA\core     : EMPTY_CATCH_BLOCK x3 (Alpha.cs · Beta.cs · WndLongSampleAnalyzeControlView.cs)
#   <접두>\ModuleA\ui       : FORWARD_NULL x1 (Gamma.cs) + RESOURCE_LEAK x1 (Delta.cs)
#   <접두>\ModuleB\src       : FORWARD_NULL x2 (Epsilon.cs · SampleDrawObjectRendererView.cs) + RESOURCE_LEAK x1
#   <접두>\ModuleB\test      : EMPTY_CATCH_BLOCK x1 (Eta.cpp)          ← 선택 폴더 밖의 같은 체커
#   <접두>\공통모듈\util    : RESOURCE_LEAK x1 + FORWARD_NULL x1
#   <접두>\공통모듈\한글폴더 : EMPTY_CATCH_BLOCK x1 + RESOURCE_LEAK x1
$FIX_PREFIX     = 'D:\Work\Proj\branches\Proj\release\2026-01-01'   # 트리가 접어야 하는 공통 접두(6단계)
$FIX_BRANCHES   = @('ModuleA', 'ModuleB', '공통모듈')                   # 접은 뒤 트리 루트가 되어야 하는 실제 분기 폴더
$FIX_FILES      = 13                 # 픽스처 파일(=트리 리프) 수
$FIX_DIRS       = 9                  # 접두 아래 디렉토리 수(분기 3 + 하위 6)
$FIX_KOREAN_DIR = '한글폴더'          # UIA 로 실제 이름이 읽혀야 하는 한글 폴더
$FIX_LONG_FILE  = 'WndLongSampleAnalyzeControlView.cs'   # 30자 넘는 파일명(말줄임 대상)
$SCOPE_KEEP_DIR = 'core'             # 이 폴더 노드만 체크해 실행 → 이 폴더 항목만 나와야 한다
$SCOPE_DROP_DIR = 'ui'               # 체크하지 않은 폴더
$SCOPE_KEEP_N   = 3                  # 그 폴더의 파일 수 = 검출 건수 = 범위 실행 산출 md 수
$MD_TOTAL       = 13                 # 전건 실행 산출 md 수(= 픽스처 행 수)
$MD_ASSIGN      = 5                  # 규칙을 지정한 체커($K_ASSIGN)의 항목 수(다건 부착 증명)

# ---- 하위 탭 라벨 ------------------------------------------------------------
$SUB_TABS        = @('코드 규칙', '주석·레이아웃')  # [코드 자동수정] 하위 탭 라벨(순서 포함)
# NOTE: 한글은 PowerShell 이 변수명 문자로 취급한다 — "$FIX_FILES개" 는 $FIX_FILES개 라는 (없는) 변수가 되므로
# 한글이 바로 붙는 자리에서는 반드시 ${VAR} 로 감싼다.
$XLS_TOTAL_TEXT = "전체 ${FIX_FILES}개 파일"   # 선택 없음(전건) 상태의 요약 문구

# ---- 진단 산출 폴더 -------------------------------------------------------
# 실패해도 증거가 남아야 하므로 스크립트 시작 시점에 만든다. 회전은 이름(=시각) 정렬로 결정적.
$runStamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$runDir   = Join-Path $LogRoot ("uia-" + $runStamp)
$guiLogDir = Join-Path $runDir 'gui-logs'
$shotsRoot = Join-Path $runDir 'shots'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $guiLogDir | Out-Null
New-Item -ItemType Directory -Force -Path $shotsRoot | Out-Null
$resultLog = Join-Path $runDir 'result.log'
$Utf8Bom = New-Object System.Text.UTF8Encoding($true)   # 한글 로그를 메모장/PS5.1 이 바르게 읽도록 BOM
[System.IO.File]::WriteAllText($resultLog, "UIA 진단 로그 · $runStamp`r`n", $Utf8Bom)

# 회전: 최신 $KEEP_UIA_LOGS 개의 uia-* 폴더만 남긴다(이번 실행 폴더 포함).
try {
    $old = @(Get-ChildItem -LiteralPath $LogRoot -Directory -Filter 'uia-*' -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending | Select-Object -Skip $KEEP_UIA_LOGS)
    foreach ($d in $old) { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }
} catch { }

# 콘솔 + result.log 동시 기록(콘솔 출력은 기존과 동일하게 유지).
function Log($text) {
    Write-Host $text
    try { [System.IO.File]::AppendAllText($resultLog, [string]$text + "`r`n", $Utf8Bom) } catch { }
}

# ---- UIA helpers ----------------------------------------------------------
$AProp    = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
$NameProp = [System.Windows.Automation.AutomationElement]::NameProperty
$CTProp   = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
$PidProp  = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$ListItemCT = [System.Windows.Automation.ControlType]::ListItem
$Desc    = [System.Windows.Automation.TreeScope]::Descendants
$ChildTS = [System.Windows.Automation.TreeScope]::Children

function New-IdCondition($id) { New-Object System.Windows.Automation.PropertyCondition $AProp, $id }
function UIA-FindAll($root, $id) {
    if (-not $root) { return @() }
    return @($root.FindAll($Desc, (New-IdCondition $id)))
}
function UIA-First($root, $id) {
    if (-not $root) { return $null }
    return $root.FindFirst($Desc, (New-IdCondition $id))
}
function UIA-FirstName($root, $id) {
    $e = UIA-First $root $id
    if ($e) { return [string]$e.Current.Name }
    return ''
}
function UIA-SetValue($e, $text) {
    $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$vp).SetValue($text)
}
function UIA-Invoke($e) {
    $ip = $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$ip).Invoke()
}
function Get-WindowByTitle($procId, $title, $timeoutSec) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition $PidProp, $procId
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $wins = @($root.FindAll($ChildTS, $cond))
        foreach ($w in $wins) { if ([string]$w.Current.Name -eq $title) { return $w } }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
# An OWNED window (RuleManagerWindow, MessageBox) is a child of its owner in the UIA tree — NOT a child of the
# desktop root. Find it by scanning the owner's Window children by title.
function Get-OwnedWindow($owner, $title, $timeoutSec) {
    if (-not $owner) { return $null }
    $winCT = [System.Windows.Automation.ControlType]::Window
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in @($owner.FindAll($ChildTS, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $winCT)))) {
            if ([string]$w.Current.Name -eq $title) { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Wait-For([scriptblock]$cond, $timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try { if (& $cond) { return $true } } catch { }
        Start-Sleep -Milliseconds 250
    }
    return $false
}
# Names of the ListItem children of a container element (RuleList items expose Name = rule name).
function Get-ListItemNames($container) {
    if (-not $container) { return @() }
    $items = @($container.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT)))
    return @($items | ForEach-Object { [string]$_.Current.Name })
}
# --- 대분류(섹션) 탭 / 범위 트리 노드 -------------------------------------
# TabItem 은 SelectionItemPattern 을 노출한다: 선택 여부 읽기 + 프로그램적 선택(화면 전환).
function UIA-IsSelected($e) {
    if (-not $e) { return $false }
    try {
        $sip = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        return [bool]([System.Windows.Automation.SelectionItemPattern]$sip).Current.IsSelected
    } catch { return $false }
}
function UIA-SelectItem($e) {
    if (-not $e) { return $false }
    try {
        $sip = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        ([System.Windows.Automation.SelectionItemPattern]$sip).Select()
        return $true
    } catch { return $false }
}
# 범위 트리(로컬 소스 / xls 경로 공용 템플릿)의 노드 체크박스: AutomationId='scopeNodeCheck', Name=노드 이름.
function Get-ScopeNode($win, $name) {
    foreach ($n in @(UIA-FindAll $win 'scopeNodeCheck')) {
        if ([string]$n.Current.Name -eq $name) { return $n }
    }
    return $null
}
function Get-ScopeNodeNames($win) {
    return @(@(UIA-FindAll $win 'scopeNodeCheck') | ForEach-Object { [string]$_.Current.Name })
}
function UIA-Toggle($e) {
    if (-not $e) { return $false }
    try {
        $tp = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        ([System.Windows.Automation.TogglePattern]$tp).Toggle()
        return $true
    } catch { return $false }
}
# --- 트리 노드(TreeItem) 자체를 읽는다 ------------------------------------
# scopeNodeCheck(체크박스)가 아니라 TreeItem 을 보는 이유: 스크린리더/자동화가 실제로 읽는 이름은 TreeItem 의
# Name 이고, AutomationProperties.Name 을 주지 않으면 그게 ToString() 폴백(=클래스명)이 된다.
# TabControl 의 하위 탭 라벨(순서대로). 탭 헤더는 TabControl 의 직계 자식이므로 Children 스코프로 읽는다.
$TabItemCT = [System.Windows.Automation.ControlType]::TabItem
function Get-TabItemNames($tabControl) {
    if (-not $tabControl) { return @() }
    $items = @($tabControl.FindAll($ChildTS, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $TabItemCT)))
    return @($items | ForEach-Object { [string]$_.Current.Name })
}
$TreeItemCT = [System.Windows.Automation.ControlType]::TreeItem
function Get-TreeItems($root) {
    if (-not $root) { return @() }
    return @($root.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $TreeItemCT)))
}
function Get-TreeRootItems($tree) {
    if (-not $tree) { return @() }
    return @($tree.FindAll($ChildTS, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $TreeItemCT)))
}
function Get-TreeItemNames($root) {
    return @((Get-TreeItems $root) | ForEach-Object { [string]$_.Current.Name })
}
# 자식 TreeItem 이 없는 노드 = 파일 리프.
function Get-TreeLeafNames($tree) {
    $out = @()
    foreach ($it in (Get-TreeItems $tree)) {
        if ((Get-TreeItems $it).Count -eq 0) { $out += [string]$it.Current.Name }
    }
    return @($out)
}
# 접힌 노드를 전부 펼친다(가상화가 꺼져 있어도 '펼치지 않은' 하위는 컨테이너가 생성되지 않는다).
function Expand-AllTreeItems($tree, [int]$maxPasses = 6) {
    $collapsed = [System.Windows.Automation.ExpandCollapseState]::Collapsed
    for ($p = 0; $p -lt $maxPasses; $p++) {
        $any = $false
        foreach ($it in (Get-TreeItems $tree)) {
            try {
                $ec = $it.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                if (([System.Windows.Automation.ExpandCollapsePattern]$ec).Current.ExpandCollapseState -eq $collapsed) {
                    ([System.Windows.Automation.ExpandCollapsePattern]$ec).Expand()
                    $any = $true
                }
            } catch { }
        }
        if (-not $any) { return }
        Start-Sleep -Milliseconds 200
    }
}
# 가로 스크롤이 필요한 상태인가. ScrollPattern 이 없으면 스크롤 자체가 불가 = 가로 스크롤 없음.
function Get-HorizontallyScrollable($e) {
    if (-not $e) { return $false }
    try {
        $sp = $null
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)) { return $false }
        return [bool]([System.Windows.Automation.ScrollPattern]$sp).Current.HorizontallyScrollable
    } catch { return $false }
}
# $names 중 '$prefix...' 로 시작하는 것이 하나라도 있는가(노드 이름은 "이름  (N건)" 형태라 접두 비교).
function Name-StartsAny($names, $prefix) {
    return (@($names | Where-Object { $_ -like ($prefix + '*') }).Count -ge 1)
}
# 창에서 사용자 눈에 닿는 모든 문자열(요소 Name + ValuePattern 값)을 모은다. "화면에 이 문구가 없다"는
# 특정 요소 하나로는 판정할 수 없다 — 탭 헤더·버튼 라벨·안내문·요약·로그가 전부 다른 요소이기 때문이다.
function Get-AllVisibleText($root) {
    if (-not $root) { return @() }
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($root.FindAll($Desc, [System.Windows.Automation.Condition]::TrueCondition))) {
        try { $n = [string]$e.Current.Name; if ($n) { [void]$texts.Add($n) } } catch { }
        try {
            $vp = $null
            if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                $v = [string]([System.Windows.Automation.ValuePattern]$vp).Current.Value
                if ($v) { [void]$texts.Add($v) }
            }
        } catch { }
    }
    return @($texts)
}
# $needle 을 포함하는 문자열들(대소문자 무시). 실패 메시지에 "무엇이 걸렸는지" 그대로 싣기 위해 값을 돌려준다.
function Find-TextMatches($texts, $needle) {
    return @($texts | Where-Object { $_ -like ('*' + $needle + '*') })
}
# "이 화면에 없다" 판정: 요소가 아예 없거나(다른 대분류에 속해 시각 트리에 없음) 화면 밖/0크기다.
function Element-Absent($win, $id) {
    $e = UIA-First $win $id
    if (-not $e) { return $true }
    $r = Get-RectInfo $e
    if ($null -eq $r) { return $true }
    return ($r.Off -or $r.Empty -or ($r.W -le 0) -or ($r.H -le 0))
}

# The assignCombo (ComboBox) whose AutomationProperties.Name == checker key.
function Get-AssignCombo($win, $key) {
    foreach ($c in @(UIA-FindAll $win 'assignCombo')) {
        if ([string]$c.Current.Name -eq $key) { return $c }
    }
    return $null
}
# Read a ComboBox's current selection by expanding it and finding the selected ListItem (robust for WPF combos).
function Get-ComboValue($combo) {
    if (-not $combo) { return $null }
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    ([System.Windows.Automation.ExpandCollapsePattern]$ec).Expand()
    Wait-For { @($combo.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT))).Count -ge 1 } 4 | Out-Null
    $val = $null
    foreach ($it in @($combo.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT)))) {
        try {
            $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if (([System.Windows.Automation.SelectionItemPattern]$sip).Current.IsSelected) { $val = [string]$it.Current.Name; break }
        } catch { }
    }
    try { ([System.Windows.Automation.ExpandCollapsePattern]$ec).Collapse() } catch { }
    return $val
}
# Select a value in a ComboBox by expanding and choosing the matching ListItem (ExpandCollapse + SelectionItem).
function Set-ComboValue($combo, $value) {
    if (-not $combo) { return $false }
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    ([System.Windows.Automation.ExpandCollapsePattern]$ec).Expand()
    Wait-For { @($combo.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT))).Count -ge 1 } 4 | Out-Null
    foreach ($it in @($combo.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT)))) {
        if ([string]$it.Current.Name -eq $value) {
            $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            ([System.Windows.Automation.SelectionItemPattern]$sip).Select()
            return $true
        }
    }
    try { ([System.Windows.Automation.ExpandCollapsePattern]$ec).Collapse() } catch { }
    return $false
}
function Close-Window($win) {
    try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        ([System.Windows.Automation.WindowPattern]$wp).Close()
        return $true
    } catch { return $false }
}
# A modal MessageBox owned by $owner is a Window child of $owner in the UIA tree. Find it by caption and click its
# Yes button (Korean '예(&Y)' -> UIA Name usually '예…', English fallback 'Yes').
function Click-ConfirmYes($owner, $caption, $timeoutSec) {
    if (-not $owner) { return $false }
    $winCT = [System.Windows.Automation.ControlType]::Window
    $btnCT = [System.Windows.Automation.ControlType]::Button
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in @($owner.FindAll($ChildTS, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $winCT)))) {
            if ([string]$w.Current.Name -ne $caption) { continue }
            foreach ($b in @($w.FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $btnCT)))) {
                $n = [string]$b.Current.Name
                if ($n.StartsWith('예') -or $n -eq 'Yes') { try { UIA-Invoke $b; return $true } catch { } }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# ---- geometry (스크린샷 대체: UI 품질을 수치로 판정) ----------------------
# 요소의 사각형 + 가시/활성 상태. Rect.Empty(렌더 안 됨)는 0크기로 정규화해 단정에서 걸리게 한다.
function Get-RectInfo($e) {
    if (-not $e) { return $null }
    $r = $null
    try { $r = $e.Current.BoundingRectangle } catch { return $null }
    $off = $true; $en = $false
    try { $off = [bool]$e.Current.IsOffscreen } catch { }
    try { $en  = [bool]$e.Current.IsEnabled }   catch { }
    if ($null -eq $r -or $r.IsEmpty) {
        return [pscustomobject]@{ X = 0; Y = 0; W = 0; H = 0; Empty = $true; Off = $off; En = $en }
    }
    return [pscustomobject]@{
        X = [int][math]::Round($r.X); Y = [int][math]::Round($r.Y)
        W = [int][math]::Round($r.Width); H = [int][math]::Round($r.Height)
        Empty = $false; Off = $off; En = $en
    }
}
function Format-Rect($r) {
    if (-not $r) { return '(없음)' }
    if ($r.Empty) { return 'Rect(empty)' }
    return ("Rect({0},{1},{2},{3})" -f $r.X, $r.Y, $r.W, $r.H)
}
# 자식 사각형이 부모(창) 사각형 안에 있는가 = 잘림 없음. 렌더 라운딩만큼 허용.
function Rect-Inside($outer, $inner, $tol) {
    if (-not $outer -or -not $inner) { return $false }
    if ($outer.Empty -or $inner.Empty) { return $false }
    return (($inner.X -ge ($outer.X - $tol)) -and
            ($inner.Y -ge ($outer.Y - $tol)) -and
            (($inner.X + $inner.W) -le ($outer.X + $outer.W + $tol)) -and
            (($inner.Y + $inner.H) -le ($outer.Y + $outer.H + $tol)))
}
# 두 사각형이 실제로 겹치는가(경계 접촉은 겹침 아님).
function Rect-Overlaps($a, $b) {
    if (-not $a -or -not $b) { return $false }
    if ($a.Empty -or $b.Empty) { return $false }
    $noOverlap = (($a.X + $a.W) -le $b.X) -or (($b.X + $b.W) -le $a.X) -or
                 (($a.Y + $a.H) -le $b.Y) -or (($b.Y + $b.H) -le $a.Y)
    return (-not $noOverlap)
}

# ---- UIA 트리 덤프 (AI 의 '눈') ------------------------------------------
# 한 줄 = 한 요소: ControlType | AutomationId | Name | Rect(x,y,w,h) | Off=bool | En=bool | Val="..."
function Format-UiaNode($e) {
    $ct = '?'
    try { $ct = ([string]$e.Current.ControlType.ProgrammaticName) -replace '^ControlType\.', '' } catch { }
    $id = ''; try { $id = [string]$e.Current.AutomationId } catch { }
    $nm = ''; try { $nm = [string]$e.Current.Name } catch { }
    $r = Get-RectInfo $e
    $val = $null
    try {
        $vpObj = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vpObj)) {
            $val = [string]([System.Windows.Automation.ValuePattern]$vpObj).Current.Value
        }
    } catch { }
    $nm = ($nm -replace "`r`n", '\n') -replace "`n", '\n'
    if ($nm.Length -gt $VAL_TRUNC) { $nm = $nm.Substring(0, $VAL_TRUNC) + '…' }
    $valText = ''
    if ($null -ne $val) {
        $val = ($val -replace "`r`n", '\n') -replace "`n", '\n'
        if ($val.Length -gt $VAL_TRUNC) { $val = $val.Substring(0, $VAL_TRUNC) + '…' }
        $valText = ' | Val="' + $val + '"'
    }
    $off = 'true'; $en = 'false'
    if ($r) { if (-not $r.Off) { $off = 'false' }; if ($r.En) { $en = 'true' } }
    return ("{0} | id={1} | name={2} | {3} | Off={4} | En={5}{6}" -f $ct, $id, $nm, (Format-Rect $r), $off, $en, $valText)
}
$script:dumpNodes = 0
function Dump-UiaNode($walker, $e, [int]$depth, $sb) {
    if (-not $e) { return }
    if ($script:dumpNodes -ge $MAX_TREE_NODES) { return }
    $script:dumpNodes++
    [void]$sb.AppendLine(('  ' * $depth) + (Format-UiaNode $e))
    if ($depth -ge $MAX_TREE_DEPTH) { return }
    $child = $null
    try { $child = $walker.GetFirstChild($e) } catch { return }
    while ($child) {
        Dump-UiaNode $walker $child ($depth + 1) $sb
        if ($script:dumpNodes -ge $MAX_TREE_NODES) { break }
        try { $child = $walker.GetNextSibling($child) } catch { break }
    }
}
# 트리 덤프를 문자열로 만든다(파일로도 쓰고, FAILURE-CONTEXT 에도 그대로 끼워 넣는다).
function Build-UiaTreeText($root, $title) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# UIA 트리 덤프 — $title")
    [void]$sb.AppendLine("# 시각: " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))
    [void]$sb.AppendLine('# 형식: ControlType | id=AutomationId | name=Name | Rect(x,y,w,h) | Off=IsOffscreen | En=IsEnabled | Val="ValuePattern 값"')
    [void]$sb.AppendLine("# Rect 는 화면 좌표(px). w/h=0 또는 Off=true 는 렌더되지 않은(또는 잘린) 요소다.")
    [void]$sb.AppendLine('')
    if (-not $root) {
        [void]$sb.AppendLine('(요소 없음 — 창이 이미 닫혔거나 UIA 접근 불가)')
        return $sb.ToString()
    }
    $script:dumpNodes = 0
    Dump-UiaNode ([System.Windows.Automation.TreeWalker]::ControlViewWalker) $root 0 $sb
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("# 노드 수: " + $script:dumpNodes)
    return $sb.ToString()
}
function Write-UiaTree($root, $title, $fileName) {
    $text = Build-UiaTreeText $root $title
    $path = Join-Path $runDir $fileName
    try { [System.IO.File]::WriteAllText($path, $text, $Utf8Bom) } catch { }
    Log ("  [dump] $fileName  (" + $script:dumpNodes + " nodes)")
    return $path
}

# ---- 창 스냅샷 (앱이 스스로 렌더한 실제 이미지) ---------------------------
# GUI 를 --screenshot-dir 로 띄우면 자동 지점(메인창 로드/관리창 오픈/실행 완료)에서 스스로 PNG 를 남기고,
# 그 폴더에 capture.request 를 만들면 활성 창을 즉시 캡처한다. 여기서는 트리 덤프와 같은 단계에서 요청을 넣어
# "그 순간의 화면"을 확보한다. 앱이 .tmp → rename 으로 쓰므로 파일이 보이면 완성본이다.
#
# PNG 헤더(시그니처 + IHDR)를 직접 읽는다: 이미지 라이브러리 없이 유효성/픽셀 크기를 판정하기 위해서다.
function Get-PngInfo($path) {
    if (-not $path) { return $null }
    try {
        $buf = New-Object byte[] 33
        $read = 0
        $fs = New-Object System.IO.FileStream $path, ([System.IO.FileMode]::Open), ([System.IO.FileAccess]::Read), ([System.IO.FileShare]::ReadWrite)
        try { $read = $fs.Read($buf, 0, $buf.Length) } finally { $fs.Dispose() }
        if ($read -lt 24) { return $null }
        # PNG 시그니처 89 50 4E 47 0D 0A 1A 0A
        if ($buf[0] -ne 0x89 -or $buf[1] -ne 0x50 -or $buf[2] -ne 0x4E -or $buf[3] -ne 0x47) { return $null }
        $w = ([int]$buf[16] * 16777216) + ([int]$buf[17] * 65536) + ([int]$buf[18] * 256) + [int]$buf[19]
        $h = ([int]$buf[20] * 16777216) + ([int]$buf[21] * 65536) + ([int]$buf[22] * 256) + [int]$buf[23]
        $len = 0
        try { $len = (Get-Item -LiteralPath $path).Length } catch { }
        return [pscustomobject]@{ W = $w; H = $h; Bytes = $len }
    }
    catch { return $null }
}
# 비율이 허용 배율($SHOT_SCALE_EXPECTED) 중 하나와 ±$SHOT_SCALE_TOL 안에서 일치하는가.
function Shot-ScaleOk([double]$ratio) {
    foreach ($e in $SHOT_SCALE_EXPECTED) {
        if ([math]::Abs($ratio - [double]$e) -le ([double]$e * $SHOT_SCALE_TOL)) { return $true }
    }
    return $false
}
# 앱은 "활성 창"을 찍는다. 그런데 관리창이 열려 있는 동안 SetFocus 한 번으로는 소유 창(메인)이 실제 전면이
# 되지 않는 경우가 있어(활성화 경쟁), 엉뚱한 창이 찍혀 PNG-창 Rect 대조가 간헐적으로 어긋났다.
# 그래서 Win32 GetForegroundWindow 로 "정말 그 창이 전면인가"를 확인할 때까지 SetFocus 를 반복한다.
# Add-Type 이 막힌 환경이면 확인 없이 기존 동작(SetFocus 한 번)으로 진행한다.
$script:HasForegroundApi = $false
try {
    Add-Type -Namespace SparrowUia -Name Win32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();' -ErrorAction Stop
    $script:HasForegroundApi = $true
}
catch { }
# 주의: SetFocus 는 대상 앱의 UI 스레드로 마샬링되는 호출이다. 짧은 간격으로 연타하면(250ms x 수십 회)
# 소유창/피소유창 사이 활성화가 튕기면서 앱의 디스패처가 멎는 것을 실측했다(창 프레임만 남고 WPF 내용이
# UIA 트리에서 사라짐). 그래서 '드물게 · 몇 번만' 시도하고, 안 되면 경고만 남긴다.
function Wait-ForActiveWindow($element, [int]$attempts = 3) {
    if (-not $element) { return $false }
    $hwnd = [IntPtr]::Zero
    if ($script:HasForegroundApi) {
        try { $hwnd = [IntPtr][int]$element.Current.NativeWindowHandle } catch { }
    }
    for ($i = 1; $i -le $attempts; $i++) {
        try { $element.SetFocus() } catch { }
        Start-Sleep -Milliseconds (300 * $i)
        if ($hwnd -eq [IntPtr]::Zero) { return $true }   # 확인 수단 없음 → 기존 동작(판정은 PNG 대조가 한다)
        if ([SparrowUia.Win32]::GetForegroundWindow() -eq $hwnd) { return $true }
    }
    return $false
}
# 요청 기반 캡처. $focusElement 를 주면 그 창이 실제 전면이 될 때까지 기다린다(앱은 활성 창을 찍는다).
# 접미사($suffix)는 파일명에 그대로 들어가므로 그 이름을 가진 새 PNG 만 이 요청의 결과로 인정한다(자동 캡처와 혼동 방지).
# 타임아웃은 경고만 남긴다(장수·유효성 단정이 최종 판정을 한다).
function Request-Snapshot($dir, $suffix, [int]$timeoutSec, $focusElement) {
    if (-not $dir) { return $null }
    if ($focusElement) {
        if (-not (Wait-ForActiveWindow $focusElement 3)) {
            Log "  [warn] 캡처 대상 창을 전면으로 만들지 못했습니다(suffix=$suffix) — 다른 창이 찍힐 수 있습니다"
        }
    }
    $before = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter '*.png' -File -ErrorAction SilentlyContinue)) { $before[$f.Name] = $true }
    $req = Join-Path $dir 'capture.request'
    try { [System.IO.File]::WriteAllText($req, [string]$suffix, (New-Object System.Text.UTF8Encoding($false))) }
    catch { Log "  [warn] capture.request 생성 실패: $($_.Exception.Message)"; return $null }

    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter '*.png' -File -ErrorAction SilentlyContinue)) {
            if ($before.ContainsKey($f.Name)) { continue }
            if ($f.Name -notlike "*-$suffix-*") { continue }
            $info = Get-PngInfo $f.FullName
            $px = '판독 불가'
            if ($info) { $px = [string]$info.W + "x" + [string]$info.H + "px" }
            Log ("  [shot] " + $f.Name + "  (" + $f.Length + " bytes · " + $px + ")")
            return $f.FullName
        }
        Start-Sleep -Milliseconds 250
    }
    Log "  [warn] 요청 기반 스냅샷 타임아웃(suffix=$suffix, ${timeoutSec}s) — capture.request 가 처리되지 않았습니다"
    try { Remove-Item -LiteralPath $req -Force -ErrorAction SilentlyContinue } catch { }
    return $null
}
# PNG 픽셀 크기가 UIA 가 보고한 창 Rect 와 대략 일치하는가(잘못된 스케일로 렌더되는 회귀 방지).
function Check-ShotScale($shot, $rect, $label) {
    $info = Get-PngInfo $shot
    if (-not $shot -or -not $info -or -not $rect -or $rect.Empty -or $rect.W -le 0 -or $rect.H -le 0) {
        Check "S) $label PNG-창 Rect 대조 가능(PNG=$(if($shot){Split-Path $shot -Leaf}else{'없음'}) · 창 $(Format-Rect $rect))" { $false }
        return
    }
    $rx = [double]$info.W / [double]$rect.W
    $ry = [double]$info.H / [double]$rect.H
    Check ("S) $label PNG 픽셀 크기 ≈ UIA 창 Rect (PNG " + $info.W + "x" + $info.H + " / 창 " + $rect.W + "x" + $rect.H +
           " · 비율 " + [math]::Round($rx, 3) + "/" + [math]::Round($ry, 3) + ")") {
        (Shot-ScaleOk $rx) -and (Shot-ScaleOk $ry)
    }
}

# ---- assert harness -------------------------------------------------------
$failures = @()
function Check($name, [scriptblock]$cond) {
    try { if (& $cond) { Log "  [ok]   $name" } else { $script:failures += $name; Log "  [FAIL] $name" } }
    catch { $script:failures += "$name ($($_.Exception.Message))"; Log "  [FAIL] $name ($($_.Exception.Message))" }
}

# 요소 하나에 대한 레이아웃 단정 2종: (1) 비퇴화 렌더(w>0,h>0,Off=false) (2) 창 경계 내(잘림 없음).
function Check-ElementLayout($win, $winLabel, $el, $elLabel) {
    $wr = Get-RectInfo $win
    $r  = Get-RectInfo $el
    Check "L) $winLabel/$elLabel 렌더 정상(w>0,h>0,Off=false) (실제: $(Format-Rect $r) Off=$(if($r){$r.Off}else{'?'}))" {
        ($null -ne $r) -and (-not $r.Empty) -and ($r.W -gt 0) -and ($r.H -gt 0) -and (-not $r.Off)
    }
    Check "L) $winLabel/$elLabel 창 경계 내(잘림 없음) (요소 $(Format-Rect $r) ⊂ 창 $(Format-Rect $wr))" {
        Rect-Inside $wr $r $RECT_TOLERANCE
    }
}
# id 로 찾아 존재 단정 + 레이아웃 단정. 반환: 찾은 요소(없으면 $null).
function Check-IdLayout($win, $winLabel, $id) {
    $el = UIA-First $win $id
    Check "L) $winLabel/$id 요소 존재" { $null -ne $el }
    if ($el) { Check-ElementLayout $win $winLabel $el $id }
    return $el
}

function Snapshot-Dir($d) {
    $m = @{}
    if (-not (Test-Path -LiteralPath $d)) { return $m }
    foreach ($f in @(Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue)) {
        $m[$f.FullName] = ([string]$f.Length + ':' + $f.LastWriteTimeUtc.Ticks)
    }
    return $m
}
function Snapshots-Equal($a, $b) {
    if ($a.Count -ne $b.Count) { return $false }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { return $false }; if ($a[$k] -ne $b[$k]) { return $false } }
    return $true
}

# ---- build tool + fixture -------------------------------------------------
Log "  building SparrowRunner.Gui + GuiUiaFixture (Release)..."
& $dotnet.Source build $guiProj -c Release -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "SparrowRunner.Gui build failed" }
& $dotnet.Source build $fixProj -c Release -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "GuiUiaFixture build failed" }

$exe    = Join-Path $RepositoryRoot 'tools\SparrowRunner.Gui\bin\Release\net8.0-windows\SparrowRunner.Gui.exe'
$fixExe = Join-Path $RepositoryRoot 'tests\SparrowGuiUiaFixture\bin\Release\net8.0\GuiUiaFixture.exe'
if (-not (Test-Path -LiteralPath $exe))    { throw "GUI exe not found: $exe" }
if (-not (Test-Path -LiteralPath $fixExe)) { throw "fixture exe not found: $fixExe" }

Log "  진단 로그 폴더: $runDir"
$cacheBefore = Snapshot-Dir $realCache

function Invoke-OneIteration([int]$iter) {
    Log ""
    Log "=== iteration $iter ==="
    $failuresAtStart = $script:failures.Count
    $work   = Join-Path $env:TEMP ("sparrow-gui-uia-" + [guid]::NewGuid().ToString("N"))
    $guides = Join-Path $work 'guides'
    $out    = Join-Path $work 'out'
    New-Item -ItemType Directory -Force -Path $guides | Out-Null
    # NOTE: $out is NOT pre-created — Run creates it.
    $xls = Join-Path $work 'fixture.xls'
    $assignJson = Join-Path $guides '_assignments.json'
    # 앱 자신의 세션 로그/Track C 리포트도 이 실행의 증거로 함께 수집한다(반복별 폴더).
    $iterGuiLogDir = Join-Path $guiLogDir ("iter" + $iter)
    New-Item -ItemType Directory -Force -Path $iterGuiLogDir | Out-Null
    # 창 스냅샷도 반복별 폴더로 분리한다(이전 반복의 잔여 capture.request 가 다음 반복을 건드리지 않게).
    $iterShotDir = Join-Path $shotsRoot ("iter" + $iter)
    New-Item -ItemType Directory -Force -Path $iterShotDir | Out-Null

    $proc = $null
    $main = $null
    $mgr = $null
    $shotMain = $null; $shotMgr = $null; $shotAssign = $null; $shotScoped = $null; $shotFix = $null
    $mainRectForShot = $null; $mgrRectForShot = $null
    try {
        & $fixExe $xls | Out-Null
        if (-not (Test-Path -LiteralPath $xls)) { throw "fixture xls not generated" }

        # Preseed the LIBRARY with a rule NAMED exactly like a checker key ($K_KEYNAMED), WITHOUT any assignment.
        # UTF-8 BOM + internal '## 근거' header exercise the BOM strip / section-boundary handling on read.
        $keyNamedBody = "규칙: 자원을 using 으로 감싼다.`r`n`r`n## 근거`r`nSparrow 권장.`r`n"
        [System.IO.File]::WriteAllText((Join-Path $guides "$K_KEYNAMED.md"), $keyNamedBody, (New-Object System.Text.UTF8Encoding($true)))

        # ---- launch GUI: xls/out prefilled + --open-rule-manager (auto-open the manager window) ----
        # --log-dir 로 앱의 세션 로그/실행 리포트를 이 진단 폴더로 유도한다(실 %LOCALAPPDATA% 오염 방지 + 증거 동봉).
        # --screenshot-dir 로 앱이 스스로 자기 창을 PNG 로 렌더하게 한다(자동 지점 + capture.request 요청 캡처).
        $guiArgs = @('--trackc-xls', $xls, '--trackc-out', $out, '--guides-dir', $guides,
                     '--log-dir', $iterGuiLogDir, '--screenshot-dir', $iterShotDir,
                     '--open-rule-manager')
        $proc = Start-Process -FilePath $exe -ArgumentList $guiArgs -PassThru
        try { $proc.WaitForInputIdle(10000) | Out-Null } catch { }

        $main = Get-WindowByTitle $proc.Id 'Sparrow Helper' 20
        Check "메인 창 기동 (UIA attach)" { $null -ne $main }
        if (-not $main) { throw "main window did not appear" }
        Write-UiaTree $main "iteration $iter · 1단계: 메인창 로드 직후" ("tree-1-main-loaded-iter$iter.txt") | Out-Null

        # ================= (0) 대분류 분할: XLS 분리 화면 =================
        # --trackc-xls 로 기동하면 [XLS 분리] 대분류가 선택된 상태여야 한다. 그리고 그 화면에는 A/B 전용 입력
        # (프로젝트 대상 경로 · 로컬 소스 트리)이 "아예 없어야" 한다 — 보이면 필수 입력처럼 오해된다.
        $sectionTabs = UIA-First $main 'SectionTabs'
        $xlsTab = UIA-First $main 'SectionXlsTab'
        $fixTab = UIA-First $main 'SectionFixTab'
        Check "0) 대분류 전환 컨트롤(SectionTabs) + 두 대분류 탭 존재" {
            ($null -ne $sectionTabs) -and ($null -ne $xlsTab) -and ($null -ne $fixTab)
        }
        Check "0) --trackc-xls 기동 시 [XLS 분리] 대분류가 선택됨" { UIA-IsSelected $xlsTab }
        Check "0) [코드 자동수정] 대분류는 선택 안 됨" { -not (UIA-IsSelected $fixTab) }
        foreach ($xlsId in @('TrackCXlsPathBox', 'TrackCOutputPathBox', 'OpenRuleManagerButton',
                             'TrackCMappingSummary', 'XlsScopeTree', 'XlsScopeSummary')) {
            Check "0) XLS 화면에 $xlsId 렌더" { -not (Element-Absent $main $xlsId) }
        }
        Check "0) [핵심] XLS 화면에 프로젝트 대상 경로 입력(TargetPathBox) 없음" { Element-Absent $main 'TargetPathBox' }
        Check "0) XLS 화면에 로컬 소스 범위 트리(ScopeTree) 없음" { Element-Absent $main 'ScopeTree' }
        Check "0) XLS 화면에 코드 규칙 체크박스(ASObjectVarSafe) 없음" { Element-Absent $main 'ASObjectVarSafe' }
        # (U) 실사용자 언어: 화면 어디에도 내부 트랙 명칭이 노출되지 않는다(내부 식별자/주석은 그대로 유지).
        $xlsTexts = @(Get-AllVisibleText $main)
        $xlsTrackHits = @(Find-TextMatches $xlsTexts 'Track A') + @(Find-TextMatches $xlsTexts 'Track B')
        Check "U) XLS 화면 텍스트에 'Track A'/'Track B' 문구 없음 (적중 $($xlsTrackHits.Count)개: $($xlsTrackHits -join ' | '))" {
            $xlsTrackHits.Count -eq 0
        }

        # xls 경로에서 만들어진 범위 트리(로컬 소스 스캔 아님): 픽스처의 폴더 + 파일 리프가 전부 있어야 한다.
        $treeReady = Wait-For { @(Get-ScopeNodeNames $main).Count -ge ($FIX_FILES + $FIX_DIRS) } 20
        $scopeNames = Get-ScopeNodeNames $main
        Check "0) XLS 범위 트리 노드 생성 (실제 $($scopeNames.Count)개 / 기대 $($FIX_FILES + $FIX_DIRS)개)" { $treeReady }
        Check "0) 트리에 범위 검증용 두 폴더($SCOPE_KEEP_DIR · $SCOPE_DROP_DIR) 노드 존재" {
            (@($scopeNames) -contains $SCOPE_KEEP_DIR) -and (@($scopeNames) -contains $SCOPE_DROP_DIR)
        }
        Check "0) 트리에 파일 리프(Alpha.cs · Gamma.cs) 존재" {
            (@($scopeNames) -contains 'Alpha.cs') -and (@($scopeNames) -contains 'Gamma.cs')
        }
        $xlsSummary0 = UIA-FirstName $main 'XlsScopeSummary'
        Check "0) 초기 범위 요약 = 선택 없음(전건) (실제: '$xlsSummary0')" {
            ($xlsSummary0 -like "*$XLS_TOTAL_TEXT*") -and ($xlsSummary0 -like '*선택 없음*')
        }

        # ============ (P) 실 규모 트리 UX — 공통 접두 · UIA 이름 · 전 노드 노출 · 가로 잘림 ============
        # 실 xls 로만 드러났던 결함 3종의 회귀 방지. 합성 4파일 픽스처에서는 전부 우연히 통과했었다.
        $xlsTree = UIA-First $main 'XlsScopeTree'
        Check "P) XlsScopeTree 요소 존재" { $null -ne $xlsTree }
        Expand-AllTreeItems $xlsTree

        # (1) 공통 접두 접힘: 루트가 'D:' 가 아니라 실제 분기 폴더이고, 루트 개수 = 분기 수.
        $rootNames = @((Get-TreeRootItems $xlsTree) | ForEach-Object { [string]$_.Current.Name })
        $branchHit = @($FIX_BRANCHES | Where-Object { Name-StartsAny $rootNames $_ }).Count
        Check "P) [결함1] 트리 루트 = 실제 분기 폴더 $($FIX_BRANCHES.Count)개(드라이브/단일자식 체인 아님) (실제: $($rootNames -join ' | '))" {
            ($rootNames.Count -eq $FIX_BRANCHES.Count) -and ($branchHit -eq $FIX_BRANCHES.Count) -and
            (@($rootNames | Where-Object { $_ -like 'D:*' }).Count -eq 0)
        }
        $commonCaption = UIA-FirstName $main 'XlsScopeCommonPath'
        Check "P) [결함1] 공통 경로 캡션 표시(접어 낸 접두를 한 줄로) (실제: '$commonCaption')" {
            (-not (Element-Absent $main 'XlsScopeCommonPath')) -and ($commonCaption -like "*$FIX_PREFIX*")
        }

        # (2) UIA 이름: 타입명 폴백이 하나도 없고, 한글 폴더·긴 파일명이 실제 이름으로 읽힌다.
        $nodeNames = Get-TreeItemNames $xlsTree
        $typeNamed = @($nodeNames | Where-Object { $_ -like '*SourceScopeNode*' })
        Check "P) [결함3] TreeItem UIA 이름에 타입명(SourceScopeNode) 0개 (노드 $($nodeNames.Count)개 중 $($typeNamed.Count)개)" {
            ($nodeNames.Count -ge $FIX_FILES) -and ($typeNamed.Count -eq 0) -and
            (@($nodeNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0)
        }
        Check "P) [결함3] UIA 로 한글 폴더($FIX_KOREAN_DIR)·긴 파일명($FIX_LONG_FILE)이 실제 이름으로 읽힘" {
            (Name-StartsAny $nodeNames $FIX_KOREAN_DIR) -and (Name-StartsAny $nodeNames $FIX_LONG_FILE) -and
            (Name-StartsAny $nodeNames 'Alpha.cs')
        }

        # (3) 가상화 OFF: 확장 후 리프(파일 노드) 수 == 픽스처 파일 수(화면 밖 노드까지 전부 노출).
        $leafNames = Get-TreeLeafNames $xlsTree
        Check "P) [결함3] 전 노드 노출(가상화 없음): 파일 리프 $FIX_FILES 개 (실제: $($leafNames.Count)개)" {
            $leafNames.Count -eq $FIX_FILES
        }

        # (4) 가로 잘림/스크롤 없음: 트리가 가로로 스크롤될 필요가 없고, 보이는 노드가 전부 트리 폭 안에 있다.
        $treeRect = Get-RectInfo $xlsTree
        $hScroll = Get-HorizontallyScrollable $xlsTree
        Check "P) [결함2] 트리 가로 스크롤 불필요(HorizontallyScrollable=$hScroll · 트리 $(Format-Rect $treeRect))" { -not $hScroll }
        $overflow = @()
        foreach ($it in (Get-TreeItems $xlsTree)) {
            $r = Get-RectInfo $it
            if (-not $r -or $r.Empty -or $r.Off) { continue }   # 세로 스크롤로 뷰포트 밖인 노드는 판정 대상이 아니다
            if (($r.X -lt ($treeRect.X - $RECT_TOLERANCE)) -or
                (($r.X + $r.W) -gt ($treeRect.X + $treeRect.W + $RECT_TOLERANCE))) {
                $overflow += ([string]$it.Current.Name + ' ' + (Format-Rect $r))
            }
        }
        Check "P) [결함2] 보이는 노드가 전부 트리 폭 안(가로 잘림 없음) (초과 $($overflow.Count)개: $($overflow -join ' , '))" {
            ($null -ne $treeRect) -and (-not $treeRect.Empty) -and ($overflow.Count -eq 0)
        }

        # 같은 단계에서 실제 이미지도 남긴다(트리 덤프 = 수치, PNG = 눈으로 보는 화면).
        $mainRectForShot = Get-RectInfo $main
        $shotMain = Request-Snapshot $iterShotDir 'req-main' $SHOT_TIMEOUT_SEC $main

        # ================= (1) 관리창 자동 오픈 =================
        $mgr = Get-OwnedWindow $main '체커 규칙 관리' 20
        Check "1) [체커 규칙 관리] 별도 창 자동 오픈(--open-rule-manager)" { $null -ne $mgr }
        if (-not $mgr) { throw "rule manager window did not appear" }

        $rowsReady = Wait-For { @(UIA-FindAll $mgr 'assignCombo').Count -ge 3 } 20
        Check "1) 체커 매핑 영역에 검출 체커 3종 행 렌더" { $rowsReady }
        Write-UiaTree $mgr "iteration $iter · 2단계: 관리창 오픈 직후" ("tree-2-manager-open-iter$iter.txt") | Out-Null
        $mgrRectForShot = Get-RectInfo $mgr
        $shotMgr = Request-Snapshot $iterShotDir 'req-manager' $SHOT_TIMEOUT_SEC $mgr

        # ================= (L) 레이아웃 회귀 단정 =================
        # 스크린샷을 볼 수 없으므로 잘림/0크기/화면밖/겹침/찌그러짐을 전부 수치로 판정한다.
        $mgrRect = Get-RectInfo $mgr
        Check "L) 관리창 최소 크기 ${MIN_MGR_W}x${MIN_MGR_H} 이상 (실제: $(Format-Rect $mgrRect))" {
            ($null -ne $mgrRect) -and (-not $mgrRect.Empty) -and ($mgrRect.W -ge $MIN_MGR_W) -and ($mgrRect.H -ge $MIN_MGR_H)
        }
        foreach ($mgrId in @('RuleList', 'RuleNameBox', 'RuleEditor', 'RuleNewButton', 'RuleSaveButton',
                             'RuleDeleteButton', 'AssignList', 'AssignSaveButton')) {
            Check-IdLayout $mgr '관리창' $mgrId | Out-Null
        }
        # 체커 매핑 콤보는 id 를 공유하므로(행마다 하나) Name(체커 키)으로 구분해 전부 검사한다.
        foreach ($combo in @(UIA-FindAll $mgr 'assignCombo')) {
            Check-ElementLayout $mgr '관리창' $combo ("assignCombo[" + [string]$combo.Current.Name + "]")
        }
        # 규칙 에디터가 찌그러지지 않았는지(높이 하한) + 마스터(목록)·디테일(에디터) 겹침 없음.
        $editorRect = Get-RectInfo (UIA-First $mgr 'RuleEditor')
        Check "L) 규칙 에디터 높이 >= $MIN_RULE_EDITOR_H (실제: $(Format-Rect $editorRect))" {
            ($null -ne $editorRect) -and (-not $editorRect.Empty) -and ($editorRect.H -ge $MIN_RULE_EDITOR_H)
        }
        $listRect = Get-RectInfo (UIA-First $mgr 'RuleList')
        Check "L) 규칙 목록·에디터 겹침 없음 (목록 $(Format-Rect $listRect) / 에디터 $(Format-Rect $editorRect))" {
            (-not (Rect-Overlaps $listRect $editorRect))
        }
        # 메인창 쪽 핵심 요소도 같은 기준으로 본다([XLS 분리] 대분류가 선택된 상태).
        foreach ($mainId in @('RunButton', 'SectionTabs', 'SectionFixTab', 'SectionXlsTab',
                              'TrackCXlsPathBox', 'TrackCOutputPathBox', 'OpenRuleManagerButton',
                              'TrackCMappingSummary', 'XlsScopeTree', 'XlsScopeSummary', 'XlsScopeCommonPath')) {
            Check-IdLayout $main '메인창' $mainId | Out-Null
        }
        # 레이아웃 실패는 관리창이 살아 있는 지금이 가장 좋은 증거 시점이다(창을 닫은 뒤엔 트리를 뜰 수 없다).
        Save-FailureContext $iter $failuresAtStart $main $mgr

        # ================= (2) 자동매핑 없음 (핵심) =================
        Check "2) 시작 시 _assignments.json 미생성(지정 없음)" { -not (Test-Path -LiteralPath $assignJson) }

        $ruleNames = Get-ListItemNames (UIA-First $mgr 'RuleList')
        Check "2) 규칙 라이브러리에 체커키 동명 규칙($K_KEYNAMED) 표시" { @($ruleNames) -contains $K_KEYNAMED }

        $vKeyNamed = Get-ComboValue (Get-AssignCombo $mgr $K_KEYNAMED)
        $vAssign   = Get-ComboValue (Get-AssignCombo $mgr $K_ASSIGN)
        $vPure     = Get-ComboValue (Get-AssignCombo $mgr $K_PURE)
        Check "2) [핵심] 체커키와 동명 규칙 존재해도 $K_KEYNAMED 지정 = '— 없음 —'(자동매핑 없음) (실제: '$vKeyNamed')" { $vKeyNamed -eq $NONE }
        Check "2) $K_ASSIGN 지정 = '— 없음 —' (실제: '$vAssign')" { $vAssign -eq $NONE }
        Check "2) $K_PURE 지정 = '— 없음 —' (실제: '$vPure')" { $vPure -eq $NONE }

        # ================= (3) 규칙 CRUD — 생성 =================
        UIA-Invoke (UIA-First $mgr 'RuleNewButton')
        Start-Sleep -Milliseconds 150
        UIA-SetValue (UIA-First $mgr 'RuleNameBox') $NEW_RULE
        UIA-SetValue (UIA-First $mgr 'RuleEditor') ("규칙: $NEW_MARK.")
        UIA-Invoke (UIA-First $mgr 'RuleSaveButton')

        $newRuleFile = Join-Path $guides "$NEW_RULE.md"
        $createdOk = Wait-For {
            (Test-Path -LiteralPath $newRuleFile) -and (@(Get-ListItemNames (UIA-First $mgr 'RuleList')) -contains $NEW_RULE)
        } 10
        Check "3) [새 규칙] 저장 → RuleList 에 '$NEW_RULE' 추가 + '$NEW_RULE.md' 생성" { $createdOk }
        Write-UiaTree $mgr "iteration $iter · 3단계: 규칙 저장 후" ("tree-3-after-rule-save-iter$iter.txt") | Out-Null

        # ================= (4) 직접 지정 =================
        $selected = Set-ComboValue (Get-AssignCombo $mgr $K_ASSIGN) $NEW_RULE
        Check "4) $K_ASSIGN assignCombo 에서 규칙 '$NEW_RULE' 선택(Expand+SelectionItem)" { $selected }
        $reflected = Wait-For { (Get-ComboValue (Get-AssignCombo $mgr $K_ASSIGN)) -eq $NEW_RULE } 6
        Check "4) 선택이 콤보에 반영됨" { $reflected }

        UIA-Invoke (UIA-First $mgr 'AssignSaveButton')
        $assignWritten = Wait-For {
            if (-not (Test-Path -LiteralPath $assignJson)) { return $false }
            $j = [System.IO.File]::ReadAllText($assignJson)
            $j.Contains($K_ASSIGN) -and $j.Contains($NEW_RULE)
        } 10
        Check "4) [지정 저장] → _assignments.json 에 $K_ASSIGN → $NEW_RULE 기록" { $assignWritten }
        Check "4) $K_KEYNAMED 은 여전히 미지정(_assignments.json 에 없음)" {
            $j = [System.IO.File]::ReadAllText($assignJson); -not $j.Contains($K_KEYNAMED)
        }
        Write-UiaTree $mgr "iteration $iter · 4단계: 지정 저장 후" ("tree-4-after-assign-save-iter$iter.txt") | Out-Null
        # 규칙 생성 + 지정 저장이 화면에 반영된 관리창(라이브러리에 새 규칙, 매핑 콤보에 그 규칙)을 이미지로 남긴다.
        $shotAssign = Request-Snapshot $iterShotDir 'req-assign-saved' $SHOT_TIMEOUT_SEC $mgr
        Save-FailureContext $iter $failuresAtStart $main $mgr   # 규칙/지정 단계 실패도 창이 살아 있을 때 잡는다

        # ================= (5) 실행 부착 =================
        $mgrClosed = Close-Window $mgr
        Check "5) 관리창 닫기" { $mgrClosed }
        $mgr = $null   # 닫힌 창을 나중에 덤프하려 하지 않도록
        Start-Sleep -Milliseconds 300

        $runBtn = UIA-First $main 'RunButton'
        Check "5) 메인 RunButton 존재" { $null -ne $runBtn }
        if ($runBtn) { UIA-Invoke $runBtn }

        $assignDir = Join-Path $out $K_ASSIGN
        $attachOk = Wait-For {
            if (-not (Test-Path -LiteralPath $assignDir)) { return $false }
            $mds = @(Get-ChildItem -LiteralPath $assignDir -Filter *.md -File)
            if ($mds.Count -lt $MD_ASSIGN) { return $false }
            $withRule = @($mds | Where-Object {
                $t = [System.IO.File]::ReadAllText($_.FullName)
                $t.Contains('## 매핑 규칙') -and $t.Contains($NEW_MARK)
            })
            return ($withRule.Count -eq $mds.Count)
        } 30
        $assignCount = 0
        if (Test-Path -LiteralPath $assignDir) { $assignCount = @(Get-ChildItem -LiteralPath $assignDir -Filter *.md -File).Count }
        Check "5) 실행으로 출력 폴더 생성" { Test-Path -LiteralPath $out }
        Check "5) 지정한 $K_ASSIGN 하위 모든 항목 md 에 규칙 부착 ($assignCount 건 / 기대 $MD_ASSIGN 건, 다건)" {
            $attachOk -and ($assignCount -eq $MD_ASSIGN)
        }
        Write-UiaTree $main "iteration $iter · 5단계: 실행 후" ("tree-5-after-run-iter$iter.txt") | Out-Null

        # the key-named-but-UNASSIGNED checker stays PURE, and so does the unrelated one.
        $keyNamedDir = Join-Path $out $K_KEYNAMED
        $pureDir     = Join-Path $out $K_PURE
        Check "5) [핵심] 지정 안 한(파일은 존재) $K_KEYNAMED 는 순수(부착 없음)" {
            $mds = @(Get-ChildItem -LiteralPath $keyNamedDir -Filter *.md -File -ErrorAction SilentlyContinue)
            ($mds.Count -ge 1) -and (@($mds | Where-Object { ([System.IO.File]::ReadAllText($_.FullName)).Contains('## 매핑 규칙') }).Count -eq 0)
        }
        Check "5) 무관 체커 $K_PURE 순수" {
            $mds = @(Get-ChildItem -LiteralPath $pureDir -Filter *.md -File -ErrorAction SilentlyContinue)
            ($mds.Count -ge 1) -and (@($mds | Where-Object { ([System.IO.File]::ReadAllText($_.FullName)).Contains('## 매핑 규칙') }).Count -eq 0)
        }
        # 출력 폴더 순수성: 루트에 체커 폴더만, 부산물 파일 0 (리포트는 로그 폴더로 갔다).
        Check "5) 출력 폴더 루트에 부산물 파일 0건(리포트는 로그 폴더에)" {
            @(Get-ChildItem -LiteralPath $out -File -ErrorAction SilentlyContinue).Count -eq 0
        }

        # ================= (9) 진단 로그(세션 로그 + 실행 리포트) =================
        $sessionFiles = @(Get-ChildItem -LiteralPath $iterGuiLogDir -Filter 'session-*.log' -File -ErrorAction SilentlyContinue)
        Check "9) GUI 세션 로그 파일 생성(session-*.log, $($sessionFiles.Count)개)" { $sessionFiles.Count -ge 1 }
        if ($sessionFiles.Count -ge 1) {
            $sessionText = [System.IO.File]::ReadAllText($sessionFiles[0].FullName)
            Check "9) 세션 로그에 시작 헤더(앱 버전/시작 인자/스킬 루트/guides/OS/.NET) 존재" {
                $sessionText.Contains('=== Sparrow Helper 세션 로그 ===') -and $sessionText.Contains('앱 버전') -and
                $sessionText.Contains('시작 인자') -and $sessionText.Contains('스킬 루트') -and
                $sessionText.Contains('guides 폴더') -and $sessionText.Contains('OS') -and $sessionText.Contains('.NET')
            }
            Check "9) 세션 로그 각 줄에 HH:mm:ss.fff 타임스탬프" {
                @([regex]::Matches($sessionText, '(?m)^\d{2}:\d{2}:\d{2}\.\d{3}  ')).Count -ge 3
            }
            # 창 스냅샷도 세션 로그에 한 줄씩 남는다(성공 'snapshot: <파일명>' / 실패 'snapshot 실패: <사유>').
            Check "9) 세션 로그에 창 스냅샷 기록('snapshot: <파일명>') 존재 · 실패 줄 없음" {
                (@([regex]::Matches($sessionText, 'snapshot: ')).Count -ge $MIN_SHOTS) -and
                (-not $sessionText.Contains('snapshot 실패'))
            }
        }
        $reportFiles = @(Get-ChildItem -LiteralPath $iterGuiLogDir -Filter 'trackc-*.json' -File -ErrorAction SilentlyContinue)
        Check "9) Track C 실행 리포트 생성(trackc-*.json, $($reportFiles.Count)개) + .log 요약 동반" {
            ($reportFiles.Count -ge 1) -and
            (@(Get-ChildItem -LiteralPath $iterGuiLogDir -Filter 'trackc-*.log' -File -ErrorAction SilentlyContinue).Count -ge 1)
        }
        if ($reportFiles.Count -ge 1) {
            $rep = $null
            try { $rep = [System.IO.File]::ReadAllText($reportFiles[0].FullName) | ConvertFrom-Json } catch { }
            Check "9) 리포트 수치가 실제 실행과 일치(writtenMd=$MD_TOTAL · checkerFolders=3 · sha256/크기 기록)" {
                ($null -ne $rep) -and ($rep.writtenMd -eq $MD_TOTAL) -and ($rep.checkerFolders -eq 3) -and
                ($rep.totalRows -eq $MD_TOTAL) -and ($rep.inputSha256.Length -eq 64) -and ($rep.inputSizeBytes -gt 0)
            }
            Check "9) 리포트 assignments 가 지정/부착을 정확히 기록($K_ASSIGN → $NEW_RULE, $MD_ASSIGN 건 부착)" {
                if ($null -eq $rep) { return $false }
                $a = @($rep.assignments | Where-Object { $_.checkerKey -eq $K_ASSIGN })
                ($a.Count -eq 1) -and ($a[0].ruleName -eq $NEW_RULE) -and ($a[0].ruleExists) -and ($a[0].itemsAttached -eq $MD_ASSIGN)
            }
            Check "9) 리포트 unmappedCheckers 에 미지정 체커($K_KEYNAMED · $K_PURE) 포함" {
                if ($null -eq $rep) { return $false }
                (@($rep.unmappedCheckers) -contains $K_KEYNAMED) -and (@($rep.unmappedCheckers) -contains $K_PURE)
            }
        }

        # ================= (6) 지정 기억 =================
        UIA-Invoke (UIA-First $main 'OpenRuleManagerButton')
        $mgr2 = Get-OwnedWindow $main '체커 규칙 관리' 15
        Check "6) [체커 규칙 관리] 재오픈" { $null -ne $mgr2 }
        if ($mgr2) {
            $mgr = $mgr2
            Wait-For { @(UIA-FindAll $mgr2 'assignCombo').Count -ge 3 } 15 | Out-Null
            $remembered = Wait-For { (Get-ComboValue (Get-AssignCombo $mgr2 $K_ASSIGN)) -eq $NEW_RULE } 8
            $vRemember = Get-ComboValue (Get-AssignCombo $mgr2 $K_ASSIGN)
            Check "6) 재오픈 시 $K_ASSIGN 지정이 '$NEW_RULE' 로 미리 채워짐(기억) (실제: '$vRemember')" { $remembered }
            $vKeyNamed2 = Get-ComboValue (Get-AssignCombo $mgr2 $K_KEYNAMED)
            Check "6) $K_KEYNAMED 은 여전히 '— 없음 —'(자동 지정 안 됨) (실제: '$vKeyNamed2')" { $vKeyNamed2 -eq $NONE }

            # ================= (7) 규칙 CRUD — 삭제 =================
            UIA-Invoke (UIA-First $mgr2 'RuleNewButton')
            Start-Sleep -Milliseconds 150
            UIA-SetValue (UIA-First $mgr2 'RuleNameBox') $TMP_RULE
            UIA-SetValue (UIA-First $mgr2 'RuleEditor') "임시 규칙 본문."
            UIA-Invoke (UIA-First $mgr2 'RuleSaveButton')
            $tmpFile = Join-Path $guides "$TMP_RULE.md"
            $tmpCreated = Wait-For { Test-Path -LiteralPath $tmpFile } 8
            Check "7) 삭제용 임시 규칙 '$TMP_RULE' 생성" { $tmpCreated }

            # select it in RuleList, then delete (auto-confirm the MessageBox Yes).
            $sel = $false
            foreach ($it in @((UIA-First $mgr2 'RuleList').FindAll($Desc, (New-Object System.Windows.Automation.PropertyCondition $CTProp, $ListItemCT)))) {
                if ([string]$it.Current.Name -eq $TMP_RULE) {
                    $sip = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                    ([System.Windows.Automation.SelectionItemPattern]$sip).Select(); $sel = $true; break
                }
            }
            if ($sel) {
                # RuleDelete raises a Yes/No confirm dialog (caption '규칙 삭제') — click Yes when it appears.
                UIA-Invoke (UIA-First $mgr2 'RuleDeleteButton')
                $confirmed = Click-ConfirmYes $mgr2 '규칙 삭제' 6
                $tmpDeleted = Wait-For {
                    (-not (Test-Path -LiteralPath $tmpFile)) -and (-not (@(Get-ListItemNames (UIA-First $mgr2 'RuleList')) -contains $TMP_RULE))
                } 8
                Check "7) [삭제] 후 '$TMP_RULE.md' 제거 + RuleList 에서 사라짐" { $confirmed -and $tmpDeleted }
            }
            else {
                Check "7) 임시 규칙 RuleList 선택" { $false }
            }

            Close-Window $mgr2 | Out-Null
            $mgr = $null
        }

        # ================= (X) XLS 범위 트리로 좁혀 실행 =================
        # 트리는 이 xls 자신의 경로(ListPaths)로 만들었으므로, 폴더 노드 하나를 체크하고 실행하면 그 폴더의 항목만
        # 나와야 한다(다른 폴더의 체커 폴더는 아예 생기지 않는다). 출력은 별도 폴더로 보내 전건 실행 결과와 섞지 않는다.
        $out2 = Join-Path $work 'out-scoped'
        UIA-SetValue (UIA-First $main 'TrackCOutputPathBox') $out2

        $keepNode = Get-ScopeNode $main $SCOPE_KEEP_DIR
        Check "X) 범위 트리에서 '$SCOPE_KEEP_DIR' 폴더 노드 발견" { $null -ne $keepNode }
        $toggled = UIA-Toggle $keepNode
        Check "X) '$SCOPE_KEEP_DIR' 폴더 노드 체크(TogglePattern)" { $toggled }
        $summaryUpdated = Wait-For { (UIA-FirstName $main 'XlsScopeSummary') -like "*선택 ${SCOPE_KEEP_N}개 파일*" } 8
        $xlsSummary1 = UIA-FirstName $main 'XlsScopeSummary'
        Check "X) 폴더 체크로 범위 요약 갱신 (실제: '$xlsSummary1')" {
            $summaryUpdated -and ($xlsSummary1 -like "*선택 ${SCOPE_KEEP_N}개 파일*") -and ($xlsSummary1 -like "*${SCOPE_KEEP_N}건*")
        }
        $shotScoped = Request-Snapshot $iterShotDir 'req-xls-scoped' $SHOT_TIMEOUT_SEC $main

        UIA-Invoke (UIA-First $main 'RunButton')
        $scopedDir = Join-Path $out2 $K_ASSIGN
        $scopedOk = Wait-For {
            if (-not (Test-Path -LiteralPath $scopedDir)) { return $false }
            @(Get-ChildItem -LiteralPath $scopedDir -Filter *.md -File).Count -ge $SCOPE_KEEP_N
        } 30
        $scopedTotal = 0
        if (Test-Path -LiteralPath $out2) {
            $scopedTotal = @(Get-ChildItem -LiteralPath $out2 -Recurse -Filter *.md -File).Count
        }
        Check "X) 선택 폴더($SCOPE_KEEP_DIR)의 $K_ASSIGN 항목 ${SCOPE_KEEP_N}건 생성" { $scopedOk }
        Check "X) [핵심] 선택 안 한 폴더의 체커 폴더는 생성 안 됨($K_PURE · $K_KEYNAMED)" {
            (-not (Test-Path -LiteralPath (Join-Path $out2 $K_PURE))) -and
            (-not (Test-Path -LiteralPath (Join-Path $out2 $K_KEYNAMED)))
        }
        # 전건이면 $K_ASSIGN 이 $MD_ASSIGN 건인데(선택 폴더 밖에도 같은 체커가 있다) 범위 실행은 $SCOPE_KEEP_N 건이어야
        # 한다 = 필터가 "체커"가 아니라 "경로"로 걸린다는 증명.
        Check "X) 범위 실행 출력 md 총 ${SCOPE_KEEP_N}건(전건 ${MD_TOTAL}건 아님 · 같은 체커의 다른 폴더 항목도 제외) (실제: $scopedTotal)" {
            $scopedTotal -eq $SCOPE_KEEP_N
        }
        Write-UiaTree $main "iteration $iter · 6단계: 범위 좁힌 실행 후" ("tree-6-after-scoped-run-iter$iter.txt") | Out-Null

        # ================= (T) 대분류 전환 → A/B 화면 =================
        $switched = UIA-SelectItem $fixTab
        Check "T) SectionFixTab 선택(SelectionItemPattern)" { $switched }
        $fixShown = Wait-For { (-not (Element-Absent $main 'TargetPathBox')) } 8
        Check "T) A/B 화면 등장: 대상 경로 입력(TargetPathBox) 렌더" { $fixShown }
        Check "T) A/B 화면에 로컬 소스 범위 트리(ScopeTree) 렌더" { -not (Element-Absent $main 'ScopeTree') }
        Check "T) A/B 화면에 코드 규칙 체크박스(ASObjectVarSafe) 렌더" { -not (Element-Absent $main 'ASObjectVarSafe') }
        Check "T) [핵심] A/B 화면에는 XLS 전용 컨트롤이 없음(TrackCXlsPathBox · XlsScopeTree)" {
            (Element-Absent $main 'TrackCXlsPathBox') -and (Element-Absent $main 'XlsScopeTree')
        }
        Check "T) 대분류 선택 상태가 바뀜(Fix 선택 · XLS 해제)" {
            (UIA-IsSelected $fixTab) -and (-not (UIA-IsSelected $xlsTab))
        }

        # ============ (U) 실사용자 언어 라벨 + 옵션 제거 + 커밋 안 함 안내 ============
        # 화면에서는 트랙(A/B/C) 대신 하는 일로 부른다. 내부 식별자(AutomationId TrackATab/TrackBTab, enum,
        # 커밋 메시지 prefix)는 그대로이므로, 라벨은 "id 로 찾아 Name 을 읽어" 확인한다.
        $tabAName = UIA-FirstName $main 'TrackATab'
        $tabBName = UIA-FirstName $main 'TrackBTab'
        Check "U) 하위 탭 라벨 = '코드 규칙' (실제: '$tabAName')" { $tabAName -eq '코드 규칙' }
        Check "U) 하위 탭 라벨 = '주석·레이아웃' (실제: '$tabBName')" { $tabBName -eq '주석·레이아웃' }

        $runNameA = UIA-FirstName $main 'RunButton'
        Check "U) 실행 버튼 라벨([코드 규칙] 탭) = '코드 규칙 수정 실행' (실제: '$runNameA')" { $runNameA -eq '코드 규칙 수정 실행' }
        UIA-SelectItem (UIA-First $main 'TrackBTab') | Out-Null
        Wait-For { (UIA-FirstName $main 'RunButton') -eq '주석·레이아웃 수정 실행' } 6 | Out-Null
        $runNameB = UIA-FirstName $main 'RunButton'
        Check "U) 실행 버튼 라벨([주석·레이아웃] 탭) = '주석·레이아웃 수정 실행' (실제: '$runNameB')" {
            $runNameB -eq '주석·레이아웃 수정 실행'
        }
        $summaryB = UIA-FirstName $main 'SummaryModeText'
        Check "U) 요약바가 커밋하지 않음을 알림 (실제: '$summaryB')" { $summaryB -like '*커밋하지 않*' }
        $rulesB = UIA-FirstName $main 'SummaryRulesText'
        Check "U) 요약바 규칙 문구가 사용자 언어 (실제: '$rulesB')" { $rulesB -like '주석·레이아웃 · 선택 *개' }
        # 스냅샷/후속 단정은 기본 상태(=[코드 규칙] 탭)에서 찍는다.
        UIA-SelectItem (UIA-First $main 'TrackATab') | Out-Null
        Wait-For { (UIA-FirstName $main 'RunButton') -eq '코드 규칙 수정 실행' } 6 | Out-Null
        $rulesA = UIA-FirstName $main 'SummaryRulesText'
        Check "U) 요약바 규칙 문구가 사용자 언어 (실제: '$rulesA')" { $rulesA -like '코드 규칙 · 선택 *개' }

        # DryRun/생성파일 포함은 GUI 에서 제거됐다(CLI 러너 전용). 빈 옵션 탭도 남기지 않는다.
        foreach ($goneId in @('OptionsTab', 'DryRunCheck', 'IncludeGeneratedCheck')) {
            Check "U) [제거] 옵션 컨트롤 $goneId 가 GUI 에 존재하지 않음" { $null -eq (UIA-First $main $goneId) }
        }

        # 규칙별 커밋은 유지한다(롤백 단위 = 규칙, 러너의 컴파일 게이트도 이 모드에서만 의미가 있다).
        # [코드 자동수정] 화면에서만 보이고 기본값은 꺼짐(=파일만 수정)이며, 켜면 요약 문구가 커밋 모드로 바뀐다.
        $commitBox = UIA-First $main 'CommitCheck'
        Check "U) [유지] 규칙별 커밋 체크박스가 A/B 화면에 존재" { $null -ne $commitBox }
        if ($commitBox) {
            $tog = $commitBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            Check "U) 규칙별 커밋 기본값 = 꺼짐(파일만 수정)" { $tog.Current.ToggleState -eq 'Off' }
            $modeOff = UIA-FirstName $main 'SummaryModeText'
            Check "U) 커밋 꺼짐일 때 요약 = 커밋 안 함 (실제: '$modeOff')" { $modeOff -like '파일만 수정 · 커밋하지 않음*' }

            $tog.Toggle(); Start-Sleep -Milliseconds 500
            $modeOn = UIA-FirstName $main 'SummaryModeText'
            Check "U) 커밋 켜짐일 때 요약이 커밋 모드로 전환 (실제: '$modeOn')" { $modeOn -like '규칙별 커밋 생성*' }
            $tog.Toggle(); Start-Sleep -Milliseconds 400   # 원복(이후 단정에 영향 없게)
        }

        $fixTexts = @(Get-AllVisibleText $main)
        $fixTrackHits = @(Find-TextMatches $fixTexts 'Track A') + @(Find-TextMatches $fixTexts 'Track B')
        Check "U) [핵심] A/B 화면 텍스트에 'Track A'/'Track B' 문구 없음 (적중 $($fixTrackHits.Count)개: $($fixTrackHits -join ' | '))" {
            $fixTrackHits.Count -eq 0
        }
        # DryRun/생성파일 포함만 제거했다. 규칙별 커밋은 롤백 단위라 화면에 남아 있어야 한다(위 U 블록에서 동작 검증).
        $optionHits = @(Find-TextMatches $fixTexts 'DryRun') + @(Find-TextMatches $fixTexts '생성 파일 포함')
        Check "U) [제거] 화면에 DryRun·생성파일 포함 문구 없음 (적중 $($optionHits.Count)개: $($optionHits -join ' | '))" {
            $optionHits.Count -eq 0
        }
        foreach ($fixId in @('TargetPathBox', 'ScopeTree', 'RulesTabs', 'RunButton')) {
            Check-IdLayout $main '메인창(A/B)' $fixId | Out-Null
        }
        Write-UiaTree $main "iteration $iter · 7단계: 코드 자동수정 대분류" ("tree-7-fix-section-iter$iter.txt") | Out-Null
        $shotFix = Request-Snapshot $iterShotDir 'req-fix-section' $SHOT_TIMEOUT_SEC $main

        # ============ 하위 탭 라벨/개수 (코드 자동수정 화면) ============
        $subTabNames = @(Get-TabItemNames (UIA-First $main 'RulesTabs'))
        Check "T) [코드 자동수정] 하위 탭 2개 · 라벨/순서 정확 (실제: $($subTabNames -join ' | '))" {
            ($subTabNames.Count -eq $SUB_TABS.Count) -and
            (@(0..($SUB_TABS.Count - 1) | Where-Object { $subTabNames[$_] -eq $SUB_TABS[$_] }).Count -eq $SUB_TABS.Count)
        }

        # ================= (8) clean 종료 =================
        # 실패가 있었다면 프로세스를 죽이기 전에 실패 컨텍스트(실패 목록 + 그 시점 트리)를 확보한다.
        Save-FailureContext $iter $failuresAtStart $main $mgr

        $closed = $false
        try {
            $wp = $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            ([System.Windows.Automation.WindowPattern]$wp).Close()
            $closed = $proc.WaitForExit(8000)
        } catch { }
        if (-not $closed) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
            $closed = $proc.WaitForExit(4000)
        }
        Check "8) 앱 프로세스 clean 종료" { $closed -and $proc.HasExited }
        # 정상 종료 표식이 있어야 "표식 없이 끊긴 로그 = 비정상 종료"라는 판별 기준이 성립한다.
        Check "9) 세션 로그에 정상 종료 표식('세션 종료 (정상)') 기록" {
            $sf = @(Get-ChildItem -LiteralPath $iterGuiLogDir -Filter 'session-*.log' -File -ErrorAction SilentlyContinue)
            ($sf.Count -ge 1) -and ([System.IO.File]::ReadAllText($sf[0].FullName)).Contains('세션 종료 (정상)')
        }

        # ================= (S) 창 스냅샷(실제 렌더 이미지) =================
        # 프로세스가 끝난 뒤 판정한다(모든 캡처가 디스크에 flush 된 시점). 앱이 .tmp → rename 하므로 남은 .tmp 도 없어야 한다.
        $shots = @(Get-ChildItem -LiteralPath $iterShotDir -Filter '*.png' -File -ErrorAction SilentlyContinue)
        Check "S) 창 스냅샷 PNG ${MIN_SHOTS}장 이상 생성 (실제: $($shots.Count)장 · $iterShotDir)" { $shots.Count -ge $MIN_SHOTS }
        foreach ($s in $shots) {
            $info = Get-PngInfo $s.FullName
            $desc = '판독 불가'
            if ($info) { $desc = [string]$info.W + "x" + [string]$info.H + "px" }
            Check "S) $($s.Name) 유효 PNG(시그니처+IHDR) · > ${MIN_SHOT_BYTES}B (실제: $desc · $($s.Length)B)" {
                ($null -ne $info) -and ($info.W -gt 0) -and ($info.H -gt 0) -and ($s.Length -gt $MIN_SHOT_BYTES)
            }
        }
        Check "S) 요청 기반 캡처 5종 생성(XLS 화면 · 관리창 · 지정 저장 후 · 범위 체크 · A/B 화면)" {
            ($null -ne $shotMain) -and ($null -ne $shotMgr) -and ($null -ne $shotAssign) -and
            ($null -ne $shotScoped) -and ($null -ne $shotFix)
        }
        Check-ShotScale $shotMain   $mainRectForShot '메인창(XLS 분리 화면)'
        Check-ShotScale $shotMgr    $mgrRectForShot  '관리창'
        Check-ShotScale $shotAssign $mgrRectForShot  '관리창(지정 저장 후)'
        Check-ShotScale $shotScoped $mainRectForShot '메인창(XLS 범위 체크)'
        Check-ShotScale $shotFix    $mainRectForShot '메인창(코드 자동수정 화면)'
        Check "S) capture.request 잔여 없음(요청 전부 처리) · 미완성 .tmp 없음" {
            (-not (Test-Path -LiteralPath (Join-Path $iterShotDir 'capture.request'))) -and
            (@(Get-ChildItem -LiteralPath $iterShotDir -Filter '*.tmp' -File -ErrorAction SilentlyContinue).Count -eq 0)
        }
    }
    finally {
        # 예외로 빠져나온 경로에서도 실패 컨텍스트를 남긴다(중복 저장은 내부에서 방지).
        Save-FailureContext $iter $failuresAtStart $main $mgr
        # 앱 자신의 로그/리포트는 $work 삭제 전에 이미 $iterGuiLogDir(진단 폴더) 에 있으므로 그대로 보존된다.
        if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { } }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 이 반복에서 실패가 하나라도 생겼으면 FAILURE-CONTEXT-iter<N>.txt 에 실패 목록 + 그 시점 트리 덤프를 함께 남긴다.
$script:failureContextSaved = @{}
function Save-FailureContext([int]$iter, [int]$failuresAtStart, $main, $mgr) {
    if ($script:failureContextSaved.ContainsKey($iter)) { return }
    if ($script:failures.Count -le $failuresAtStart) { return }
    $script:failureContextSaved[$iter] = $true

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# FAILURE CONTEXT — iteration $iter")
    [void]$sb.AppendLine("# 시각: " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 실패한 단정 (이 반복)')
    for ($i = $failuresAtStart; $i -lt $script:failures.Count; $i++) {
        [void]$sb.AppendLine('  - ' + $script:failures[$i])
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 같은 실행의 단계별 트리 덤프(같은 폴더)')
    foreach ($f in @(Get-ChildItem -LiteralPath $runDir -Filter "tree-*-iter$iter.txt" -File -ErrorAction SilentlyContinue |
                     Sort-Object Name)) {
        [void]$sb.AppendLine('  - ' + $f.Name)
    }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 실패 시점 UIA 트리 — 메인창')
    [void]$sb.AppendLine((Build-UiaTreeText $main "FAILURE 시점 · 메인창 (iteration $iter)"))
    if ($mgr) {
        [void]$sb.AppendLine('## 실패 시점 UIA 트리 — 관리창')
        [void]$sb.AppendLine((Build-UiaTreeText $mgr "FAILURE 시점 · 관리창 (iteration $iter)"))
    }
    $path = Join-Path $runDir ("FAILURE-CONTEXT-iter$iter.txt")
    try { [System.IO.File]::WriteAllText($path, $sb.ToString(), $Utf8Bom) } catch { }
    Log "  [failure-context] $path"
}

for ($i = 1; $i -le $Iterations; $i++) { Invoke-OneIteration $i }

# ---- real cache untouched -------------------------------------------------
$cacheAfter = Snapshot-Dir $realCache
Check "실 캐시 references\checkers 미변경(--guides-dir override)" { Snapshots-Equal $cacheBefore $cacheAfter }

# ---- (U) 소스 계약: GUI = 항상 -NoCommit · CLI 러너 옵션은 보존 -----------
# A/B 실행 자체는 이 하네스에서 돌리지 않는다(대상 소스 트리 + git + 엔진 빌드가 필요해 비용이 크다).
# 대신 "실행 후 안내 문구"와 "GUI 가 넘기는 커밋 인자"를 소스에서 직접 확인해 계약을 고정한다.
$guiSource = [System.IO.File]::ReadAllText((Join-Path $RepositoryRoot 'tools\SparrowRunner.Gui\MainWindow.xaml.cs'))
Check "U) GUI 실행 후 안내 문구가 소스에 존재('…개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.')" {
    $guiSource.Contains('개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.')
}
Check "U) GUI 가 커밋 체크 상태에 따라 -Commit / -NoCommit 을 전달" {
    $guiSource.Contains('"-NoCommit"') -and $guiSource.Contains('"-Commit"')
}
Check "U) [핵심] GUI 는 -DryRun/-IncludeGenerated 를 전달하지 않음(CLI 러너 전용 옵션)" {
    (-not $guiSource.Contains('"-DryRun"')) -and (-not $guiSource.Contains('"-IncludeGenerated"'))
}
# CLI 러너의 파라미터는 자동화/CI 자산이라 GUI 단순화와 무관하게 살아 있어야 한다(회귀 테스트가 이를 검증 중).
foreach ($runnerRel in @('tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1',
                         'tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1')) {
    $runnerPath = Join-Path $RepositoryRoot $runnerRel
    $runnerSrc = ''
    if (Test-Path -LiteralPath $runnerPath) { $runnerSrc = [System.IO.File]::ReadAllText($runnerPath) }
    foreach ($param in @('$Commit', '$NoCommit', '$DryRun', '$VerifyCmd')) {
        Check "U) CLI 러너 파라미터 보존: $(Split-Path $runnerRel -Leaf) $param" { $runnerSrc.Contains($param) }
    }
}
$commentRunner = [System.IO.File]::ReadAllText((Join-Path $RepositoryRoot 'tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1'))
Check "U) CLI 러너 파라미터 보존: Run-SparrowCommentFix.ps1 `$IncludeGenerated" { $commentRunner.Contains('$IncludeGenerated') }

Log ""
Log "진단 로그: $runDir"
if ($failures.Count) { throw ("GUI UIA tests failed (진단 로그: $runDir):`n  " + ($failures -join "`n  ")) }
Log ("GUI UIA tests passed ({0} iteration(s))." -f $Iterations)
# validate.ps1 신호 규약: 성공은 반드시 exit 0 (잔여 $LASTEXITCODE 로 인한 거짓 실패 방지).
exit 0

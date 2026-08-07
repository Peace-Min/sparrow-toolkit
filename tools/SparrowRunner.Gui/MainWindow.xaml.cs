using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SparrowXlsExport.Core;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// WPF wrapper for [코드 규칙]·[주석·레이아웃] PowerShell runners. Rewrite logic stays in the existing CLI scripts.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string _skillRoot;
        private readonly string _toolsDir;

        // 체커 규칙 캐시(가이드) 폴더 = <스킬 루트>\references\checkers. "매핑 있음 = <키>.md 파일 있음".
        // 첫 규칙 저장 시 없으면 생성한다(.gitignore 대상 로컬 자산). CLI --guides-dir 로 override 가능(테스트가
        // 임시 폴더를 줘 실 캐시 오염을 막는다). 생성자에서 인자를 반영해 한 번만 확정한다.
        private readonly string _guidesDir;

        // 세션 파일 로그(화면 LogBox 는 앱을 닫으면 증발한다). AppendLog 가 화면 + 이 파일에 동시 기록하고,
        // [XLS 분리] 실행 리포트도 같은 로그 폴더에 남는다(출력 폴더 순수성 유지). 기록 실패는 앱을 죽이지 않는다.
        private readonly SessionLog _sessionLog;

        // 창 스냅샷(--screenshot-dir). 이 앱은 미설치 커스텀 exe라 외부에서 스크린샷을 찍을 수 없어, UI 증거가
        // UIA Rect 수치뿐이었다. 그래서 앱이 스스로 자기 창을 PNG로 렌더해 남긴다(AI/신고자가 실제 UI를 눈으로 봄).
        // 인자를 주지 않으면 null = 기능 전체 비활성(기존 동작 완전 불변).
        private readonly SnapshotRecorder? _snapshots;

        // 테스트 기동 인자(GUI를 알려진 상태로 자동 구동): [XLS 분리] 탭 프리필 + 선택적 자동실행 + 관리창 자동 오픈.
        private readonly string? _startupXls;
        private readonly string? _startupXlsOut;
        private readonly bool _startupXlsAutorun;
        private readonly bool _startupOpenRuleManager;

        // 활성 대분류(+ [코드 자동수정] 화면의 하위 탭)가 곧 실행 기능이다.
        //   [코드 자동수정] 대분류 → 선택된 하위 탭([코드 규칙] / [주석·레이아웃])
        //   [XLS 분리]     대분류 → 항상 [XLS 분리]
        //   None = 방어용 폴백(로드 전 등 어느 하위 탭도 선택되지 않은 순간).
        private enum ActiveMode { CodeRule, Comment, XlsSplit, None }

        // GUI 실행 모드는 [규칙별 커밋 생성](CommitCheck) 체크 상태가 결정한다:
        //   꺼짐(기본) → 러너에 -NoCommit  : 파일만 수정, 커밋은 사용자가 git 으로 한다(git diff 로 검토).
        //   켜짐        → 러너에 -Commit    : 규칙 하나 = 커밋 하나(규칙 단위 롤백 가능).
        // 화면 안내(요약바·툴팁)와 실행 로그는 모두 ModeNotice / ModeDoneSuffix 를 거쳐 이 실제 모드를 말한다 —
        // 예전에는 로그가 "커밋: 하지 않음 (-NoCommit 고정)" 을 무조건 찍어, 커밋을 켠 사용자에게 거짓을 보고했다.
        // DryRun / 생성 파일 포함은 여전히 CLI 러너 전용 옵션(-DryRun / -IncludeGenerated)이다.
        private const string NoCommitNotice = "파일만 수정 · 커밋하지 않음 (git diff 로 검토 후 커밋)";
        private const string NoCommitDoneSuffix = "개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.";
        private const string CommitNotice = "규칙별 커밋 생성 (규칙 하나 = 커밋 하나, 규칙 단위 롤백 가능)";
        private const string CommitDoneSuffix = "개 파일 수정됨 — 규칙별로 커밋했습니다. git log 로 확인하세요.";

        // [규칙별 커밋 생성] 툴팁. git 아닌 대상에서는 "왜 잠겼는지"를 그 자리에서 말한다(XAML 이 아니라 여기가 단일 진실).
        private const string CommitToolTip = "켜면 규칙마다 커밋을 만들어 규칙 단위로 되돌릴 수 있습니다. 끄면 파일만 수정하고 커밋은 직접 하십시오.";
        private const string CommitNoGitToolTip = "대상 폴더가 git 저장소가 아니라 규칙별 커밋을 만들 수 없습니다. "
            + "위 안내의 [git 저장소 만들기]로 기준 커밋을 만들거나, 백업·다른 버전관리(SVN 등)로 복원 수단을 확보하세요.";

        /// <summary>실행 모드 안내(요약바·툴팁). 규칙별 커밋 체크 상태에 따라 갈린다.</summary>
        private string ModeNotice => CommitCheck?.IsChecked == true ? CommitNotice : NoCommitNotice;

        /// <summary>실행 종료 안내 접미사. 커밋 여부에 따라 다음 행동(git diff / git log)이 달라진다.</summary>
        private string ModeDoneSuffix => CommitCheck?.IsChecked == true ? CommitDoneSuffix : NoCommitDoneSuffix;

        /// <summary>실행 시작 로그 한 줄. 러너에 실제로 넘기는 스위치와 반드시 같은 것을 말한다(BuildJobs 참조).</summary>
        private string ModeRunLogLine => CommitCheck?.IsChecked == true
            ? "커밋: 규칙별로 커밋함 (러너에 -Commit — 규칙 하나 = 커밋 하나, git log 로 확인)"
            : "커밋: 하지 않음 (러너에 -NoCommit — 검토 후 git 으로 직접 커밋하세요)";

        // review-needed 규칙 목록([코드 규칙]). 단일 진실은 엔진 README
        // (tools\_internal\SparrowSyntaxFix\README.md) 규칙 표의 'Commit policy' 열이고,
        // 러너 Run-SparrowSyntaxFix.ps1 의 $labels '검토필요:' 접두(→ 커밋 접두 'sparrow(rule)! ')와
        // 아래 목록·XAML 의 '[검토필요] ' 라벨이 전부 그 표를 따라간다. 셋이 어긋나면 "검토필요 커밋만
        // revert" 작업에서 위험 규칙이 통째로 누락된다(실제로 forvar/fieldsplit/emptystmt/objectinitializer 가 그랬다).
        private CheckBox[] ReviewNeededCodeRules => new[]
        {
            ASForeachCast, ASObjectInitializer, ASNullVar, ASObjectVarNarrowing, ASLocalConst,
            ASArrayVarNarrowing, ASForVar, ASFieldSplit, ASEmptyStmt, ASForHoist,
        };

        /// <summary>review-needed 규칙 목록([주석·레이아웃]). 러너 Run-SparrowCommentFix.ps1 의 $labels 와 일치한다.</summary>
        private CheckBox[] ReviewNeededCommentRules => new[] { BBlockPromote };

        private void CommitCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateRunButtonForMode();
            UpdateSummary();
        }

        // 생성 파일(.g.cs/.designer.cs/obj·bin 등)은 GUI 에서 언제나 제외한다 — 빌드가 다시 만들어 내므로 고칠
        // 이유가 없다. 굳이 포함해야 하는 자동화는 CLI 러너의 -IncludeGenerated 를 쓴다.
        private const bool IncludeGeneratedFiles = false;

        private readonly Dictionary<string, RuleInfo> _ruleInfos = new Dictionary<string, RuleInfo>(StringComparer.Ordinal);
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _scopeCts;
        private CancellationTokenSource? _xlsScopeCts;
        private Process? _currentProcess;
        private string? _lastXlsOutputDir;
        private SourceScope? _currentScope;

        // 대상 루트가 git 저장소가 아니라서 [규칙별 커밋 생성]을 잠근 상태인가. UpdateGitState 가 유일한 기록자다.
        // 실행 버튼은 이 값과 무관하다 — git 이 없어도 실행은 허용한다(사내 SVN 사용처).
        private bool _commitBlockedByGit;

        // XLS 분리 대분류의 범위 트리(로컬 소스 스캔이 아니라 xls 검출 경로로 만든다).
        private XlsScope? _currentXlsScope;

        // 매핑 패널 갱신 세대. 로드시 목록화(xls census)와 실행후 갱신(출력 트리)이 겹칠 수 있어, 가장 나중에
        // 시작한 갱신만 적용되도록 세대 번호로 오래된 백그라운드 결과를 버린다(마지막 시작이 이긴다).
        private int _mappingRefreshGen;

        public ObservableCollection<SourceScopeNode> ScopeRoots { get; } = new ObservableCollection<SourceScopeNode>();

        /// <summary>XLS 분리 대분류의 범위 트리 루트(리프의 FullPath = xls 원본 경로 문자열).</summary>
        public ObservableCollection<SourceScopeNode> XlsScopeRoots { get; } = new ObservableCollection<SourceScopeNode>();

        // [XLS 분리] 검출 체커(키·건수). xls 로드 시(census) 또는 실행 후(출력 트리)에 채워진다. [체커 규칙 관리]
        // 창으로 전달하고, 메인 요약 "검출 체커 N종 · 매핑 M · 미매핑 K"(지정 기준) 계산에 쓴다.
        private List<(string Key, int Count)> _xlsCheckers = new List<(string Key, int Count)>();

        // 열려 있는 규칙 관리 창(모덜리스). 중복 오픈 방지 + 닫힐 때 메인 요약을 지정 기준으로 다시 계산한다.
        private RuleManagerWindow? _ruleManager;

        public MainWindow()
        {
            InitializeComponent();

            StartupOptions startup = StartupOptions.Parse(Environment.GetCommandLineArgs());
            _skillRoot = ResolveSkillRoot();
            _toolsDir = Path.Combine(_skillRoot, "tools");
            _guidesDir = !string.IsNullOrWhiteSpace(startup.GuidesDir)
                ? Path.GetFullPath(startup.GuidesDir!.Trim().Trim('"'))
                : Path.Combine(_skillRoot, "references", "checkers");
            _startupXls = startup.Xls;
            _startupXlsOut = startup.XlsOut;
            _startupXlsAutorun = startup.XlsAutorun;
            _startupOpenRuleManager = startup.OpenRuleManager;

            // 파일 로그를 첫 AppendLog 전에 연다(준비 로그부터 파일에 남게).
            _sessionLog = SessionLog.Create(startup.LogDir);
            _sessionLog.WriteHeader(_skillRoot, _guidesDir, Environment.GetCommandLineArgs());

            HookCrashLogging();

            AppendLog("GUI 준비 완료");
            AppendLog(_sessionLog.FilePath != null
                ? "세션 로그: " + _sessionLog.FilePath
                : "세션 로그 파일을 열 수 없습니다(화면 로그만 유지): " + _sessionLog.LogDirectory);
            if (startup.GuidesDir != null) AppendLog("guides-dir override: " + _guidesDir);
            // 스냅샷 기록기는 --screenshot-dir 가 있을 때만 만들어진다(없으면 null → 캡처/감시 모두 없음).
            _snapshots = SnapshotRecorder.Create(startup.ScreenshotDir, this, AppendLog);
            InitializeRuleInfo();
            ShowRuleInfo(nameof(ASObjectVarSafe));
            Loaded += async (_, _) =>
            {
                UpdateGitState();   // 커밋 체크박스 툴팁/활성 상태의 첫 확정(UpdateRunButtonForMode 보다 먼저)
                UpdateRunButtonForMode();
                UpdateSummary();
                ApplyStartupXlsPrefill();
                // 관리창을 열기 전에 검출 체커를 확실히 로드한다(census). --open-rule-manager 경로가 빈 목록으로
                // 열리지 않도록 프리필된 xls 를 여기서 await 한다. 범위 트리도 같은 xls 로 함께 만든다.
                if (!string.IsNullOrWhiteSpace(XlsPathBox.Text))
                {
                    await RefreshCheckerSummaryFromXlsAsync();
                    await RefreshXlsScopeAsync();
                }
                await RefreshScopeAsync(showErrors: false);
                // 자동 지점 1: 메인 창 로드 완료. ContextIdle 은 Loaded/Render/DataBind 뒤에 실행되므로 여기서
                // 한 번 양보하면 레이아웃·바인딩이 끝난 창을 찍는다(관리창을 열기 전 = 순번 01 이 메인창).
                if (_snapshots != null)
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    _snapshots.Capture(this, "main-loaded");
                }
                if (_startupOpenRuleManager) OpenRuleManager();
                if (_startupXlsAutorun) await AutoRunXlsSplitAsync();
            };
        }

        // [XLS 분리] 기동 인자를 UI에 반영한다: xls/출력 프리필이 있으면 [XLS 분리] 대분류를 선택하고 경로 상자를 채운다.
        // (autorun만 준 경우에도 XLS 분리 화면으로 전환한다.)
        private void ApplyStartupXlsPrefill()
        {
            bool any = false;
            if (!string.IsNullOrWhiteSpace(_startupXls))
            {
                XlsPathBox.Text = _startupXls!.Trim().Trim('"');
                any = true;
            }
            if (!string.IsNullOrWhiteSpace(_startupXlsOut))
            {
                XlsOutputPathBox.Text = _startupXlsOut!.Trim().Trim('"');
                any = true;
            }
            if (any || _startupXlsAutorun)
            {
                SectionTabs.SelectedItem = SectionXlsTab;   // SelectionChanged가 버튼/요약/안내를 갱신
            }
        }

        // autorun: 실제 소스 트리 없이 xls만으로 [XLS 분리] 를 구동한다(테스트 하네스 경로). 스코프 필터(FilesFrom)는
        // 비워 전건을 익스포트한다. 그 외에는 RunButton 의 인프로세스 경로(SparrowExporter.Run + CheckerRuleMapper.Apply)를
        // 그대로 재사용한다.
        private async Task AutoRunXlsSplitAsync()
        {
            string xlsPath = XlsPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(xlsPath) || !File.Exists(xlsPath))
            {
                AppendLog("autorun 취소: [XLS 분리] XLS 를 찾을 수 없습니다 (" + xlsPath + ")");
                return;
            }
            if (_cts != null) return;   // 이미 실행 중

            _cts = new CancellationTokenSource();
            SetRunning(true);
            _lastXlsOutputDir = null;
            OpenXlsOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog(">>> autorun: [XLS 분리] (스코프 필터 없음 · 전건)");
            AppendLog("xls: " + xlsPath);
            AppendLog(new string('-', 72));
            try
            {
                _lastXlsOutputDir = await RunXlsSplitAsync(xlsPath, sourceRoot: "", filesFrom: "", _cts.Token);
                OpenXlsOutputButton.IsEnabled = Directory.Exists(_lastXlsOutputDir);
                await RefreshCheckerSummaryFromOutputAsync(_lastXlsOutputDir);
                StatusText.Text = "완료";
                SummaryModeText.Text = "autorun 완료. 지정된 규칙만 부착됩니다.";
                AppendLog(new string('-', 72));
                AppendLog("autorun 완료");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "중지됨";
                AppendLog("autorun 중지");
            }
            catch (Exception ex)
            {
                StatusText.Text = "실패";
                SummaryModeText.Text = "autorun 중 오류가 발생했습니다. 로그를 확인하세요.";
                AppendLog("autorun 오류: " + ex.Message);
            }
            finally
            {
                _currentProcess = null;
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
                UpdateSummary();
                // 자동 지점 3(autorun 경로): [XLS 분리] 실행 완료 후(메인 창).
                _snapshots?.CaptureWhenIdle(this, "after-run");
            }
        }

        // XLS 분리 대분류가 선택되어 있나(= [XLS 분리] 화면).
        private bool IsXlsSection() => ReferenceEquals(SectionTabs.SelectedItem, SectionXlsTab);

        private ActiveMode CurrentMode()
        {
            if (IsXlsSection()) return ActiveMode.XlsSplit;   // XLS 분리 화면은 항상 [XLS 분리]
            object? selected = RulesTabs.SelectedItem;
            if (ReferenceEquals(selected, CodeRuleTab)) return ActiveMode.CodeRule;
            if (ReferenceEquals(selected, CommentTab)) return ActiveMode.Comment;
            return ActiveMode.None; // 방어용: 로드 전 등 어느 하위 탭도 선택되지 않은 순간
        }

        private void UpdateRunButtonForMode()
        {
            // 대분류별로 의미 없는 보조 버튼은 아예 감춘다(대상 폴더 = [코드 자동수정] 전용, 출력 폴더 = XLS 분리 전용).
            // 비활성으로만 두면 "쓸 수 없는 버튼이 계속 보이는" 상태라 대분류를 나눈 취지(관련 없는 UI를
            // 아예 안 보이게)에 어긋난다 — 스냅샷 PNG 로 확인해 Visibility 제어로 바꿨다.
            bool xls = IsXlsSection();
            OpenTargetButton.Visibility = xls ? Visibility.Collapsed : Visibility.Visible;
            OpenXlsOutputButton.Visibility = xls ? Visibility.Visible : Visibility.Collapsed;
            OpenTargetButton.IsEnabled = !xls && _cts == null;
            OpenXlsOutputButton.IsEnabled = xls && _cts == null && Directory.Exists(_lastXlsOutputDir ?? "");

            // 규칙별 커밋은 자동수정 러너가 만드는 것이다. [XLS 분리]는 읽기전용이라 커밋이 없으므로 숨긴다 —
            // 눌러도 아무 의미가 없는 옵션을 남겨 두지 않는다.
            ActiveMode mode = CurrentMode();
            bool commitApplies = !xls;
            CommitCheck.Visibility = commitApplies ? Visibility.Visible : Visibility.Collapsed;
            // git 아닌 대상에서는 커밋 옵션만 잠근다(_commitBlockedByGit). 실행 버튼은 아래 switch 에서
            // 이 값을 보지 않는다 — 되돌릴 수단이 없다는 경고는 하되 실행 자체는 막지 않는다.
            CommitCheck.IsEnabled = commitApplies && _cts == null && !_commitBlockedByGit;

            switch (mode)
            {
                case ActiveMode.CodeRule:
                    RunButton.Content = "코드 규칙 수정 실행";
                    RunButton.ToolTip = ModeNotice;
                    RunButton.IsEnabled = _cts == null;
                    break;
                case ActiveMode.Comment:
                    RunButton.Content = "주석·레이아웃 수정 실행";
                    RunButton.ToolTip = ModeNotice;
                    RunButton.IsEnabled = _cts == null;
                    break;
                case ActiveMode.XlsSplit:
                    RunButton.Content = "XLS 분리 실행";
                    RunButton.ToolTip = null;
                    RunButton.IsEnabled = _cts == null;
                    break;
                default:
                    // 방어용 폴백. 정상 상태에서는 도달하지 않는다.
                    RunButton.Content = "실행";
                    RunButton.ToolTip = "실행할 탭([코드 규칙] / [주석·레이아웃])을 선택하세요.";
                    RunButton.IsEnabled = false;
                    break;
            }
        }

        // 대분류 전환: 화면이 통째로 바뀌므로 실행 버튼/안내/요약을 그 대분류 기준으로 다시 맞춘다.
        private void SectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, SectionTabs)) return;
            if (!IsLoaded) return;

            UpdateRunButtonForMode();
            if (!IsXlsSection())
            {
                // [코드 자동수정] 화면으로 돌아오면 하위 탭에 맞는 설명을 다시 띄운다.
                switch (CurrentMode())
                {
                    case ActiveMode.Comment: ShowRuleInfo(nameof(BTrailing)); break;
                    default: ShowRuleInfo(nameof(ASObjectVarSafe)); break;
                }
            }
            UpdateSummary();
        }

        private void RulesTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, RulesTabs)) return;
            // TabControl 초기 선택은 InitializeComponent 도중(다른 명명 요소 생성 전) 발생할 수 있으므로 로드 후에만 처리한다.
            if (!IsLoaded) return;

            UpdateRunButtonForMode();
            switch (CurrentMode())
            {
                case ActiveMode.CodeRule:
                    ShowRuleInfo(nameof(ASObjectVarSafe));
                    break;
                case ActiveMode.Comment:
                    ShowRuleInfo(nameof(BTrailing));
                    break;
            }
            UpdateSummary();
        }

        // 대상 선택은 '폴더' 하나뿐이다. 예전의 [파일 선택](.sln/.csproj 다이얼로그)은 제거했다 —
        // 어떤 파일을 고르든 ResolveTargetRoot 가 부모 폴더로 환원하므로 [폴더 선택]과 결과가 같았고,
        // 두 버튼이 "sln 을 골라야 하는가"라는 오해만 만들었다.
        // 경로를 직접 입력/붙여넣는 경로는 그대로라 .sln/.csproj 문자열도 계속 받아들인다.
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "소스 폴더 선택"
            };
            string current = TargetPathBox.Text.Trim();
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            else
            {
                // .sln/.csproj 를 붙여넣어 둔 상태면 그 부모 폴더에서 고르기 시작한다.
                string? parent = SafeParentDirectory(current);
                if (parent != null) dlg.InitialDirectory = parent;
            }
            if (dlg.ShowDialog(this) == true)
            {
                TargetPathBox.Text = dlg.FolderName;
            }
        }

        /// <summary>파일 경로면 그 부모 폴더, 아니면 null. 예외는 삼킨다(경로가 깨져 있어도 UI 가 죽지 않게).</summary>
        private static string? SafeParentDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                return Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch { return null; }
        }

        private void BrowseXlsButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Sparrow 결과 XLS 선택",
                Filter = "Sparrow 결과 (*.xls;*.xlsx)|*.xls;*.xlsx|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                XlsPathBox.Text = dlg.FileName;
            }
        }

        private void BrowseXlsOutputButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "[XLS 분리] 출력 폴더 선택"
            };
            string current = XlsOutputPathBox.Text.Trim();
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
            {
                XlsOutputPathBox.Text = dlg.FolderName;
            }
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 활성 탭이 곧 실행 기능이다. 옵션 탭(None)은 실행 대상이 아니며 버튼도 비활성이지만 방어적으로 가드한다.
            ActiveMode mode = CurrentMode();
            if (mode == ActiveMode.None)
            {
                return;
            }

            // [XLS 분리] 는 입력이 xls 하나다. 대상/범위 선택은 선택 사항(팀 분담 필터)이라 별도 경로로 처리한다.
            if (mode == ActiveMode.XlsSplit)
            {
                await RunXlsSplitInteractiveAsync();
                return;
            }

            bool runCodeRule = mode == ActiveMode.CodeRule;
            bool runComment = mode == ActiveMode.Comment;

            string target = TargetPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                MessageBox.Show(this, "대상 폴더를 먼저 선택하세요.", "입력 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SourceScope scope = await EnsureScopeAsync(target);
            IReadOnlyList<string> selectedFiles = scope.SelectedFiles;
            if (selectedFiles.Count == 0)
            {
                MessageBox.Show(this, "선택된 .cs 파일이 없습니다. 좌측 작업 범위에서 파일을 선택하세요.", "범위 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string scopeManifest;
            try
            {
                scopeManifest = ScopeManifestWriter.WriteTemp(selectedFiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "범위 manifest 생성 실패: " + ex.Message, "범위 확인",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var jobs = BuildJobs(target, scopeManifest, runCodeRule, runComment);
            if (jobs.Count == 0)
            {
                TryDeleteFile(scopeManifest);
                MessageBox.Show(this, "실행할 규칙을 하나 이상 선택하세요.", "규칙 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 실행 전 대상 파일의 지문(쓰기 시각·길이)을 떠 둔다. 끝난 뒤 이걸로 "실제로 수정된 파일 수"를 세어
            // 커밋하지 않았다는 안내와 함께 알린다(러너 출력 문구 파싱에도, git 존재에도 의존하지 않는다).
            Dictionary<string, FileStamp> beforeStamps = await Task.Run(() => SnapshotFileStamps(selectedFiles));

            _cts = new CancellationTokenSource();
            SetRunning(true);
            _lastXlsOutputDir = null;
            OpenXlsOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog("target: " + target);
            AppendLog("scope: " + selectedFiles.Count + " selected / " + scope.TotalFiles + " discovered"
                      + (scope.ExcludedFiles > 0 ? " / " + scope.ExcludedFiles + " excluded" : ""));
            AppendLog("jobs: " + jobs.Count);
            AppendLog(ModeRunLogLine);
            AppendLog(new string('-', 72));

            try
            {
                foreach (RunnerJob job in jobs)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    SummaryModeText.Text = job.Name + " 실행 중";
                    await RunJobAsync(job, _cts.Token);
                }

                StatusText.Text = "완료";
                AppendLog(new string('-', 72));
                AppendLog("완료. 빌드와 Sparrow 재분석으로 결과를 확인하세요.");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "중지됨";
                AppendLog(new string('-', 72));
                AppendLog("사용자 중지");
            }
            catch (Exception ex)
            {
                StatusText.Text = "실패";
                AppendLog(new string('-', 72));
                AppendLog("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message, "실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _currentProcess = null;
                _cts.Dispose();
                _cts = null;
                TryDeleteFile(scopeManifest);
                SetRunning(false);
                UpdateSummary();
                // 결과 안내는 UpdateSummary 뒤에 써야 화면에 남는다(요약은 항상 현재 선택 기준으로 다시 계산된다).
                // 완료/중지/실패 어느 경로든 "이미 고쳐진 파일이 몇 개인지 + 커밋은 안 했다"를 똑같이 알린다.
                string notice = CountChangedFiles(beforeStamps) + ModeDoneSuffix;
                SummaryModeText.Text = notice;
                AppendLog(notice);
            }
        }

        /// <summary>대상 파일 1개의 실행 전 지문(마지막 쓰기 시각 · 길이).</summary>
        private readonly record struct FileStamp(DateTime WrittenUtc, long Length);

        // 읽지 못한 파일(권한/잠금)은 스냅샷에서 빠진다 — 없는 것을 "바뀌었다"고 세는 오탐 대신 누락 쪽으로 안전하다.
        private static Dictionary<string, FileStamp> SnapshotFileStamps(IReadOnlyList<string> files)
        {
            var stamps = new Dictionary<string, FileStamp>(files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) stamps[path] = new FileStamp(info.LastWriteTimeUtc, info.Length);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return stamps;
        }

        private static int CountChangedFiles(Dictionary<string, FileStamp> before)
        {
            int changed = 0;
            foreach (KeyValuePair<string, FileStamp> entry in before)
            {
                try
                {
                    var info = new FileInfo(entry.Key);
                    if (!info.Exists || info.LastWriteTimeUtc != entry.Value.WrittenUtc || info.Length != entry.Value.Length)
                    {
                        changed++;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return changed;
        }

        // [XLS 분리] 실행(사용자 트리거). 입력은 xls 하나이고 프로젝트 경로는 쓰지 않는다. 범위 트리에서
        // 고른 항목이 있으면 그 xls 원본 경로를 그대로 manifest 로 넘겨(RootPath 없이) 팀 분담 필터로 쓰고, 아무것도
        // 고르지 않으면 전건이다. export + CheckerRuleMapper.Apply(캐시 반영)로 실행 전에 저장해 둔 지정이 자동
        // 부착되고, 이후 요약을 실행 결과(상태·건수)로 최신화한다.
        private async Task RunXlsSplitInteractiveAsync()
        {
            string xlsPath = XlsPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(xlsPath) || !File.Exists(xlsPath))
            {
                MessageBox.Show(this, "Sparrow 결과 XLS 파일을 먼저 선택하세요.", "입력 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_cts != null) return;   // 이미 실행 중

            // 범위는 선택 사항: xls 범위 트리에서 고른 경로가 있으면 그대로 필터, 없으면 전건.
            // RootPath 는 넘기지 않는다 — xls 자기 경로로 xls 를 거르므로 로컬 소스 루트 개념이 필요 없고,
            // 그래서 팀원마다 체크아웃 위치가 달라도 매칭이 어긋날 수 없다.
            string sourceRoot = "";
            string filesFrom = "";
            string? scopeManifest = null;
            IReadOnlyList<string> selectedXlsPaths = _currentXlsScope?.SelectedPaths ?? Array.Empty<string>();
            if (selectedXlsPaths.Count > 0)
            {
                try
                {
                    scopeManifest = ScopeManifestWriter.WriteTempRaw(selectedXlsPaths);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "범위 manifest 생성 실패: " + ex.Message, "범위 확인",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                filesFrom = scopeManifest;
                AppendLog("범위 선택: 파일 " + selectedXlsPaths.Count + "개 · 검출 "
                          + (_currentXlsScope?.SelectedItems ?? 0) + "건 (xls 경로 기준)");
            }

            _cts = new CancellationTokenSource();
            SetRunning(true);
            _lastXlsOutputDir = null;
            OpenXlsOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog(">>> XLS 분리 실행([XLS 분리])" + (filesFrom.Length > 0 ? " (범위 필터 적용)" : " (범위 선택 없음 · 전건)"));
            AppendLog("입력 xls: " + xlsPath);
            AppendLog(new string('-', 72));
            try
            {
                _lastXlsOutputDir = await RunXlsSplitAsync(xlsPath, sourceRoot, filesFrom, _cts.Token);
                OpenXlsOutputButton.IsEnabled = Directory.Exists(_lastXlsOutputDir);
                await RefreshCheckerSummaryFromOutputAsync(_lastXlsOutputDir);
                StatusText.Text = "완료";
                SummaryModeText.Text = "실행 완료. 지정된 규칙만 부착됩니다.";
                AppendLog(new string('-', 72));
                AppendLog("완료");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "중지됨";
                SummaryModeText.Text = "사용자가 실행을 중지했습니다.";
                AppendLog("사용자 중지");
            }
            catch (Exception ex)
            {
                StatusText.Text = "실패";
                SummaryModeText.Text = "실행 중 오류가 발생했습니다. 로그를 확인하세요.";
                AppendLog("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message, "실행 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _currentProcess = null;
                _cts.Dispose();
                _cts = null;
                TryDeleteFile(scopeManifest);
                SetRunning(false);
                UpdateSummary();
                // 자동 지점 3: [XLS 분리] 실행 완료 후(메인 창) — 실행 결과가 반영된 요약/매핑 패널이 찍힌다.
                _snapshots?.CaptureWhenIdle(this, "after-run");
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            try
            {
                if (_currentProcess != null && !_currentProcess.HasExited)
                {
                    _currentProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                AppendLog("중지 실패: " + ex.Message);
            }
        }

        private void OpenTargetButton_Click(object sender, RoutedEventArgs e)
        {
            string target = TargetPathBox.Text.Trim().Trim('"');
            string? dir = null;
            if (Directory.Exists(target)) dir = target;
            else if (File.Exists(target)) dir = Path.GetDirectoryName(target);

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "열 수 있는 대상 폴더가 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }

        private void OpenXlsOutputButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastXlsOutputDir) || !Directory.Exists(_lastXlsOutputDir))
            {
                MessageBox.Show(this, "열 수 있는 [XLS 분리] 출력 폴더가 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = _lastXlsOutputDir, UseShellExecute = true });
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
        }

        private void TargetPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSummary();
            _ = RefreshScopeAsync(showErrors: false);
        }

        // XLS 경로가 설정되는 순간(찾아보기 선택 OR 시작 인자 --xls 프리필) 실행(export) 없이 체커와 검출
        // 경로만 파싱해 요약·범위 트리를 즉시 채운다. 경로 상자는 IsReadOnly 라 프로그램적 설정 시에만 발생한다.
        private void XlsPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _ = RefreshCheckerSummaryFromXlsAsync();
            _ = RefreshXlsScopeAsync();
        }

        private async void RefreshScopeButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScopeAsync(showErrors: true);
        }

        private void SelectAllScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in ScopeRoots) root.SetSubtree(true);
            UpdateSummary();
        }

        private void ClearScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in ScopeRoots) root.SetSubtree(false);
            UpdateSummary();
        }

        private void SelectAllXlsScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in XlsScopeRoots) root.SetSubtree(true);
            UpdateSummary();
        }

        private void ClearXlsScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in XlsScopeRoots) root.SetSubtree(false);
            UpdateSummary();
        }

        // 두 범위 트리(로컬 소스 / xls 경로)가 같은 노드 템플릿을 공유하므로 핸들러도 공유한다.
        private void ScopeCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSummary();
        }

        // xls 경로가 정해지면 그 xls 의 검출 경로(ListPaths — 무작성)로 범위 트리를 다시 만든다. 선택 상태는
        // 유지하지 않는다(다른 xls = 다른 경로 집합). 실패/빈 입력은 빈 트리 + 안내 문구.
        private async Task RefreshXlsScopeAsync()
        {
            _xlsScopeCts?.Cancel();
            _xlsScopeCts?.Dispose();
            _xlsScopeCts = new CancellationTokenSource();
            CancellationToken token = _xlsScopeCts.Token;

            string xls = XlsPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(xls) || !File.Exists(xls))
            {
                _currentXlsScope = null;
                XlsScopeRoots.Clear();
                UpdateXlsCommonPath(null);
                UpdateXlsScopeSummary();
                return;
            }

            try
            {
                XlsScopeSummary.Text = "XLS 검출 경로를 읽는 중...";
                XlsScope scope = await XlsScopeDiscovery.DiscoverAsync(xls, token);
                if (token.IsCancellationRequested) return;

                _currentXlsScope = scope;
                XlsScopeRoots.Clear();
                foreach (SourceScopeNode root in scope.Roots) XlsScopeRoots.Add(root);
                UpdateXlsCommonPath(scope.CommonPrefix);
                UpdateXlsScopeSummary();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _currentXlsScope = null;
                XlsScopeRoots.Clear();
                UpdateXlsCommonPath(null);
                XlsScopeSummary.Text = "범위 트리 생성 실패: " + ex.Message;
            }
        }

        // 공통 접두 캡션. 실 xls 는 모든 경로가 D:\Work\...\release\<날짜>\ 를 공유해서, 접지 않으면 의미 없는
        // 단일 자식 노드를 여러 번 파고들어야 실제 프로젝트 폴더가 나온다. 접은 경로는 트리 위에 한 줄로 보여 주고
        // (길면 말줄임 + ToolTip 에 전체), 접두가 없으면 캡션을 숨긴다. 표시만 접는 것이고 선택/매칭은 원본 경로 전체.
        private void UpdateXlsCommonPath(string? prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                XlsScopeCommonPath.Text = "";
                XlsScopeCommonPath.ToolTip = null;
                XlsScopeCommonPathBox.Visibility = Visibility.Collapsed;
                return;
            }

            XlsScopeCommonPath.Text = "공통 경로: " + prefix;
            XlsScopeCommonPath.ToolTip = prefix;
            XlsScopeCommonPathBox.Visibility = Visibility.Visible;
        }

        // "전체 N개 파일 · M건 / 선택 ..." 요약. 아무것도 체크하지 않은 상태 = 전건(필터 없음)임을 분명히 알린다.
        private void UpdateXlsScopeSummary()
        {
            XlsScope? scope = _currentXlsScope;
            if (scope == null || scope.TotalFiles == 0)
            {
                XlsScopeSummary.Text = "XLS를 선택하면 검출 경로 트리가 만들어집니다.";
                return;
            }

            int selectedFiles = scope.SelectedFileCount;
            if (selectedFiles == 0)
            {
                XlsScopeSummary.Text = "전체 " + scope.TotalFiles + "개 파일 · " + scope.TotalItems
                                       + "건 · 선택 없음(전건 분리)";
                return;
            }

            XlsScopeSummary.Text = "선택 " + selectedFiles + "개 파일 · " + scope.SelectedItems + "건"
                                   + " (전체 " + scope.TotalFiles + " · " + scope.TotalItems + "건)";
        }

        private async Task RefreshScopeAsync(bool showErrors)
        {
            if (!IsLoaded && !showErrors) return;

            string target = TargetPathBox.Text.Trim().Trim('"');
            // 대상 루트가 정해지는 지점 = git 여부를 다시 판정할 지점.
            UpdateGitState();
            if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                _currentScope = null;
                ScopeRoots.Clear();
                ScopeStatusText.Text = "대상 경로를 선택하세요.";
                UpdateSummary();
                return;
            }

            _scopeCts?.Cancel();
            _scopeCts?.Dispose();
            _scopeCts = new CancellationTokenSource();
            CancellationToken token = _scopeCts.Token;

            try
            {
                ScopeStatusText.Text = "소스 파일을 탐색하는 중...";
                SourceScope? previousScope = _currentScope;
                HashSet<string>? previousSelection = null;
                string expectedRoot = ResolveTargetRoot(target);
                if (previousScope != null && SamePath(previousScope.RootPath, expectedRoot))
                {
                    int previousSelectable = previousScope.RootNode.EnumerateFiles()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    IReadOnlyList<string> selected = previousScope.SelectedFiles;
                    if (selected.Count > 0 && selected.Count < previousSelectable)
                    {
                        previousSelection = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    }
                }

                SourceScope scope = await SourceScopeDiscovery.DiscoverAsync(target, IncludeGeneratedFiles, token);
                if (token.IsCancellationRequested) return;
                if (previousSelection != null)
                {
                    scope.RootNode.ApplySelection(previousSelection);
                }

                _currentScope = scope;
                ScopeRoots.Clear();
                ScopeRoots.Add(scope.RootNode);
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _currentScope = null;
                ScopeRoots.Clear();
                ScopeStatusText.Text = "범위 탐색 실패: " + ex.Message;
                if (showErrors)
                {
                    MessageBox.Show(this, ex.Message, "범위 탐색 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task<SourceScope> EnsureScopeAsync(string target)
        {
            UpdateGitState();   // 실행 직전에도 대상 루트 기준으로 다시 판정한다(경로가 그 사이 바뀌었을 수 있다)
            string expectedRoot = ResolveTargetRoot(target);
            if (_currentScope != null && SamePath(_currentScope.RootPath, expectedRoot))
            {
                return _currentScope;
            }

            SourceScope scope = await SourceScopeDiscovery.DiscoverAsync(target, IncludeGeneratedFiles, CancellationToken.None);
            _currentScope = scope;
            ScopeRoots.Clear();
            ScopeRoots.Add(scope.RootNode);
            UpdateSummary();
            return scope;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        // ===== git 게이트 =====
        //
        // 자동수정은 소스 파일을 실제로 고친다. git 이 없으면 되돌릴 수단이 없는데, 예전에는 그 상태로 -Commit 을
        // 넘겨 "파일은 이미 고쳐졌는데 커밋만 실패" + "git 락 재시도 실패"라는 오진까지 나왔다(실측).
        // 그래서 대상 루트가 정해질 때마다 git 여부를 판정해 커밋 옵션만 잠그고 조치를 안내한다.
        // 실행 버튼은 건드리지 않는다 — 사내에 SVN 사용처가 있어 git 없다고 차단하면 그쪽이 도구를 못 쓴다.

        /// <summary>
        /// 루트에서 위로 올라가며 <c>.git</c> 을 찾는다(= git 자신의 판정). 하위 폴더를 골라도 상위가 저장소면 git 이다.
        /// worktree/submodule 은 <c>.git</c> 이 파일이므로 디렉토리·파일 둘 다 인정한다. 찾으면 그 저장소 루트, 없으면 null.
        /// </summary>
        private static string? FindGitRepositoryRoot(string startPath)
        {
            try
            {
                DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(startPath));
                for (int depth = 0; dir != null && depth < 64; depth++, dir = dir.Parent)
                {
                    string marker = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(marker) || File.Exists(marker)) return dir.FullName;
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            return null;
        }

        /// <summary>대상 루트의 git 상태를 다시 판정해 커밋 옵션과 안내를 맞춘다(범위 탐색 시점마다 호출).</summary>
        private void UpdateGitState()
        {
            string target = TargetPathBox.Text.Trim().Trim('"');
            bool hasTarget = !string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target));
            // 대상이 아직 없으면 잠그지 않는다(고르기 전부터 옵션이 죽어 있으면 이유를 알 수 없다).
            string root = hasTarget ? ResolveTargetRoot(target) : "";
            _commitBlockedByGit = hasTarget && FindGitRepositoryRoot(root) == null;

            if (_commitBlockedByGit)
            {
                GitNoticeText.Text = "이 폴더는 git 저장소가 아닙니다 (" + root + "). 자동수정은 소스 파일을 실제로 고치는데 "
                    + "되돌릴 수단이 없습니다. [git 저장소 만들기]로 기준 커밋을 남기거나, 백업·다른 버전관리(SVN 등)로 "
                    + "복원 수단을 확보한 뒤 실행하세요. 실행은 막지 않습니다 — [규칙별 커밋 생성]만 꺼 둡니다.";
                GitNoticeBox.Visibility = Visibility.Visible;
                if (CommitCheck.IsChecked == true) CommitCheck.IsChecked = false;
            }
            else
            {
                GitNoticeBox.Visibility = Visibility.Collapsed;
            }

            CommitCheck.ToolTip = _commitBlockedByGit ? CommitNoGitToolTip : CommitToolTip;
            UpdateRunButtonForMode();
        }

        private async void InitGitButton_Click(object sender, RoutedEventArgs e)
        {
            string target = TargetPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                MessageBox.Show(this, "대상 폴더를 먼저 선택하세요.", "git 저장소 만들기",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string root = ResolveTargetRoot(target);
            MessageBoxResult confirm = MessageBox.Show(this,
                "'" + root + "' 에 git 저장소를 만들까요?" + Environment.NewLine
                + "git init → git add -A → git commit -m \"baseline\" 을 실행해 지금 상태를 기준 커밋으로 남깁니다."
                + Environment.NewLine + "이후 자동수정 결과를 git diff 로 검토하고 되돌릴 수 있습니다.",
                "git 저장소 만들기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            InitGitButton.IsEnabled = false;
            try
            {
                AppendLog("");
                AppendLog(">>> git 저장소 만들기: " + root);
                IReadOnlyList<string> lines = await Task.Run(() => InitGitRepository(root));
                foreach (string line in lines) AppendLog(line);
            }
            catch (Exception ex)
            {
                AppendLog("git 저장소 만들기 실패: " + ex.Message);
            }
            finally
            {
                InitGitButton.IsEnabled = true;
                // 성공했으면 여기서 다시 판정되어 커밋 체크박스가 열리고 안내가 사라진다. 실패면 사유는 위 로그에 남는다.
                UpdateGitState();
            }
        }

        /// <summary>git init → add -A → commit 을 순서대로 실행하고, 각 단계의 종료코드·출력을 로그 줄로 돌려준다.</summary>
        private static IReadOnlyList<string> InitGitRepository(string root)
        {
            var log = new List<string>();
            (string Label, string[] Args)[] steps =
            {
                ("git init", new[] { "init" }),
                ("git add -A", new[] { "add", "-A" }),
                ("git commit -m \"baseline\"", new[] { "commit", "-m", "baseline" }),
            };

            foreach ((string label, string[] args) in steps)
            {
                (int exitCode, string output) = RunGit(root, args);
                log.Add("  " + label + " → exit=" + exitCode);
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length > 0) log.Add("    | " + line);
                }
                if (exitCode != 0)
                {
                    log.Add("  중단: " + label + " 실패 — 위 사유를 확인하세요(git 미설치/PATH · user.name/user.email 미설정 등).");
                    return log;
                }
            }

            log.Add("  완료: 기준 커밋(baseline)을 만들었습니다. 이제 [규칙별 커밋 생성]을 켤 수 있습니다.");
            return log;
        }

        /// <summary>git 한 번 실행. 출력은 전부 회수해 로그로만 흘린다(콘솔/화면에 usage 도움말이 새지 않는다).</summary>
        private static (int ExitCode, string Output) RunGit(string workingDirectory, params string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in arguments) psi.ArgumentList.Add(argument);
            // 자격증명/에디터 프롬프트로 멈추지 않게 한다(GUI 뒤에서 돌므로 사용자가 응답할 수 없다).
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            var output = new StringBuilder();
            try
            {
                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                if (!process.Start()) return (-1, "git 프로세스를 시작하지 못했습니다.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return (process.ExitCode, output.ToString());
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (-1, "git 을 실행할 수 없습니다(설치·PATH 확인): " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return (-1, "git 실행 중 오류: " + ex.Message);
            }
        }

        private List<RunnerJob> BuildJobs(string target, string filesFrom, bool runCodeRule, bool runComment)
        {
            var jobs = new List<RunnerJob>();
            if (!runCodeRule && !runComment)
            {
                return jobs;
            }

            string logDir = ResolveTargetRoot(target);
            Directory.CreateDirectory(logDir);

            if (runCodeRule)
            {
                var rules = CollectRules(
                    (ASObjectVarSafe, "objectvar-safe"),
                    (ASObviousVar, "obviousvar"),
                    (ASArrayVarSafe, "arrayvar-safe"),
                    (ASParens, "parens"),
                    (ASForeachCast, "foreachcast"),
                    (ASObjectInitializer, "objectinitializer"),
                    (ASNullVar, "nullvar"),
                    (ASObjectVarNarrowing, "objectvar-narrowing"),
                    (ASLocalConst, "localconst"),
                    (ASArrayVarNarrowing, "arrayvar-narrowing"),
                    (ASForVar, "forvar"),
                    (ASFieldSplit, "fieldsplit"),
                    (ASEmptyStmt, "emptystmt"),
                    (ASForHoist, "forhoist"));
                if (rules.Count > 0)
                {
                    jobs.Add(new RunnerJob(
                        "코드 규칙 수정",
                        Path.Combine(_toolsDir, "_internal", "SparrowSyntaxFix", "Run-SparrowSyntaxFix.ps1"),
                        rules,
                        logDir));
                }
            }

            if (runComment)
            {
                var rules = CollectRules(
                    (BTrailing, "trailing"),
                    (BSpace, "space"),
                    (BPeriod, "period"),
                    (BCapitalize, "capitalize"),
                    (BFlatten, "flatten"),
                    (BMemberBlank, "memberblank"),
                    (BOneDeclaration, "onedeclaration"),
                    (BOneStatement, "onestatement"),
                    (BContinuation, "continuation"),
                    (BLinqAlign, "linqalign"),
                    (BBlockPromote, "blockpromote"));
                if (rules.Count > 0)
                {
                    jobs.Add(new RunnerJob(
                        "주석·레이아웃 수정",
                        Path.Combine(_toolsDir, "_internal", "SparrowCommentFix", "Run-SparrowCommentFix.ps1"),
                        rules,
                        logDir));
                }
            }

            foreach (RunnerJob job in jobs)
            {
                job.Arguments.Add("-Solution");
                job.Arguments.Add(target);
                job.Arguments.Add("-Rules");
                job.Arguments.Add(string.Join(",", job.Rules));
                job.Arguments.Add("-LogDir");
                job.Arguments.Add(job.LogDir);
                job.Arguments.Add("-FilesFrom");
                job.Arguments.Add(filesFrom);

                // 규칙별 커밋을 켜면 러너가 규칙마다 커밋을 만든다(롤백 단위 = 규칙). 끄면 파일만 고치고
                // 커밋은 사용자가 git 으로 한다. DryRun·생성 파일 포함은 CLI 러너 전용 옵션으로만 남아 있다.
                if (CommitCheck.IsChecked == true) job.Arguments.Add("-Commit");
                else job.Arguments.Add("-NoCommit");
            }

            return jobs;
        }

        private async Task RunJobAsync(RunnerJob job, CancellationToken cancellationToken)
        {
            if (!File.Exists(job.ScriptPath))
            {
                throw new FileNotFoundException("러너 스크립트를 찾을 수 없습니다.", job.ScriptPath);
            }

            AppendLog("");
            AppendLog(">>> " + job.Name);
            AppendLog("rules: " + string.Join(",", job.Rules));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(job.ScriptPath) ?? _skillRoot,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(job.ScriptPath);
            foreach (string arg in job.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _currentProcess = process;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => AppendLog(e.Data)));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => AppendLog(e.Data)));
            };

            if (!process.Start()) throw new InvalidOperationException("PowerShell 프로세스를 시작하지 못했습니다.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(job.Name + " 실패(exit=" + process.ExitCode + ")");
            }
        }

        private async Task<string> RunXlsSplitAsync(string inputXls, string sourceRoot, string filesFrom, CancellationToken cancellationToken)
        {
            // 익스포터 산출물(<체커 키>\{ID}_{파일명}_{라인}.md)을 사용자가 지정한 출력 폴더에 그대로 생성한다.
            // 선행 문서(체커 가이드/프롬프트/판정 계약)는 필요하지 않다 — 입력은 xls 하나뿐이다.
            string outputRoot = ResolveXlsOutputRoot(inputXls, XlsOutputPathBox.Text);
            var log = new DispatcherTextWriter(Dispatcher, AppendLog);

            DateTime startedUtc = DateTime.UtcNow;
            var elapsed = Stopwatch.StartNew();

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExportOptions exportOptions = BuildXlsSplitOptions(inputXls, outputRoot, sourceRoot, filesFrom);

                log.WriteLine("");
                log.WriteLine(">>> [XLS 분리] 체커별 md 분리");
                log.WriteLine("입력 XLS  : " + inputXls);
                log.WriteLine("출력 폴더 : " + outputRoot);
                ExportResult parse = SparrowExporter.Run(exportOptions, null);

                // 범위 필터 진단: 선택한 소스가 이 xls의 검출 경로와 하나도 안 맞으면(다른 체크아웃/잘못된 폴더)
                // 조용한 빈 결과 대신 운영자에게 원인을 로그로 알린다. Tier-2 모호 매칭은 소프트 경고로 남긴다.
                if (parse.ScopeDiagnostic != null)
                {
                    log.WriteLine("");
                    log.WriteLine(parse.ScopeDiagnostic);
                }
                if (parse.ScopeAmbiguousWarning != null)
                {
                    log.WriteLine(parse.ScopeAmbiguousWarning);
                }

                cancellationToken.ThrowIfCancellationRequested();
                log.WriteLine("생성 완료: 항목 md " + parse.WrittenCount + "건 · 체커 폴더 " + parse.UniqueCheckers + "개");

                // Export 직후 자동으로 캐시된 체커 규칙을 각 항목 md 에 self-contained 부착한다. 매핑이 없는 체커는
                // 순수 그대로 둔다(멱등). 새 체커만 매핑 패널에서 채우면 다음 실행부터 자동 부착된다.
                MapResult map = CheckerRuleMapper.Apply(parse.OutputDir, _guidesDir);
                log.WriteLine("규칙 매핑: 매핑 " + map.Mapped.Count + "개 · 미매핑 " + map.Unmapped.Count
                              + "개 · 규칙 부착 항목 " + map.ItemsTouched + "건");
                if (map.Unmapped.Count > 0)
                {
                    log.WriteLine("미매핑 체커(규칙 없음): " + string.Join(", ", map.Unmapped));
                }

                // 실행 리포트(기계 판독용)는 로그 폴더에만 쓴다 — 출력 폴더는 "체커 폴더 + 항목 md만" 계약을 유지한다.
                elapsed.Stop();
                WriteXlsSplitReport(exportOptions, parse, map, startedUtc, elapsed.ElapsedMilliseconds, log);

                return parse.OutputDir;
            }, cancellationToken);
        }

        // [XLS 분리] 실행 1회의 진단 리포트(json + 사람이 읽는 .log 요약)를 세션 로그 폴더에 남긴다. 어떤 실패도
        // 실행 결과를 바꾸지 않는다(경고만 로그). 출력 폴더에는 절대 쓰지 않는다.
        private void WriteXlsSplitReport(ExportOptions exportOptions, ExportResult parse, MapResult map,
                                       DateTime startedUtc, long elapsedMs, TextWriter log)
        {
            string? reportPath = _sessionLog.NewXlsSplitReportPath();
            if (reportPath == null)
            {
                log.WriteLine("실행 리포트: 로그 폴더를 쓸 수 없어 생략했습니다 (" + _sessionLog.LogDirectory + ")");
                return;
            }

            try
            {
                XlsSplitRunReport payload = XlsSplitReportWriter.Build(exportOptions, parse, map, _guidesDir, startedUtc, elapsedMs);
                if (XlsSplitReportWriter.TryWrite(reportPath, payload, out string? error))
                {
                    log.WriteLine("실행 리포트: " + reportPath);
                }
                else
                {
                    log.WriteLine("실행 리포트 기록 실패(무시): " + error);
                }
            }
            catch (Exception ex)
            {
                log.WriteLine("실행 리포트 생성 실패(무시): " + ex.Message);
            }
        }

        private ExportOptions BuildXlsSplitOptions(string inputXls, string outputDir, string sourceRoot, string filesFrom)
        {
            // 전건 수정 정책: 심각도/체커/Max 필터 없이 Sparrow 검출 전건을 대상으로 한다.
            // (범위 필터 filesFrom/RootPath 은 팀 분담용 파일/폴더 선택이며 검출 제외가 아님.)
            return new ExportOptions
            {
                InputPath = inputXls,
                OutDir = outputDir,
                RootPath = sourceRoot,
                FilesFrom = filesFrom
            };
        }

        // xls 를 고른 순간(실행 전) 검출 체커를 파싱해(SparrowExporter.ListCheckers — 어떤 파일도 쓰지 않음) 메인
        // 요약을 즉시 채운다. xls 가 비었거나 없으면 빈 상태. 세대 번호로 겹친 갱신 중 마지막만 반영한다.
        private async Task RefreshCheckerSummaryFromXlsAsync()
        {
            int gen = ++_mappingRefreshGen;
            string xls = XlsPathBox.Text.Trim().Trim('"');
            List<(string Key, int Count)> checkers;
            if (string.IsNullOrEmpty(xls) || !File.Exists(xls))
            {
                checkers = new List<(string Key, int Count)>();
            }
            else
            {
                checkers = await Task.Run(() =>
                    SparrowExporter.ListCheckers(xls).Select(c => (c.Key, c.Count)).ToList());
            }

            if (gen != _mappingRefreshGen) return;   // 더 나중에 시작된 갱신이 있음 → 오래된 결과 폐기
            _xlsCheckers = checkers;
            UpdateCheckerMappingSummary();
            _ruleManager?.UpdateCheckers(_xlsCheckers);   // 관리창이 열려 있으면 체커 매핑 목록도 갱신
        }

        // 실행 후: 출력 폴더의 검출 체커(CheckerRuleMapper.ListCheckers)로 요약을 갱신한다(지정 반영 후 상태).
        private async Task RefreshCheckerSummaryFromOutputAsync(string? outputDir)
        {
            int gen = ++_mappingRefreshGen;
            List<(string Key, int Count)> checkers;
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                checkers = new List<(string Key, int Count)>();
            }
            else
            {
                checkers = await Task.Run(() =>
                    CheckerRuleMapper.ListCheckers(outputDir!).Select(i => (i.Key, i.ItemCount)).ToList());
            }

            if (gen != _mappingRefreshGen) return;
            _xlsCheckers = checkers;
            UpdateCheckerMappingSummary();
            _ruleManager?.UpdateCheckers(_xlsCheckers);
        }

        // 메인 요약 "검출 체커 N종 · 매핑 M · 미매핑 K"(지정 기준: assignment 가 있고 그 규칙 파일이 실제 존재하는
        // 체커만 매핑으로 센다 — 실행 시 실제 부착되는 것과 일치). 파일명이 체커키와 같아도 지정 안 했으면 미매핑.
        private void UpdateCheckerMappingSummary()
        {
            int total = _xlsCheckers.Count;
            if (total == 0)
            {
                CheckerMappingSummary.Text = "XLS를 선택하면 검출 체커가 요약됩니다.";
                return;
            }

            Dictionary<string, string> assignments = CheckerRuleStore.LoadAssignments(_guidesDir);
            int mapped = _xlsCheckers.Count(c =>
                assignments.TryGetValue(c.Key, out string? rule)
                && !string.IsNullOrWhiteSpace(rule)
                && CheckerRuleStore.RuleExists(_guidesDir, rule!));
            int unmapped = total - mapped;
            CheckerMappingSummary.Text = "검출 체커 " + total + "종 · 매핑 " + mapped + " · 미매핑 " + unmapped;
        }

        private void OpenRuleManagerButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRuleManager();
        }

        // [체커 규칙 관리] 창(모덜리스)을 연다. 이미 열려 있으면 앞으로 가져오고 현재 검출 체커로 갱신한다. 규칙
        // 라이브러리 관리(A영역)는 xls 무관이므로 검출 체커가 비어 있어도 열 수 있다. 창이 닫히면 지정 변경이
        // 반영되도록 메인 요약을 다시 계산한다.
        private void OpenRuleManager()
        {
            if (_ruleManager != null)
            {
                try { _ruleManager.Activate(); } catch { /* 창이 닫히는 중일 수 있음 */ }
                _ruleManager.UpdateCheckers(_xlsCheckers);
                return;
            }

            var win = new RuleManagerWindow(_guidesDir, _xlsCheckers) { Owner = this };
            win.Closed += (_, _) =>
            {
                _ruleManager = null;
                UpdateCheckerMappingSummary();   // 지정 변경(_assignments.json) 반영
            };
            _ruleManager = win;
            win.Show();
            // 자동 지점 2: 관리창 오픈 직후(레이아웃/행 생성이 끝나는 ContextIdle 에 찍는다).
            _snapshots?.CaptureWhenIdle(win, "manager-open");
        }

        private static string ResolveXlsOutputRoot(string inputXls, string configuredOutput)
        {
            string trimmed = configuredOutput.Trim().Trim('"');
            if (!string.IsNullOrEmpty(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string inputFullPath = Path.GetFullPath(inputXls);
            string parent = Path.GetDirectoryName(inputFullPath) ?? Environment.CurrentDirectory;
            string name = Path.GetFileNameWithoutExtension(inputFullPath) + ".export";
            return Path.Combine(parent, name);
        }

        private static void TryDeleteFile(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary scope manifest cleanup is best-effort only.
            }
        }

        private void InitializeRuleInfo()
        {
            AddRuleInfo(ASObjectVarSafe, "객체 생성 명시 타입을 var로 변경",
                "선언 타입과 생성 타입이 같은 지역변수를 var로 바꿉니다.",
                "체커: PRACTICE.OBJECT_INSTANTIATION.NOT_USED_IMPLICIT_TYPING. 정적 타입 축소가 없는 기본 안전 규칙입니다.",
                "Foo item = new Foo();\r\n// ->\r\nvar item = new Foo();");
            AddRuleInfo(ASObviousVar, "명확한 지역변수 타입을 var로 변경",
                "리터럴, 캐스트, 명확한 생성 결과처럼 타입 추론이 분명한 지역변수를 var로 바꿉니다.",
                "체커: PRACTICE.OBVIOUS_VARIABLE_TYPE.NOT_USED_IMPLICIT_TYPING.",
                "string name = \"A\";\r\ndouble ratio = (double)20;\r\n// ->\r\nvar name = \"A\";\r\nvar ratio = (double)20;");
            AddRuleInfo(ASArrayVarSafe, "배열 초기화 문법 간소화",
                "동일 배열 타입의 장황한 초기화 구문만 줄입니다.",
                "체커: PRACTICE.ARRAY_DECLARATION.COMPLICATED_SYNTAX. 선언 타입은 유지합니다.",
                "int[] values = new int[] { 1, 2, 3 };\r\n// ->\r\nint[] values = { 1, 2, 3 };");
            AddRuleInfo(ASParens, "논리식 괄호 명확화",
                "&&와 || 논리식의 모든 피연산자에 괄호를 추가합니다.",
                "체커: MISSING_PARENTHESIS_IN_EXPRESSION. Sparrow 기준상 atom도 감쌉니다.",
                "if (isReady && hasValue || forced)\r\n// ->\r\nif (((isReady) && (hasValue)) || (forced))");
            AddRuleInfo(ASForeachCast, "[검토필요] foreach Cast<T> + var",
                "비제네릭 컬렉션 foreach의 명시 타입을 Cast<T>()와 var 조합으로 바꿉니다.",
                "체커: PRACTICE.LOOP_VARIABLE.NOT_USED_IMPLICIT_TYPING. 검토필요 커밋 대상입니다.",
                "foreach (XmlNode node in nodes)\r\n// ->\r\nforeach (var node in System.Linq.Enumerable.Cast<XmlNode>(nodes))");
            AddRuleInfo(ASObjectInitializer, "[검토필요] 연속 대입을 object initializer로 통합",
                "객체 생성 직후 연속된 단순 속성/필드 대입을 initializer로 합칩니다.",
                "체커: PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER. 연속 구간만 처리하며 검토필요 커밋 대상입니다.",
                "var item = new Foo();\r\nitem.A = 1;\r\nitem.B = text;\r\n// ->\r\nvar item = new Foo { A = 1, B = text };");
            AddRuleInfo(ASNullVar, "[검토필요] typed null var 초기화",
                "초기값이 없거나 null인 명시 지역변수를 typed null var 형태로 바꿉니다.",
                "체커: 명확한 지역변수 var 권장 계열. 검토필요 커밋 대상입니다.",
                "Foo item;\r\n// ->\r\nvar item = (Foo)null;");
            AddRuleInfo(ASObjectVarNarrowing, "[검토필요] 상위 타입 선언을 var로 축소",
                "인터페이스/상위 타입 선언을 실제 생성 타입 var로 바꿉니다.",
                "정적 타입 축소가 발생하므로 검토필요 커밋 대상입니다.",
                "IList<string> names = new List<string>();\r\n// ->\r\nvar names = new List<string>();");
            AddRuleInfo(ASLocalConst, "[검토필요] 지역 const를 var로 변경",
                "지역 const 선언을 일반 var 지역변수로 바꿉니다.",
                "지역 const 의미가 중요한 경우 검토가 필요합니다.",
                "const string Code = \"A\";\r\n// ->\r\nvar Code = \"A\";");
            AddRuleInfo(ASArrayVarNarrowing, "[검토필요] 배열 선언을 var + new[]로 축소",
                "선언 배열 타입을 var와 암시 배열 생성으로 줄입니다.",
                "object[] 등 정적 타입 축소 가능성이 있어 검토필요 커밋 대상입니다.",
                "int[] values = new int[] { 1, 2, 3 };\r\n// ->\r\nvar values = new[] { 1, 2, 3 };");
            AddRuleInfo(ASForVar, "[검토필요] for 루프 초기화 변수를 var로 변경",
                "for 초기화절의 명시 타입을 var로 바꿉니다.",
                "체커: 루프 변수 암시적 타입 사용 권장. 검토필요 커밋 대상입니다.",
                "for (int i = 0; i < count; i++)\r\n// ->\r\nfor (var i = 0; i < count; i++)");
            AddRuleInfo(ASFieldSplit, "[검토필요] 한 줄 다중 필드 선언 분리",
                "한 줄에 여러 필드를 선언한 구문을 필드별 선언으로 나눕니다.",
                "체커: 한 줄에 하나의 선언문 배치. 검토필요 커밋 대상입니다.",
                "private int x, y;\r\n// ->\r\nprivate int x;\r\nprivate int y;");
            AddRuleInfo(ASEmptyStmt, "[검토필요] 불필요한 빈 문장 제거",
                "불필요한 빈 문장 세미콜론을 제거합니다.",
                "체커: 한 줄에 하나의 구문/불필요 문장 계열. 검토필요 커밋 대상입니다.",
                "DoWork();;\r\n// ->\r\nDoWork();");
            AddRuleInfo(ASForHoist, "[검토필요] for 다중 선언자 분리",
                "for 초기화절의 다중 선언자를 루프 밖 선언으로 분리합니다.",
                "루프 스코프가 바뀌므로 검토필요 커밋 대상입니다.",
                "for (int i = 0, j = 0; i < n; i++)\r\n// ->\r\nvar j = 0;\r\nfor (var i = 0; i < n; i++)");

            AddRuleInfo(BTrailing, "코드 뒤 주석을 위 줄로 이동",
                "코드 뒤에 붙은 주석을 코드 위의 독립 주석 줄로 이동하고 문장 규칙을 맞춥니다.",
                "체커: 독립된 줄의 주석 작성 권장, 주석 앞 빈 줄 계열.",
                "DoWork(); //done\r\n// ->\r\n// Done.\r\nDoWork();");
            AddRuleInfo(BSpace, "주석 기호 뒤 공백 추가",
                "주석 기호 뒤 공백을 보강합니다.",
                "체커: FORMATTING.COMMENT.MISSING_SPACE_AFTER_DELIMITER.",
                "//done\r\n// ->\r\n// done");
            AddRuleInfo(BPeriod, "주석 끝 마침표 추가",
                "일반 문장 주석 끝에 마침표를 추가합니다.",
                "체커: FORMATTING.COMMENT.MISSING_PERIOD. Doxygen line-form은 보호 대상입니다.",
                "// Done\r\n// ->\r\n// Done.");
            AddRuleInfo(BCapitalize, "주석 첫 영문 대문자화",
                "주석 첫 ASCII 영문자를 대문자로 바꿉니다.",
                "체커: FORMATTING.COMMENT.LOWERCASE_FIRST_LETTER.",
                "// done.\r\n// ->\r\n// Done.");
            AddRuleInfo(BFlatten, "별표/Doxygen 블록 주석 평탄화",
                "별표 블록/Doxygen 주석을 의미 보존이 가능한 한 줄 주석 형태로 평탄화합니다.",
                "체커: FORMATTING.COMMENT.BLOCK_OF_ASTERISK.",
                "/** @brief delta marker */\r\n// ->\r\n// Delta marker.");
            AddRuleInfo(BMemberBlank, "멤버 선언 사이 빈 줄 추가",
                "메서드/프로퍼티/필드 등 멤버 선언 사이에 빈 줄을 추가합니다.",
                "체커: FORMATTING.BETWEEN_MEMBER_DEFINITION.MISSING_BLANK_LINE.",
                "public int A { get; set; }\r\npublic int B { get; set; }\r\n// ->\r\npublic int A { get; set; }\r\n\r\npublic int B { get; set; }");
            AddRuleInfo(BOneDeclaration, "한 줄 다중 선언 분리",
                "한 줄에 여러 지역변수를 선언한 구문을 줄별 선언으로 나눕니다.",
                "체커: 한 줄에 하나의 선언문 배치.",
                "int x = 1, y = 2;\r\n// ->\r\nint x = 1;\r\nint y = 2;");
            AddRuleInfo(BOneStatement, "한 줄 다중 구문 분리",
                "한 줄에 여러 문장이 붙은 구문을 문장별 줄로 나눕니다.",
                "체커: 한 줄에 하나의 구문 배치.",
                "Start(); Stop();\r\n// ->\r\nStart();\r\nStop();");
            AddRuleInfo(BContinuation, "여러 줄 문장 들여쓰기 보정",
                "여러 줄 문장의 continuation line 들여쓰기를 보정합니다.",
                "체커: FORMATTING.CONTINUATION_LINE.BAD_INDENTATION. 변경량이 클 수 있어 DryRun 확인을 권장합니다.",
                "var value = Foo(\r\nx,\r\ny);\r\n// ->\r\nvar value = Foo(\r\n    x,\r\n    y);");
            AddRuleInfo(BLinqAlign, "LINQ 쿼리 절 정렬",
                "LINQ query expression의 from/where/select 절 정렬을 맞춥니다.",
                "체커: FORMATTING.LINQ.QUERY_CLAUSE_ALIGNMENT.",
                "var q = from x in xs\r\nwhere x.Enabled\r\nselect x;\r\n// ->\r\nvar q = from x in xs\r\n        where x.Enabled\r\n        select x;");
            AddRuleInfo(BBlockPromote, "[검토필요] inline block 주석 이동",
                "코드 뒤 inline block comment를 코드 위 독립 주석으로 승격합니다.",
                "체커: 독립된 줄의 주석 작성 권장/별표 블록 제한. 검토필요 커밋 대상입니다.",
                "DoWork(); /* done */\r\n// ->\r\n// Done.\r\nDoWork();");
        }

        private void AddRuleInfo(CheckBox checkBox, string title, string summary, string checker, string example)
        {
            var (before, after) = SplitExample(example);
            var info = new RuleInfo(title, summary, checker, before, after);
            _ruleInfos[checkBox.Name] = info;
            checkBox.ToolTip = title + Environment.NewLine + checker;
            checkBox.MouseEnter += RuleControl_MouseEnter;
            checkBox.GotKeyboardFocus += RuleControl_FocusOrClick;
            checkBox.Click += RuleControl_FocusOrClick;
            checkBox.Checked += RuleControl_CheckedChanged;
            checkBox.Unchecked += RuleControl_CheckedChanged;
        }

        private void RuleControl_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is CheckBox checkBox) ShowRuleInfo(checkBox.Name);
        }

        private void RuleControl_FocusOrClick(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox) ShowRuleInfo(checkBox.Name);
            UpdateSummary();
        }

        private void RuleControl_CheckedChanged(object sender, RoutedEventArgs e)
        {
            UpdateSummary();
        }

        private void ShowRuleInfo(string key)
        {
            if (!_ruleInfos.TryGetValue(key, out RuleInfo? info))
            {
                RuleInfoTitle.Text = "규칙 설명";
                RuleInfoBody.Text = "규칙을 선택하면 대응 체커와 변경 예시가 표시됩니다.";
                RuleBeforeBox.Text = "";
                RuleAfterBox.Text = "";
                return;
            }

            RuleInfoTitle.Text = info.Title;
            RuleInfoBody.Text = info.Summary + Environment.NewLine + info.Checker;
            RuleBeforeBox.Text = info.Before;
            RuleAfterBox.Text = info.After;
        }

        private static (string Before, string After) SplitExample(string example)
        {
            string normalized = example.Replace("\r\n", "\n");
            const string marker = "\n// ->\n";
            int index = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return (example.Trim(), "");
            }

            string before = normalized.Substring(0, index).Trim();
            string after = normalized.Substring(index + marker.Length).Trim();
            return (before, after);
        }

        private void UpdateSummary()
        {
            if (!IsLoaded) return;

            ActiveMode mode = CurrentMode();
            UpdateXlsScopeSummary();
            SectionHintText.Text = mode == ActiveMode.XlsSplit
                ? "XLS 분리: 입력은 XLS 하나입니다. 프로젝트 경로가 필요 없고 소스를 수정하지 않습니다."
                // 커밋 여부는 [규칙별 커밋 생성] 상태에 따라 달라지므로 여기서 단정하지 않는다.
                // 그건 모드에 따라 갈리는 요약바(ModeNotice)와 실행 로그(ModeRunLogLine)가 말한다.
                : "코드 자동수정: C# 전용입니다. 선택한 탭의 규칙만 실행하며, 소스 파일을 실제로 수정합니다.";
            int selectedFiles = _currentScope?.SelectedFiles.Count ?? 0;
            int totalFiles = _currentScope?.TotalFiles ?? 0;
            int excludedFiles = _currentScope?.ExcludedFiles ?? 0;

            if (_currentScope != null)
            {
                ScopeStatusText.Text = $"{selectedFiles}개 선택 / {totalFiles}개 발견"
                    + (excludedFiles > 0 ? $" / {excludedFiles}개 제외" : "");
            }

            string target = TargetPathBox.Text.Trim();
            SummaryTargetText.Text = string.IsNullOrEmpty(target)
                ? "대상 경로가 필요합니다."
                : target;

            switch (mode)
            {
                case ActiveMode.CodeRule:
                {
                    int count = CountChecked(ASObjectVarSafe, ASObviousVar, ASArrayVarSafe, ASParens, ASForeachCast,
                        ASObjectInitializer, ASNullVar, ASObjectVarNarrowing, ASLocalConst, ASArrayVarNarrowing,
                        ASForVar, ASFieldSplit, ASEmptyStmt, ASForHoist);
                    int review = CountChecked(ReviewNeededCodeRules);
                    SummaryRulesText.Text = $"코드 규칙 · 선택 {count}개";
                    SummaryModeText.Text = $"{ModeNotice} · 검토필요 {review} · 선택 파일 {selectedFiles}";
                    break;
                }
                case ActiveMode.Comment:
                {
                    int count = CountChecked(BTrailing, BSpace, BPeriod, BCapitalize, BFlatten, BMemberBlank,
                        BOneDeclaration, BOneStatement, BContinuation, BLinqAlign, BBlockPromote);
                    int review = CountChecked(ReviewNeededCommentRules);
                    SummaryRulesText.Text = $"주석·레이아웃 · 선택 {count}개";
                    SummaryModeText.Text = $"{ModeNotice} · 검토필요 {review} · 선택 파일 {selectedFiles}";
                    break;
                }
                case ActiveMode.XlsSplit:
                {
                    // 자동수정 요약 패널은 XLS 화면에 보이지 않는다(범위·체커 요약은 그 화면 자체에 있다). 화면을
                    // 되돌렸을 때 남은 문구가 오해를 주지 않도록 값만 정리해 둔다.
                    SummaryRulesText.Text = "XLS 분리 실행 중이거나 대기 중";
                    SummaryModeText.Text = "XLS → 체커별 폴더 md 분리(<체커 키>\\{ID}_{파일명}_{라인}.md)";
                    break;
                }
                default:
                    // 방어용 폴백(하위 탭이 둘뿐이라 정상 상태에서는 도달하지 않는다).
                    SummaryRulesText.Text = "실행할 탭([코드 규칙] / [주석·레이아웃])을 선택하세요";
                    SummaryModeText.Text = $"{ModeNotice} · 선택 파일 {selectedFiles}";
                    break;
            }
        }
        private static int CountChecked(params CheckBox[] boxes)
        {
            return boxes.Count(b => b.IsChecked == true);
        }

        private static List<string> CollectRules(params (CheckBox CheckBox, string Rule)[] pairs)
        {
            return pairs
                .Where(p => p.CheckBox.IsChecked == true)
                .Select(p => p.Rule)
                .ToList();
        }

        private static string ResolveTargetRoot(string target)
        {
            if (Directory.Exists(target)) return target;
            string? dir = Path.GetDirectoryName(target);
            return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
        }

        private static string ResolveSkillRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; dir != null && i < 12; i++, dir = dir.Parent)
            {
                string skill = Path.Combine(dir.FullName, "SKILL.md");
                string runner = Path.Combine(dir.FullName, "tools", "Run-SparrowRunnerGui.cmd");
                string syntax = Path.Combine(dir.FullName, "tools", "_internal", "SparrowSyntaxFix", "Run-SparrowSyntaxFix.ps1");
                if (File.Exists(skill) && File.Exists(runner) && File.Exists(syntax)) return dir.FullName;
            }

            string fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            if (File.Exists(Path.Combine(fallback, "SKILL.md")) &&
                File.Exists(Path.Combine(fallback, "tools", "Run-SparrowRunnerGui.cmd")))
            {
                return fallback;
            }

            throw new DirectoryNotFoundException(@"sparrow-toolkit 루트를 찾을 수 없습니다(레포 루트의 SKILL.md + tools\Run-SparrowRunnerGui.cmd 로 판별합니다).");
        }

        private void SetRunning(bool running)
        {
            // 실행 중에는 무조건 비활성. 종료 후에는 활성 기능(옵션 탭이면 비활성)에 맞춰 상태를 복원한다.
            if (running) RunButton.IsEnabled = false;
            else UpdateRunButtonForMode();
            StopButton.IsEnabled = running;
            BrowseFolderButton.IsEnabled = !running;
            RefreshScopeButton.IsEnabled = !running;
            SelectAllScopeButton.IsEnabled = !running;
            ClearScopeButton.IsEnabled = !running;
            ScopeTree.IsEnabled = !running;
            BrowseXlsButton.IsEnabled = !running;
            BrowseXlsOutputButton.IsEnabled = !running;
            SelectAllXlsScopeButton.IsEnabled = !running;
            ClearXlsScopeButton.IsEnabled = !running;
            XlsScopeTree.IsEnabled = !running;
            // 실행 중 대분류 전환 금지(실행 기능이 화면 선택으로 정해지므로 도중에 바뀌면 안 된다).
            SectionTabs.IsEnabled = !running;
            RulesTabs.IsEnabled = !running;
            TargetPathBox.IsEnabled = !running;
            XlsPathBox.IsEnabled = !running;
            XlsOutputPathBox.IsEnabled = !running;
            StatusText.Text = running ? "실행 중..." : "대기 중";
        }

        // 미처리 예외를 세션 로그에만 남긴다. 예외를 처리(Handled)하지 않으므로 앱 동작은 지금과 동일하고,
        // 증거만 추가된다. UI 스레드가 아닐 수 있어 화면 로그(AppendLog)는 건드리지 않는다.
        private void HookCrashLogging()
        {
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.DispatcherUnhandledException += (_, args) =>
                        _sessionLog.Append("!!! 미처리 예외(Dispatcher): " + args.Exception);
                }
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                    _sessionLog.Append("!!! 미처리 예외(AppDomain): " + (args.ExceptionObject?.ToString() ?? "(null)"));
                TaskScheduler.UnobservedTaskException += (_, args) =>
                    _sessionLog.Append("!!! 관측되지 않은 Task 예외: " + args.Exception);
            }
            catch
            {
                // 진단 훅 등록 실패가 앱 기동을 막지 않는다.
            }
        }

        // 정상 종료 표식. 이 줄 없이 끝난 세션 로그 = 비정상 종료(크래시/강제 종료) 신호다.
        protected override void OnClosed(EventArgs e)
        {
            // 스냅샷 요청 감시자를 먼저 정리한다(닫히는 창을 캡처하려는 요청이 뒤늦게 들어오지 않도록).
            try { _snapshots?.Dispose(); } catch { /* 정리 실패는 종료를 막지 않는다 */ }
            _sessionLog.Append("세션 종료 (정상)");
            base.OnClosed(e);
        }

        // 화면 + 세션 파일 동시 기록. 파일에는 HH:mm:ss.fff 타임스탬프를 붙인다(화면은 기존처럼 원문 그대로 —
        // 사람이 읽는 창은 간결하게, AI/사후분석이 읽는 파일은 시간까지). 파일 기록 실패는 무시한다.
        private void AppendLog(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            _sessionLog.Append(line);
        }

        private sealed class RuleInfo
        {
            public RuleInfo(string title, string summary, string checker, string before, string after)
            {
                Title = title;
                Summary = summary;
                Checker = checker;
                Before = before;
                After = after;
            }

            public string Title { get; }
            public string Summary { get; }
            public string Checker { get; }
            public string Before { get; }
            public string After { get; }
        }

        private sealed class DispatcherTextWriter : TextWriter
        {
            private readonly Dispatcher _dispatcher;
            private readonly Action<string> _append;

            public DispatcherTextWriter(Dispatcher dispatcher, Action<string> append)
            {
                _dispatcher = dispatcher;
                _append = append;
            }

            public override Encoding Encoding => new UTF8Encoding(false);

            public override void WriteLine(string? value)
            {
                string line = value ?? "";
                _dispatcher.BeginInvoke(new Action(() => _append(line)));
            }
        }

        private sealed class RunnerJob
        {
            public RunnerJob(string name, string scriptPath, IReadOnlyList<string> rules, string logDir)
            {
                Name = name;
                ScriptPath = scriptPath;
                Rules = rules;
                LogDir = logDir;
            }

            public string Name { get; }
            public string ScriptPath { get; }
            public IReadOnlyList<string> Rules { get; }
            public string LogDir { get; }
            public List<string> Arguments { get; } = new List<string>();
        }

        // 기동 인자 파서: GUI를 테스트에서 알려진 상태로 자동 구동하기 위한 최소 옵션.
        //   --xls <path>          : [XLS 분리] 탭 선택 + XLS 경로 프리필
        //   --xls-out <dir>       : [XLS 분리] 출력 폴더 프리필
        //   --guides-dir <dir>    : 체커 규칙 라이브러리/지정 폴더 override (실 references\checkers 오염 방지)
        //   --log-dir <dir>       : 세션 로그/[XLS 분리] 리포트 폴더 override (기본 %LOCALAPPDATA%\SparrowRunner\logs)
        //   --screenshot-dir <dir>: 창 스냅샷 PNG 폴더. 주면 자동 지점(메인창 로드/관리창 오픈/[XLS 분리] 완료)에서
        //                           스스로 캡처하고, 그 폴더에 capture.request 파일이 생기면 활성 창을 즉시 캡처한다.
        //                           주지 않으면 스냅샷 기능 전체가 비활성이다(기존 동작 불변).
        //   --xls-autorun         : 로드 완료 후 [XLS 분리] 실행 자동 트리거
        //   --open-rule-manager   : 로드 완료 후 [체커 규칙 관리] 창 자동 오픈(검출 체커 로드된 상태)
        private sealed class StartupOptions
        {
            public string? Xls { get; private set; }
            public string? XlsOut { get; private set; }
            public string? GuidesDir { get; private set; }
            public string? LogDir { get; private set; }
            public string? ScreenshotDir { get; private set; }
            public bool XlsAutorun { get; private set; }
            public bool OpenRuleManager { get; private set; }

            public static StartupOptions Parse(string[] args)
            {
                var o = new StartupOptions();
                if (args == null) return o;

                // args[0] 은 실행 파일 경로이므로 값 소비는 인덱스 안전하게 처리한다.
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    switch (a)
                    {
                        case "--xls":
                            o.Xls = NextValue(args, ref i);
                            break;
                        case "--xls-out":
                            o.XlsOut = NextValue(args, ref i);
                            break;
                        case "--guides-dir":
                            o.GuidesDir = NextValue(args, ref i);
                            break;
                        case "--log-dir":
                            o.LogDir = NextValue(args, ref i);
                            break;
                        case "--screenshot-dir":
                            o.ScreenshotDir = NextValue(args, ref i);
                            break;
                        case "--xls-autorun":
                            o.XlsAutorun = true;
                            break;
                        case "--open-rule-manager":
                            o.OpenRuleManager = true;
                            break;
                    }
                }
                return o;
            }

            // 다음 토큰을 값으로 소비한다(없거나 다음이 또 다른 --옵션이면 null). 인덱스는 소비한 만큼 전진.
            private static string? NextValue(string[] args, ref int i)
            {
                if (i + 1 >= args.Length) return null;
                string next = args[i + 1] ?? "";
                if (next.StartsWith("--", StringComparison.Ordinal)) return null;
                i++;
                return next;
            }
        }
    }
}

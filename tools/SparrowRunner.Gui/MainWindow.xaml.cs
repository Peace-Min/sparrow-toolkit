using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SparrowXlsExport.Core;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// WPF wrapper for Track A/B PowerShell runners. Rewrite logic stays in the existing CLI scripts.
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
        // Track C 실행 리포트도 같은 로그 폴더에 남는다(출력 폴더 순수성 유지). 기록 실패는 앱을 죽이지 않는다.
        private readonly SessionLog _sessionLog;

        // 창 스냅샷(--screenshot-dir). 이 앱은 미설치 커스텀 exe라 외부에서 스크린샷을 찍을 수 없어, UI 증거가
        // UIA Rect 수치뿐이었다. 그래서 앱이 스스로 자기 창을 PNG로 렌더해 남긴다(AI/신고자가 실제 UI를 눈으로 봄).
        // 인자를 주지 않으면 null = 기능 전체 비활성(기존 동작 완전 불변).
        private readonly SnapshotRecorder? _snapshots;

        // 테스트 기동 인자(GUI를 알려진 상태로 자동 구동): Track C 탭 프리필 + 선택적 자동실행 + 관리창 자동 오픈.
        private readonly string? _startupTrackCXls;
        private readonly string? _startupTrackCOut;
        private readonly bool _startupTrackCAutorun;
        private readonly bool _startupOpenRuleManager;

        // 활성 대분류(+ A/B 화면의 하위 탭)가 곧 실행 트랙이다. 트랙은 내부 개념이고 화면에는 노출하지 않는다.
        //   [코드 자동수정] 대분류 → 선택된 하위 탭([코드 규칙]=Track A / [주석·레이아웃]=Track B)
        //   [XLS 분리]     대분류 → 항상 Track C
        //   None = 방어용 폴백(로드 전 등 어느 하위 탭도 선택되지 않은 순간).
        private enum ActiveTrack { A, B, C, None }

        // GUI 실행은 언제나 "파일만 수정, 커밋 없음"이다 — 러너에 -NoCommit 을 고정으로 넘긴다. 검토·커밋은
        // 사용자가 git 으로 한다(git diff 가 DryRun 보다 상위 호환이고, 자동 커밋이 없으니 컴파일 게이트도 불필요).
        // 자동 커밋 / DryRun / 생성 파일 포함은 CLI 러너 옵션(-Commit / -DryRun / -IncludeGenerated)으로 남아 있다.
        private const string NoCommitNotice = "파일만 수정 · 커밋하지 않음 (git diff 로 검토 후 커밋)";
        private const string NoCommitDoneSuffix = "개 파일 수정됨 — 커밋하지 않았습니다. git diff 로 검토 후 커밋하세요.";
        private const string CommitNotice = "규칙별 커밋 생성 (규칙 하나 = 커밋 하나, 규칙 단위 롤백 가능)";
        private const string CommitDoneSuffix = "개 파일 수정됨 — 규칙별로 커밋했습니다. git log 로 확인하세요.";

        /// <summary>실행 모드 안내(요약바·툴팁). 규칙별 커밋 체크 상태에 따라 갈린다.</summary>
        private string ModeNotice => CommitCheck?.IsChecked == true ? CommitNotice : NoCommitNotice;

        /// <summary>실행 종료 안내 접미사. 커밋 여부에 따라 다음 행동(git diff / git log)이 달라진다.</summary>
        private string ModeDoneSuffix => CommitCheck?.IsChecked == true ? CommitDoneSuffix : NoCommitDoneSuffix;

        private void CommitCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateRunButtonForTrack();
            UpdateSummary();
        }

        // 생성 파일(.g.cs/.designer.cs/obj·bin 등)은 GUI 에서 언제나 제외한다 — 빌드가 다시 만들어 내므로 고칠
        // 이유가 없다. 굳이 포함해야 하는 자동화는 CLI 러너의 -IncludeGenerated 를 쓴다.
        private const bool IncludeGeneratedFiles = false;

        private readonly Dictionary<string, RuleInfo> _ruleInfos = new Dictionary<string, RuleInfo>(StringComparer.Ordinal);
        private const string CFamilyCompoundStatementsKey = "CFamily.Code.CompoundStatements";
        private const string CFamilyFinalElseKey = "CFamily.Code.FinalElse";
        private const string CFamilyMissingElseKey = "CFamily.Code.MissingElse";
        private const string CFamilySwitchDefaultKey = "CFamily.Code.SwitchDefault";
        private const string CFamilyLogicalParenthesesKey = "CFamily.Code.LogicalParentheses";
        private const string CFamilyUnsignedSuffixKey = "CFamily.Code.UnsignedSuffix";
        private const string CFamilyIgnoredReturnKey = "CFamily.Code.IgnoredReturn";
        private const string CFamilySizeOfPointeeKey = "CFamily.Code.SizeOfPointee";
        private const string CFamilyFixedWidthTypesKey = "CFamily.Code.FixedWidthTypes";
        private const string CFamilyTrailingCommentKey = "CFamily.Comment.TrailingComment";
        private const string CFamilyCommentSpaceKey = "CFamily.Comment.Space";
        private const string CFamilyCommentPeriodKey = "CFamily.Comment.Period";
        private const string CFamilyCommentCapitalizeKey = "CFamily.Comment.Capitalize";
        private bool _cFamilyCompoundStatements = true;
        private bool _cFamilyFinalElse;
        private bool _cFamilyMissingElse;
        private bool _cFamilySwitchDefault;
        private bool _cFamilyLogicalParentheses = true;
        private bool _cFamilyUnsignedSuffix;
        private bool _cFamilyIgnoredReturn;
        private bool _cFamilySizeOfPointee;
        private bool _cFamilyFixedWidthTypes;
        private bool _cFamilyTrailingComment = true;
        private bool _cFamilyCommentSpace = true;
        private bool _cFamilyCommentPeriod = true;
        private bool _cFamilyCommentCapitalize = true;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _scopeCts;
        private CancellationTokenSource? _xlsScopeCts;
        private Process? _currentProcess;
        private string? _lastTrackCOutputDir;
        private SourceScope? _currentScope;
        private SourceScope? _currentCSharpScope;
        private CancellationTokenSource? _csharpScopeCts;

        // XLS 분리 대분류의 범위 트리(로컬 소스 스캔이 아니라 xls 검출 경로로 만든다).
        private XlsScope? _currentXlsScope;

        // 매핑 패널 갱신 세대. 로드시 목록화(xls census)와 실행후 갱신(출력 트리)이 겹칠 수 있어, 가장 나중에
        // 시작한 갱신만 적용되도록 세대 번호로 오래된 백그라운드 결과를 버린다(마지막 시작이 이긴다).
        private int _mappingRefreshGen;

        public ObservableCollection<SourceScopeNode> ScopeRoots { get; } = new ObservableCollection<SourceScopeNode>();
        public ObservableCollection<SourceScopeNode> CSharpScopeRoots { get; } = new ObservableCollection<SourceScopeNode>();

        /// <summary>XLS 분리 대분류의 범위 트리 루트(리프의 FullPath = xls 원본 경로 문자열).</summary>
        public ObservableCollection<SourceScopeNode> XlsScopeRoots { get; } = new ObservableCollection<SourceScopeNode>();

        // Track C 검출 체커(키·건수). xls 로드 시(census) 또는 실행 후(출력 트리)에 채워진다. [체커 규칙 관리]
        // 창으로 전달하고, 메인 요약 "검출 체커 N종 · 매핑 M · 미매핑 K"(지정 기준) 계산에 쓴다.
        private List<(string Key, int Count)> _trackCCheckers = new List<(string Key, int Count)>();

        // 열려 있는 규칙 관리 창(모덜리스). 중복 오픈 방지 + 닫힐 때 메인 요약을 지정 기준으로 다시 계산한다.
        private RuleManagerWindow? _ruleManager;
        private LogWindow? _logWindow;
        private string _activeTaskName = "준비됨";
        private bool _darkTheme;
        private int _sourcePreviewGeneration;
        private readonly List<string> _pinnedSourceFiles = new List<string>();
        private readonly List<SplitSourceView> _splitSourceViews = new List<SplitSourceView>();
        private TabItem? _previewSourceTab;
        private string? _currentPreviewPath;

        private sealed class SplitSourceView
        {
            public SplitSourceView(string path, TextBox viewer)
            {
                Path = path;
                Viewer = viewer;
            }

            public string Path { get; }
            public TextBox Viewer { get; }
        }

        public MainWindow()
        {
            InitializeComponent();

            // XLS를 가장 왼쪽에 배치하고 첫 화면으로 선택한다.
            SectionTabs.Items.Remove(SectionXlsTab);
            SectionTabs.Items.Insert(0, SectionXlsTab);
            SectionTabs.SelectedItem = SectionXlsTab;

            StartupOptions startup = StartupOptions.Parse(Environment.GetCommandLineArgs());
            _skillRoot = ResolveSkillRoot();
            _toolsDir = Path.Combine(_skillRoot, "tools");
            _guidesDir = !string.IsNullOrWhiteSpace(startup.GuidesDir)
                ? Path.GetFullPath(startup.GuidesDir!.Trim().Trim('"'))
                : Path.Combine(_skillRoot, "references", "checkers");
            _startupTrackCXls = startup.TrackCXls;
            _startupTrackCOut = startup.TrackCOut;
            _startupTrackCAutorun = startup.TrackCAutorun;
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
                UpdateRunButtonForTrack();
                UpdateSummary();
                ApplyStartupTrackCPrefill();
                // 관리창을 열기 전에 검출 체커를 확실히 로드한다(census). --open-rule-manager 경로가 빈 목록으로
                // 열리지 않도록 프리필된 xls 를 여기서 await 한다. 범위 트리도 같은 xls 로 함께 만든다.
                if (!string.IsNullOrWhiteSpace(TrackCXlsPathBox.Text))
                {
                    await RefreshTrackCSummaryFromXlsAsync();
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
                if (_startupTrackCAutorun) await AutoRunTrackCAsync();
            };
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;

            if (e.Key == Key.F1 && modifiers == ModifierKeys.None)
            {
                OpenHelpWindow();
                e.Handled = true;
            }
            else if (e.Key == Key.F && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                OpenSourceFile();
                e.Handled = true;
            }
            else if (e.Key == Key.O && modifiers == ModifierKeys.Control)
            {
                OpenSourceFolder();
                e.Handled = true;
            }
            else if (e.Key == Key.O && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ChooseXlsOutputFolder();
                e.Handled = true;
            }
            else if (e.Key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                _ = RegisterSourceFilesAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && modifiers == ModifierKeys.None)
            {
                RunXlsFromMenu();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && modifiers == ModifierKeys.Shift)
            {
                StopButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F6 && modifiers == ModifierKeys.None)
            {
                RunCommentLayoutFromMenu();
                e.Handled = true;
            }
            else if (e.Key == Key.F7 && modifiers == ModifierKeys.None)
            {
                RunCodeRulesFromMenu();
                e.Handled = true;
            }
            else if (e.Key == Key.T && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                OpenTargetFolderFromMenu();
                e.Handled = true;
            }
            else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (_cts == null) CommitCheck.IsChecked = CommitCheck.IsChecked != true;
                e.Handled = true;
            }
            else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                OpenRuleSettings(commentLayout: false);
                e.Handled = true;
            }
            else if (e.Key == Key.L && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                OpenRuleSettings(commentLayout: true);
                e.Handled = true;
            }
            else if (e.Key == Key.O && modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                OpenTrackCOutputButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.R && modifiers == ModifierKeys.Control)
            {
                OpenRuleManager();
                e.Handled = true;
            }
        }

        private void OpenFileMenuItem_Click(object sender, RoutedEventArgs e) => OpenSourceFile();

        private void OpenSourceFile()
        {
            if (_cts != null || IsCSharpSection()) return;
            SectionTabs.SelectedItem = SectionFixTab;
            BrowseFileButton_Click(this, new RoutedEventArgs());
        }

        private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e) => OpenSourceFolder();

        private void OpenSourceFolder()
        {
            if (_cts != null || IsCSharpSection()) return;
            SectionTabs.SelectedItem = SectionFixTab;
            BrowseFolderButton_Click(this, new RoutedEventArgs());
        }

        private void ChooseOutputFolderMenuItem_Click(object sender, RoutedEventArgs e) => ChooseXlsOutputFolder();

        private void ChooseXlsOutputFolder()
        {
            if (_cts != null || IsCSharpSection()) return;
            SectionTabs.SelectedItem = SectionXlsTab;
            BrowseTrackCOutputButton_Click(this, new RoutedEventArgs());
        }

        private void RegisterSourceMenuItem_Click(object sender, RoutedEventArgs e) => _ = RegisterSourceFilesAsync();

        private async Task RegisterSourceFilesAsync()
        {
            if (_cts != null || IsCSharpSection()) return;
            SectionTabs.SelectedItem = SectionFixTab;

            string target = TargetPathBox.Text.Trim().Trim('"');
            if (!Directory.Exists(target) && !File.Exists(target))
            {
                var folderDialog = new OpenFolderDialog { Title = "소스 파일이 있는 프로젝트 폴더 선택" };
                if (folderDialog.ShowDialog(this) != true) return;
                TargetPathBox.Text = folderDialog.FolderName;
            }

            await RefreshScopeAsync(showErrors: true);
            SourceScope? scope = _currentScope;
            if (scope == null || scope.TotalFiles == 0)
            {
                MessageBox.Show(this, "선택한 폴더에서 등록할 소스 파일을 찾지 못했습니다.\n지원 형식: .c, .cpp, .cs, .h, .hpp",
                    "소스 파일 등록", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = new HashSet<string>(scope.SelectedFiles, StringComparer.OrdinalIgnoreCase);
            var dialog = new SourceFileSelectionWindow(scope.RootPath, scope.RootNode.EnumerateFiles(), selected)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            scope.RootNode.ApplySelection(new HashSet<string>(dialog.SelectedFiles, StringComparer.OrdinalIgnoreCase));
            UpdateSummary();
        }

        private void ProjectExplorerToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ProjectExplorerTreeHost == null || ProjectExplorerToggle == null) return;
            bool show = ProjectExplorerToggle.IsChecked == true;
            ProjectExplorerTreeHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ProjectExplorerToggle.ToolTip = show ? "프로젝트 트리 숨기기" : "프로젝트 트리 보이기";
        }

        private void ScopeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(e.NewValue is SourceScopeNode node) || !node.IsFile) return;

            TabItem? pinned = PinnedSourceTabs.Items.OfType<TabItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, node.FullPath, StringComparison.OrdinalIgnoreCase));
            if (pinned != null)
            {
                PinnedSourceTabs.SelectedItem = pinned;
                return;
            }

            ShowPreviewSourceTab(node.FullPath);
        }

        private void ScopeTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(ScopeTree.SelectedItem is SourceScopeNode node) || !node.IsFile) return;
            PinSourceFile(node.FullPath);
            e.Handled = true;
        }

        private void PinSourceFile(string path)
        {
            TabItem? existing = PinnedSourceTabs.Items.OfType<TabItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (ReferenceEquals(existing, _previewSourceTab))
                {
                    _previewSourceTab = null;
                    ConfigurePinnedSourceTab(existing, path);
                    if (!_pinnedSourceFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                        _pinnedSourceFiles.Add(path);
                }
                PinnedSourceTabs.SelectedItem = existing;
                return;
            }

            var tab = new TabItem { Tag = path, ToolTip = path };
            ConfigurePinnedSourceTab(tab, path);
            _pinnedSourceFiles.Add(path);
            PinnedSourceTabs.Items.Add(tab);
            PinnedSourceTabs.SelectedItem = tab;
        }

        private void ShowPreviewSourceTab(string path)
        {
            if (_previewSourceTab == null)
            {
                _previewSourceTab = new TabItem { FontStyle = FontStyles.Italic };
                PinnedSourceTabs.Items.Add(_previewSourceTab);
            }

            bool wasSelected = ReferenceEquals(PinnedSourceTabs.SelectedItem, _previewSourceTab);
            _previewSourceTab.Tag = path;
            _previewSourceTab.ToolTip = path + Environment.NewLine + "미리보기 — 두 번 클릭하면 고정됩니다.";
            _previewSourceTab.Header = new TextBlock
            {
                Text = Path.GetFileName(path),
                MaxWidth = 145,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (wasSelected) PinnedSourceTabs.SelectedItem = null;
            PinnedSourceTabs.SelectedItem = _previewSourceTab;
        }

        private void ConfigurePinnedSourceTab(TabItem tab, string path)
        {
            tab.Tag = path;
            tab.ToolTip = path;
            tab.FontStyle = FontStyles.Normal;

            var closeButton = new Button
            {
                Content = "×",
                Width = 22,
                MinWidth = 22,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 16,
                ToolTip = "탭 닫기"
            };
            closeButton.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");

            var fileName = new TextBlock
            {
                Text = Path.GetFileName(path),
                MaxWidth = 145,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(fileName);
            header.Children.Add(closeButton);
            tab.Header = header;
            closeButton.Tag = tab;
            closeButton.Click += ClosePinnedSourceTab_Click;
        }

        private void ClosePinnedSourceTab_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is TabItem tab)) return;
            string? path = tab.Tag as string;
            bool wasSelected = ReferenceEquals(PinnedSourceTabs.SelectedItem, tab);
            PinnedSourceTabs.Items.Remove(tab);
            if (path != null) _pinnedSourceFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (wasSelected && PinnedSourceTabs.Items.Count > 0)
                PinnedSourceTabs.SelectedIndex = 0;
            else if (PinnedSourceTabs.Items.Count == 0)
            {
                ClearSplitSourcePanes();
                ResetSourcePreview();
            }
            e.Handled = true;
        }

        private async void PinnedSourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, PinnedSourceTabs)) return;
            if (!(PinnedSourceTabs.SelectedItem is TabItem tab) || !(tab.Tag is string path)) return;
            await LoadPrimarySourcePathAsync(path);
        }

        private async void SplitSourceViewButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = (PinnedSourceTabs.SelectedItem as TabItem)?.Tag as string ?? _currentPreviewPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "분할할 소스 파일 탭을 먼저 선택하세요.", "소스 코드 보기",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await AddSourceSplitPaneAsync(path);
        }

        private void SourceTabsMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = SourceTabsMoreButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#23262F" : "#FFFFFF")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#E6EDF3" : "#191F28")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_darkTheme ? "#464C5A" : "#C9D0D8"))
            };
            if (PinnedSourceTabs.Items.Count > 0)
            {
                menu.Items.Add(new MenuItem { Header = "열린 소스 파일", IsEnabled = false });
                foreach (TabItem tab in PinnedSourceTabs.Items.OfType<TabItem>())
                {
                    string? path = tab.Tag as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var item = new MenuItem
                    {
                        Header = Path.GetFileName(path) + (ReferenceEquals(tab, _previewSourceTab) ? "  (미리보기)" : ""),
                        IsCheckable = true,
                        IsChecked = ReferenceEquals(tab, PinnedSourceTabs.SelectedItem),
                        ToolTip = path
                    };
                    item.Click += (_, _) => PinnedSourceTabs.SelectedItem = tab;
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
            }
            var closeAll = new MenuItem
            {
                Header = "모두 닫기",
                IsEnabled = PinnedSourceTabs.Items.Count > 0 || _splitSourceViews.Count > 0 || _currentPreviewPath != null
            };
            closeAll.Click += (_, _) => ClearPinnedSourceTabs();
            menu.Items.Add(closeAll);
            menu.IsOpen = true;
        }

        private void ClearPinnedSourceTabs()
        {
            PinnedSourceTabs.Items.Clear();
            _pinnedSourceFiles.Clear();
            _previewSourceTab = null;
            ClearSplitSourcePanes();
            ResetSourcePreview();
        }

        private void ResetSourcePreview()
        {
            _currentPreviewPath = null;
            SourceCodeViewer.Text = "프로젝트 탐색기에서 파일을 한 번 클릭하면 미리보고, 두 번 클릭하면 탭으로 고정합니다.";
        }

        private async Task AddSourceSplitPaneAsync(string path)
        {
            int splitterColumn = SourceSplitHost.ColumnDefinitions.Count;
            SourceSplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            SourceSplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var splitter = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            splitter.SetResourceReference(BackgroundProperty, "LineBrush");
            Grid.SetColumn(splitter, splitterColumn);
            SourceSplitHost.Children.Add(splitter);

            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var tabHeader = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 0, 12, 0)
            };
            tabHeader.SetResourceReference(Border.BackgroundProperty, "TitleBarBrush");
            tabHeader.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var name = new TextBlock
            {
                Text = Path.GetFileName(path),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = path
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            tabHeader.Child = name;
            pane.Children.Add(tabHeader);

            var viewer = CreateSourceViewer();
            Grid.SetRow(viewer, 1);
            pane.Children.Add(viewer);
            Grid.SetColumn(pane, splitterColumn + 1);
            SourceSplitHost.Children.Add(pane);
            _splitSourceViews.Add(new SplitSourceView(path, viewer));
            await LoadSourceTextAsync(path, viewer, primaryGeneration: null);
        }

        private void ClearSplitSourcePanes()
        {
            SourceSplitHost.Children.Clear();
            SourceSplitHost.ColumnDefinitions.Clear();
            SourceSplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(SourceCodeViewer, 0);
            SourceSplitHost.Children.Add(SourceCodeViewer);
            _splitSourceViews.Clear();
        }

        private TextBox CreateSourceViewer()
        {
            var viewer = new TextBox
            {
                IsReadOnly = true,
                IsUndoEnabled = false,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10, 12, 10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            viewer.SetResourceReference(TextBox.BackgroundProperty, "CodeSurfaceBrush");
            viewer.SetResourceReference(TextBox.ForegroundProperty, "CodeTextBrush");
            viewer.SetResourceReference(TextBox.CaretBrushProperty, "CodeTextBrush");
            viewer.SetResourceReference(TextBox.SelectionBrushProperty, "CodeSelectionBrush");
            return viewer;
        }

        private async Task LoadPrimarySourcePathAsync(string path)
        {
            int generation = ++_sourcePreviewGeneration;
            _currentPreviewPath = path;
            await LoadSourceTextAsync(path, SourceCodeViewer, generation);
        }

        private async Task LoadSourceTextAsync(string path, TextBox viewer, int? primaryGeneration)
        {

            viewer.Text = "소스 파일을 읽는 중...";

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("파일을 찾을 수 없습니다.", path);
                if (info.Length > 5 * 1024 * 1024)
                {
                    viewer.Text = "파일이 5MB보다 커서 미리보기를 표시하지 않습니다.\n원본 파일은 실행 범위에 그대로 포함됩니다.";
                    return;
                }

                byte[] bytes = await File.ReadAllBytesAsync(path);
                string source = DecodeSourceText(bytes);
                if (primaryGeneration.HasValue && primaryGeneration.Value != _sourcePreviewGeneration) return;
                viewer.Text = AddLineNumbers(source);
                viewer.ScrollToHome();
            }
            catch (Exception ex)
            {
                if (primaryGeneration.HasValue && primaryGeneration.Value != _sourcePreviewGeneration) return;
                viewer.Text = "소스 파일을 표시할 수 없습니다.\n\n" + ex.Message;
            }
        }

        private static string DecodeSourceText(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949).GetString(bytes);
            }
        }

        private static string AddLineNumbers(string source)
        {
            string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            int width = Math.Max(3, lines.Length.ToString().Length);
            return string.Join(Environment.NewLine,
                lines.Select((line, index) => (index + 1).ToString().PadLeft(width) + "  " + line));
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private void ManageCheckerRulesMenuItem_Click(object sender, RoutedEventArgs e) => OpenRuleManager();

        private void CodeRuleSettingsMenuItem_Click(object sender, RoutedEventArgs e)
            => OpenRuleSettings(commentLayout: false);

        private void CommentRuleSettingsMenuItem_Click(object sender, RoutedEventArgs e)
            => OpenRuleSettings(commentLayout: true);

        private void OpenRuleSettings(bool commentLayout)
        {
            if (_cts != null) return;

            RulesTabs.SelectedItem = commentLayout ? TrackBTab : TrackATab;
            IReadOnlyList<RuleSettingOption> cFamilyOptions = commentLayout
                ? BuildCFamilyCommentRuleOptions()
                : BuildCFamilyCodeRuleOptions();
            IReadOnlyList<RuleSettingOption> csharpOptions = commentLayout
                ? BuildCommentRuleOptions()
                : BuildCodeRuleOptions();
            string title = commentLayout ? "주석·레이아웃 설정" : "코드 규칙 설정";
            var dialog = new RuleSettingsWindow(title, cFamilyOptions, csharpOptions, _darkTheme) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            IReadOnlyDictionary<string, bool> selections = dialog.Selections;
            ApplyCFamilyRuleSelections(selections, commentLayout);
            foreach ((CheckBox CheckBox, bool IsDefault) pair in commentLayout ? CommentRuleControls() : CodeRuleControls())
            {
                if (selections.TryGetValue(pair.CheckBox.Name, out bool selected))
                {
                    pair.CheckBox.IsChecked = selected;
                }
            }

            UpdateRunButtonForTrack();
            UpdateSummary();
        }

        private IReadOnlyList<RuleSettingOption> BuildCodeRuleOptions()
        {
            var controls = CodeRuleControls();
            return controls.Select(pair => BuildCSharpRuleOption(
                pair.CheckBox,
                pair.IsDefault,
                pair.IsDefault ? "기본 자동수정" : "선택 자동수정")).ToList();
        }

        private IReadOnlyList<RuleSettingOption> BuildCommentRuleOptions()
        {
            var controls = CommentRuleControls();
            return controls.Select(pair => BuildCSharpRuleOption(
                pair.CheckBox,
                pair.IsDefault,
                pair.IsDefault ? "기본 주석 규칙" : "선택 레이아웃/주석 규칙")).ToList();
        }

        private RuleSettingOption BuildCSharpRuleOption(CheckBox checkBox, bool isDefault, string group)
        {
            _ruleInfos.TryGetValue(checkBox.Name, out RuleInfo? info);
            string details = info == null
                ? "규칙 설명이 준비되지 않았습니다."
                : info.Summary + Environment.NewLine + info.Checker;
            return new RuleSettingOption(
                checkBox.Name,
                checkBox.Content?.ToString() ?? checkBox.Name,
                group,
                checkBox.IsChecked == true,
                isDefault,
                details,
                info?.Before,
                info?.After);
        }

        private IReadOnlyList<RuleSettingOption> BuildCFamilyCodeRuleOptions() => new[]
        {
            new RuleSettingOption(
                CFamilyCompoundStatementsKey,
                "조건문·반복문 중괄호 추가",
                "기본 자동수정",
                _cFamilyCompoundStatements,
                isDefault: true,
                "기준: MISRA C:2012 Rule 15.6 / CWE-483\n설명: if, else, for, while, do 본문을 중괄호가 있는 복합문으로 만들어 제어 흐름의 오해를 방지합니다.",
                "if (ready)\n    run();",
                "if (ready)\n{\n    run();\n}"),
            new RuleSettingOption(
                CFamilyFinalElseKey,
                "else-if 체인의 최종 else 추가",
                "선택 자동수정",
                _cFamilyFinalElse,
                isDefault: false,
                "기준: MISRA C:2012 Rule 15.7\n설명: if-else if 체인을 최종 else로 끝내 예상하지 못한 조건을 명시적으로 처리합니다. 단독 if문은 별도의 'if-else문에서 else 누락' 규칙에서 처리합니다.",
                "if (state == READY)\n{\n    start();\n}\nelse if (state == STOPPED)\n{\n    stop();\n}",
                "if (state == READY)\n{\n    start();\n}\nelse if (state == STOPPED)\n{\n    stop();\n}\nelse\n{\n    asm(\"nop\");\n}"),
            new RuleSettingOption(
                CFamilyMissingElseKey,
                "if-else문에서 else 누락",
                "선택 자동수정",
                _cFamilyMissingElse,
                isDefault: false,
                "기준: 프로젝트 자체 규칙(MISRA C:2012 및 CWE-658/659 직접 대응 없음)\n설명: else가 없는 단독 if문을 검출하고 else 본문에 asm(\"nop\");을 추가합니다.",
                "if (ready)\n{\n    run();\n}",
                "if (ready)\n{\n    run();\n}\nelse\n{\n    asm(\"nop\");\n}"),
            new RuleSettingOption(
                CFamilySwitchDefaultKey,
                "switch 문의 default 추가",
                "선택 자동수정",
                _cFamilySwitchDefault,
                isDefault: false,
                "기준: MISRA C:2012 Rule 16.4 / CWE-478\n설명: switch 문에 default 레이블을 추가하고 asm(\"nop\");과 break;를 넣어 열거되지 않은 값의 처리 경로를 명확하게 합니다.",
                "switch (state)\n{\ncase READY:\n    start();\n    break;\n}",
                "switch (state)\n{\ncase READY:\n    start();\n    break;\ndefault:\n    /* Unexpected state. */\n    asm(\"nop\");\n    break;\n}"),
            new RuleSettingOption(
                CFamilyLogicalParenthesesKey,
                "논리식 괄호 명확화",
                "기본 자동수정",
                _cFamilyLogicalParentheses,
                isDefault: true,
                "기준: MISRA C:2012 Rule 12.1 / CWE-783\n설명: 서로 다른 논리 연산자가 섞인 식의 평가 순서를 괄호로 명시해 연산자 우선순위 오해를 방지합니다.",
                "if (ready && valid || forced)\n{\n    run();\n}",
                "if ((ready && valid) || forced)\n{\n    run();\n}"),
            new RuleSettingOption(
                CFamilyUnsignedSuffixKey,
                "unsigned 정수 상수에 U 접미사 추가",
                "선택 자동수정",
                _cFamilyUnsignedSuffix,
                isDefault: false,
                "기준: MISRA C:2012 Rule 7.2\n설명: unsigned 타입과 함께 사용하는 정수 상수에 U 접미사를 붙여 상수의 부호를 명확하게 합니다.",
                "uint32_t mask = 1 << bit;",
                "uint32_t mask = 1U << bit;"),
            new RuleSettingOption(
                CFamilyIgnoredReturnKey,
                "무시한 반환값을 명시적으로 표시",
                "선택 자동수정",
                _cFamilyIgnoredReturn,
                isDefault: false,
                "기준: MISRA C:2012 Rule 17.7 / CWE-252\n설명: 반환값을 의도적으로 사용하지 않는 호출은 void 캐스트로 표시하고, 오류 처리가 필요한 호출은 검토 대상으로 구분합니다.",
                "log_flush();",
                "(void)log_flush();"),
            new RuleSettingOption(
                CFamilySizeOfPointeeKey,
                "sizeof(pointer)를 sizeof(*pointer)로 보정",
                "선택 자동수정",
                _cFamilySizeOfPointee,
                isDefault: false,
                "기준: CWE-467 (CWE-658/659 관련)\n설명: 메모리 크기를 계산하는 문맥에서 포인터 자체의 크기 대신 포인터가 가리키는 객체 크기를 사용합니다.",
                "buffer = malloc(count * sizeof(buffer));",
                "buffer = malloc(count * sizeof(*buffer));"),
            new RuleSettingOption(
                CFamilyFixedWidthTypesKey,
                "고정폭 정수 typedef 사용",
                "선택 자동수정",
                _cFamilyFixedWidthTypes,
                isDefault: false,
                "기준: MISRA C:2012 Directive 4.6\n설명: 프로젝트에서 설정한 타입 매핑에 따라 기본 정수형을 크기와 부호가 드러나는 typedef로 바꿉니다. 공개 API와 구조체 필드는 검토 대상입니다.",
                "unsigned int count;\nint result;",
                "uint32_t count;\nint32_t result;")
        };

        private IReadOnlyList<RuleSettingOption> BuildCFamilyCommentRuleOptions() => new[]
        {
            new RuleSettingOption(CFamilyTrailingCommentKey, "코드 뒤 주석을 위 줄로 이동", "기본 주석 규칙", _cFamilyTrailingComment, true),
            new RuleSettingOption(CFamilyCommentSpaceKey, "주석 기호 뒤 공백 추가", "기본 주석 규칙", _cFamilyCommentSpace, true),
            new RuleSettingOption(CFamilyCommentPeriodKey, "주석 끝 마침표 추가", "기본 주석 규칙", _cFamilyCommentPeriod, true),
            new RuleSettingOption(CFamilyCommentCapitalizeKey, "주석 첫 영문 대문자화", "기본 주석 규칙", _cFamilyCommentCapitalize, true)
        };

        private void ApplyCFamilyRuleSelections(IReadOnlyDictionary<string, bool> selections, bool commentLayout)
        {
            if (commentLayout)
            {
                if (selections.TryGetValue(CFamilyTrailingCommentKey, out bool trailing)) _cFamilyTrailingComment = trailing;
                if (selections.TryGetValue(CFamilyCommentSpaceKey, out bool space)) _cFamilyCommentSpace = space;
                if (selections.TryGetValue(CFamilyCommentPeriodKey, out bool period)) _cFamilyCommentPeriod = period;
                if (selections.TryGetValue(CFamilyCommentCapitalizeKey, out bool capitalize)) _cFamilyCommentCapitalize = capitalize;
                return;
            }

            if (selections.TryGetValue(CFamilyCompoundStatementsKey, out bool compound)) _cFamilyCompoundStatements = compound;
            if (selections.TryGetValue(CFamilyFinalElseKey, out bool finalElse)) _cFamilyFinalElse = finalElse;
            if (selections.TryGetValue(CFamilyMissingElseKey, out bool missingElse)) _cFamilyMissingElse = missingElse;
            if (selections.TryGetValue(CFamilySwitchDefaultKey, out bool switchDefault)) _cFamilySwitchDefault = switchDefault;
            if (selections.TryGetValue(CFamilyLogicalParenthesesKey, out bool parentheses)) _cFamilyLogicalParentheses = parentheses;
            if (selections.TryGetValue(CFamilyUnsignedSuffixKey, out bool unsignedSuffix)) _cFamilyUnsignedSuffix = unsignedSuffix;
            if (selections.TryGetValue(CFamilyIgnoredReturnKey, out bool ignoredReturn)) _cFamilyIgnoredReturn = ignoredReturn;
            if (selections.TryGetValue(CFamilySizeOfPointeeKey, out bool sizeOfPointee)) _cFamilySizeOfPointee = sizeOfPointee;
            if (selections.TryGetValue(CFamilyFixedWidthTypesKey, out bool fixedWidthTypes)) _cFamilyFixedWidthTypes = fixedWidthTypes;
        }

        private List<(CheckBox CheckBox, bool IsDefault)> CodeRuleControls() => new List<(CheckBox, bool)>
        {
            (ASObjectVarSafe, true), (ASObviousVar, true), (ASArrayVarSafe, true), (ASParens, true),
            (ASForeachCast, false), (ASObjectInitializer, false), (ASNullVar, false),
            (ASObjectVarNarrowing, false), (ASLocalConst, false), (ASArrayVarNarrowing, false),
            (ASForVar, false), (ASFieldSplit, false), (ASEmptyStmt, false), (ASForHoist, false)
        };

        private List<(CheckBox CheckBox, bool IsDefault)> CommentRuleControls() => new List<(CheckBox, bool)>
        {
            (BTrailing, true), (BSpace, true), (BPeriod, true), (BCapitalize, true),
            (BFlatten, false), (BMemberBlank, false), (BOneDeclaration, false),
            (BOneStatement, false), (BContinuation, false), (BLinqAlign, false), (BBlockPromote, false)
        };

        private void RunXlsMenuItem_Click(object sender, RoutedEventArgs e) => RunXlsFromMenu();

        private void RunXlsFromMenu()
        {
            if (_cts != null) return;
            _activeTaskName = "XLS 분리";
            SectionTabs.SelectedItem = SectionXlsTab;
            RunButton_Click(this, new RoutedEventArgs());
        }

        private void StopXlsMenuItem_Click(object sender, RoutedEventArgs e)
            => StopButton_Click(sender, e);

        private void OpenXlsOutputMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (IsCSharpSection()) return;
            OpenTrackCOutputButton_Click(sender, e);
        }

        private void RunCommentLayoutMenuItem_Click(object sender, RoutedEventArgs e)
            => RunCommentLayoutFromMenu();

        private void RunCodeRulesMenuItem_Click(object sender, RoutedEventArgs e)
            => RunCodeRulesFromMenu();

        private void RunCodeRulesFromMenu()
        {
            if (_cts != null) return;
            _activeTaskName = "코드 규칙 수정";
            if (IsXlsSection()) SectionTabs.SelectedItem = SectionFixTab;
            RulesTabs.SelectedItem = TrackATab;
            RunButton_Click(this, new RoutedEventArgs());
        }

        private void RunCommentLayoutFromMenu()
        {
            if (_cts != null) return;
            _activeTaskName = "주석·레이아웃 수정";
            if (IsXlsSection()) SectionTabs.SelectedItem = SectionFixTab;
            RulesTabs.SelectedItem = TrackBTab;
            RunButton_Click(this, new RoutedEventArgs());
        }

        private void OpenTargetFolderMenuItem_Click(object sender, RoutedEventArgs e)
            => OpenTargetFolderFromMenu();

        private void OpenTargetFolderFromMenu()
        {
            if (_cts != null || IsCSharpSection()) return;
            SectionTabs.SelectedItem = SectionFixTab;
            OpenTargetButton_Click(this, new RoutedEventArgs());
        }

        private void ViewHelpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenHelpWindow();
        }

        private void OpenHelpWindow()
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (window is HelpWindow existing)
                {
                    if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    return;
                }
            }

            new HelpWindow { Owner = this }.Show();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OpenLogWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow != null)
            {
                if (_logWindow.WindowState == WindowState.Minimized) _logWindow.WindowState = WindowState.Normal;
                _logWindow.Activate();
                return;
            }

            _logWindow = new LogWindow { Owner = this };
            _logWindow.SetLog(LogBox.Text);
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            // 체크박스를 쓰지 않는다. ContextMenu가 다시 만들어질 때 두 항목이 동시에 체크되는 WPF 상태를
            // 원천 차단하고 현재 테마는 하나의 체크 기호로만 표시한다.
            var light = new MenuItem { Header = (_darkTheme ? "   " : "✓ ") + "밝은 테마" };
            var dark = new MenuItem { Header = (_darkTheme ? "✓ " : "   ") + "어두운 테마" };
            light.Click += (_, _) => ApplyTheme(false);
            dark.Click += (_, _) => ApplyTheme(true);
            menu.Items.Add(light);
            menu.Items.Add(dark);
            menu.IsOpen = true;
        }

        private void ApplyTheme(bool dark)
        {
            _darkTheme = dark;
            SetThemeBrush("AccentBrush", dark ? "#4CC2FF" : "#0064FF");
            SetThemeBrush("TextPrimaryBrush", dark ? "#E6EDF3" : "#191F28");
            SetThemeBrush("TextSecondaryBrush", dark ? "#B5BDC9" : "#4E5968");
            SetThemeBrush("TextTertiaryBrush", dark ? "#7D8796" : "#8B95A1");
            SetThemeBrush("LineBrush", dark ? "#343842" : "#D8DDE3");
            SetThemeBrush("PanelBrush", dark ? "#23262F" : "#FFFFFF");
            SetThemeBrush("ControlBrush", dark ? "#2C303A" : "#FFFFFF");
            SetThemeBrush("ControlBorderBrush", dark ? "#464C5A" : "#C9D0D8");
            SetThemeBrush("InputSurfaceBrush", dark ? "#1E2129" : "#FFFFFF");
            SetThemeBrush("SelectedBrush", dark ? "#283B4D" : "#E8F2FF");
            SetThemeBrush("HoverBrush", dark ? "#2C303A" : "#F1F4F6");
            SetThemeBrush("TitleBarBrush", dark ? "#17191F" : "#FFFFFF");
            SetThemeBrush("StatusBarBrush", dark ? "#17191F" : "#F8FAFC");
            SetThemeBrush("CodeSurfaceBrush", dark ? "#202330" : "#FFFFFF");
            SetThemeBrush("CodeTextBrush", dark ? "#E6EDF3" : "#191F28");
            SetThemeBrush("CodeSelectionBrush", dark ? "#315B7D" : "#B9D7FF");
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#181A20" : "#F6F8FA"));
            Foreground = (Brush)Resources["TextPrimaryBrush"];
        }

        private void SetThemeBrush(string key, string color)
        {
            // StaticResource로 소비된 브러시는 WPF가 Freeze할 수 있어 Color를 직접 바꾸면 앱이 종료된다.
            // 항상 새 브러시를 리소스에 넣어 DynamicResource 소비자만 안전하게 갱신한다.
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Assembly assembly = typeof(MainWindow).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "알 수 없음";
            string runtime = RuntimeInformation.FrameworkDescription;

            MessageBox.Show(
                this,
                "Sparrow Helper\n\n" +
                "프로그램 버전: " + version + "\n" +
                ".NET 런타임: " + runtime + "\n\n" +
                "지원되는 언어\n" +
                "• 코드 자동수정: C# 전체 규칙\n" +
                "• 기본 규칙 자동수정: C, C++, H, HPP\n" +
                "• XLS 결과 분리: C, C++, C# 및 기타 Sparrow 지원 언어\n\n" +
                "대상 프로젝트: .NET Framework 4.7.2 레거시 C# 포함\n" +
                "실행 환경: .NET 8 기반 Windows 응용 프로그램",
                "Sparrow Helper 정보",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Track C 기동 인자를 UI에 반영한다: xls/출력 프리필이 있으면 [XLS 분리] 대분류를 선택하고 경로 상자를 채운다.
        // (autorun만 준 경우에도 XLS 분리 화면으로 전환한다.)
        private void ApplyStartupTrackCPrefill()
        {
            bool any = false;
            if (!string.IsNullOrWhiteSpace(_startupTrackCXls))
            {
                TrackCXlsPathBox.Text = _startupTrackCXls!.Trim().Trim('"');
                any = true;
            }
            if (!string.IsNullOrWhiteSpace(_startupTrackCOut))
            {
                TrackCOutputPathBox.Text = _startupTrackCOut!.Trim().Trim('"');
                any = true;
            }
            if (any || _startupTrackCAutorun)
            {
                SectionTabs.SelectedItem = SectionXlsTab;   // SelectionChanged가 버튼/요약/안내를 갱신
            }
        }

        // autorun: 실제 소스 트리 없이 xls만으로 Track C 를 구동한다(테스트 하네스 경로). 스코프 필터(FilesFrom)는
        // 비워 전건을 익스포트한다. 그 외에는 RunButton 의 인프로세스 경로(SparrowExporter.Run + CheckerRuleMapper.Apply)를
        // 그대로 재사용한다.
        private async Task AutoRunTrackCAsync()
        {
            string trackCXls = TrackCXlsPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(trackCXls) || !File.Exists(trackCXls))
            {
                AppendLog("autorun 취소: Track C XLS 를 찾을 수 없습니다 (" + trackCXls + ")");
                return;
            }
            if (_cts != null) return;   // 이미 실행 중

            _cts = new CancellationTokenSource();
            SetRunning(true);
            _lastTrackCOutputDir = null;
            OpenTrackCOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog(">>> autorun: Track C (스코프 필터 없음 · 전건)");
            AppendLog("track-c xls: " + trackCXls);
            AppendLog(new string('-', 72));
            try
            {
                _lastTrackCOutputDir = await RunTrackCAsync(trackCXls, sourceRoot: "", filesFrom: "", _cts.Token);
                OpenTrackCOutputButton.IsEnabled = Directory.Exists(_lastTrackCOutputDir);
                await RefreshTrackCSummaryFromOutputAsync(_lastTrackCOutputDir);
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
                // 자동 지점 3(autorun 경로): Track C 실행 완료 후(메인 창).
                _snapshots?.CaptureWhenIdle(this, "after-run");
            }
        }

        // XLS 분리 대분류가 선택되어 있나(= Track C 화면).
        private bool IsXlsSection() => ReferenceEquals(SectionTabs.SelectedItem, SectionXlsTab);
        private bool IsCFamilySection() => ReferenceEquals(SectionTabs.SelectedItem, SectionFixTab);
        private bool IsCSharpSection() => ReferenceEquals(SectionTabs.SelectedItem, SectionCSharpTab);

        private ActiveTrack CurrentTrack()
        {
            if (IsXlsSection()) return ActiveTrack.C;   // XLS 분리 화면은 항상 Track C
            object? selected = RulesTabs.SelectedItem;
            if (ReferenceEquals(selected, TrackATab)) return ActiveTrack.A;
            if (ReferenceEquals(selected, TrackBTab)) return ActiveTrack.B;
            return ActiveTrack.None; // 방어용: 로드 전 등 어느 하위 탭도 선택되지 않은 순간
        }

        private void UpdateRunButtonForTrack()
        {
            // 대분류별로 의미 없는 보조 버튼은 아예 감춘다(대상 폴더 = A/B 전용, 출력 폴더 = XLS 분리 전용).
            // 비활성으로만 두면 "쓸 수 없는 버튼이 계속 보이는" 상태라 대분류를 나눈 취지(관련 없는 UI를
            // 아예 안 보이게)에 어긋난다 — 스냅샷 PNG 로 확인해 Visibility 제어로 바꿨다.
            bool xls = IsXlsSection();
            // 실행·폴더 명령은 상단 [실행] 메뉴로 이관했다. 하단 컨트롤은 명령 구현을 공유하기 위한
            // 내부 프록시로만 유지하며 화면에는 노출하지 않는다.
            OpenTargetButton.Visibility = Visibility.Collapsed;
            OpenTrackCOutputButton.Visibility = Visibility.Collapsed;
            OpenTargetButton.IsEnabled = !xls && _cts == null;
            OpenTrackCOutputButton.IsEnabled = xls && _cts == null && Directory.Exists(_lastTrackCOutputDir ?? "");
            RunXlsMenuItem.IsEnabled = _cts == null;
            RunCodeRulesMenuItem.IsEnabled = _cts == null;
            StopXlsMenuItem.IsEnabled = _cts != null;
            OpenXlsOutputMenuItem.IsEnabled = _cts == null && Directory.Exists(_lastTrackCOutputDir ?? "");
            RunCommentLayoutMenuItem.IsEnabled = _cts == null;
            OpenTargetFolderMenuItem.IsEnabled = _cts == null;
            CommitPerRuleMenuItem.IsEnabled = _cts == null;

            // 규칙별 커밋은 러너(A/B)가 만드는 것이다. [XLS 분리]는 읽기전용이라 커밋이 없으므로 숨긴다 —
            // 눌러도 아무 의미가 없는 옵션을 남겨 두지 않는다.
            ActiveTrack track = CurrentTrack();
            bool commitApplies = !xls;
            CommitCheck.Visibility = Visibility.Collapsed;
            CommitCheck.IsEnabled = commitApplies && _cts == null;

            switch (track)
            {
                case ActiveTrack.A:
                    RunButton.Content = IsCSharpSection() ? "C# 코드 규칙 수정 실행" : "C/C++ 코드 규칙 수정 실행";
                    RunButton.ToolTip = ModeNotice;
                    RunButton.IsEnabled = _cts == null;
                    break;
                case ActiveTrack.B:
                    RunButton.Content = IsCSharpSection() ? "C# 주석·레이아웃 수정 실행" : "C/C++ 주석·레이아웃 수정 실행";
                    RunButton.ToolTip = ModeNotice;
                    RunButton.IsEnabled = _cts == null;
                    break;
                case ActiveTrack.C:
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

            UpdateFileMenuAvailability();
        }

        // 대분류 전환: 화면이 통째로 바뀌므로 실행 버튼/안내/요약을 그 대분류 기준으로 다시 맞춘다.
        private void SectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, SectionTabs)) return;
            if (!IsLoaded) return;

            UpdateRunButtonForTrack();
            if (!IsXlsSection())
            {
                // [코드 자동수정] 화면으로 돌아오면 하위 탭에 맞는 설명을 다시 띄운다.
                switch (CurrentTrack())
                {
                    case ActiveTrack.B: ShowRuleInfo(nameof(BTrailing)); break;
                    default: ShowRuleInfo(nameof(ASObjectVarSafe)); break;
                }
            }
            UpdateSummary();
        }

        private void UpdateFileMenuAvailability()
        {
            bool enabled = _cts == null && !IsCSharpSection();
            OpenFileMenuItem.IsEnabled = enabled;
            OpenFolderMenuItem.IsEnabled = enabled;
            ChooseOutputFolderMenuItem.IsEnabled = enabled;
            RegisterSourceMenuItem.IsEnabled = enabled;
            OpenTargetFolderMenuItem.IsEnabled = enabled && !IsXlsSection();
            OpenXlsOutputMenuItem.IsEnabled = enabled && Directory.Exists(_lastTrackCOutputDir ?? "");
            FileMenu.ToolTip = IsCSharpSection()
                ? "코드 자동수정 (C#) 탭에서는 종료만 사용할 수 있습니다."
                : null;
        }

        private void RulesTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, RulesTabs)) return;
            // TabControl 초기 선택은 InitializeComponent 도중(다른 명명 요소 생성 전) 발생할 수 있으므로 로드 후에만 처리한다.
            if (!IsLoaded) return;

            UpdateRunButtonForTrack();
            switch (CurrentTrack())
            {
                case ActiveTrack.A:
                    ShowRuleInfo(nameof(ASObjectVarSafe));
                    break;
                case ActiveTrack.B:
                    ShowRuleInfo(nameof(BTrailing));
                    break;
            }
            UpdateSummary();
        }

        private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "파일 열기",
                Filter = "소스 및 프로젝트 (*.c;*.cpp;*.cs;*.h;*.hpp;*.sln;*.csproj)|*.c;*.cpp;*.cs;*.h;*.hpp;*.sln;*.csproj|C/C++ 소스 및 헤더 (*.c;*.cpp;*.h;*.hpp)|*.c;*.cpp;*.h;*.hpp|C# 소스 (*.cs)|*.cs|Solution/Project (*.sln;*.csproj)|*.sln;*.csproj|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            string current = TargetPathBox.Text.Trim().Trim('"');
            if (File.Exists(current)) dlg.InitialDirectory = Path.GetDirectoryName(current);
            else if (Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
            {
                TargetPathBox.Text = dlg.FileName;
            }
        }

        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "소스 폴더 선택"
            };
            string current = TargetPathBox.Text.Trim();
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
            {
                TargetPathBox.Text = dlg.FolderName;
            }
        }

        private void BrowseTrackCXlsButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Sparrow 결과 XLS 선택",
                Filter = "Sparrow 결과 (*.xls;*.xlsx)|*.xls;*.xlsx|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                TrackCXlsPathBox.Text = dlg.FileName;
            }
        }

        private void BrowseTrackCOutputButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Track C 출력 폴더 선택"
            };
            string current = TrackCOutputPathBox.Text.Trim();
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
            {
                TrackCOutputPathBox.Text = dlg.FolderName;
            }
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            // 활성 탭이 곧 실행 트랙이다. 옵션 탭(None)은 실행 대상이 아니며 버튼도 비활성이지만 방어적으로 가드한다.
            ActiveTrack track = CurrentTrack();
            if (track == ActiveTrack.None)
            {
                return;
            }

            // Track C 는 입력이 xls 하나다. 대상/범위 선택은 선택 사항(팀 분담 필터)이라 별도 경로로 처리한다.
            if (track == ActiveTrack.C)
            {
                await RunTrackCInteractiveAsync();
                return;
            }

            bool runTrackA = track == ActiveTrack.A;
            bool runTrackB = track == ActiveTrack.B;

            string target = (IsCSharpSection() ? CSharpTargetPathBox.Text : TargetPathBox.Text).Trim().Trim('"');
            if (string.IsNullOrEmpty(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                MessageBox.Show(this, "대상 .sln/.csproj 또는 소스 폴더를 먼저 선택하세요.", "입력 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SourceScope scope = IsCSharpSection()
                ? await EnsureCSharpScopeAsync(target)
                : await EnsureScopeAsync(target);
            IReadOnlyList<string> selectedFiles = scope.SelectedFiles;
            if (selectedFiles.Count == 0)
            {
                string message = IsCSharpSection()
                    ? "선택된 C# 소스 파일이 없습니다. 왼쪽 작업 범위에서 파일을 선택하세요."
                    : "선택된 소스 파일이 없습니다. 파일 > 소스 파일 등록에서 작업 파일을 선택하세요.";
                MessageBox.Show(this, message, "범위 확인",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 상단 언어 탭이 실행 범위를 결정한다. 같은 프로젝트 범위를 공유하더라도
            // C/C++ 탭에서는 C 계열만, C# 탭에서는 .cs 파일만 러너에 전달한다.
            string[] csharpFiles = IsCSharpSection()
                ? selectedFiles.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>();
            string[] cFamilyFiles = IsCFamilySection()
                ? selectedFiles.Where(IsCFamilyFile).ToArray()
                : Array.Empty<string>();
            if (IsCFamilySection() && cFamilyFiles.Length == 0)
            {
                MessageBox.Show(this, "선택된 C/C++ 소스 파일이 없습니다. 지원 형식: .c, .cpp, .h, .hpp",
                    "실행 범위 확인", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsCSharpSection() && csharpFiles.Length == 0)
            {
                MessageBox.Show(this, "선택된 C# 소스 파일이 없습니다. 왼쪽 작업 범위에서 파일을 선택하세요.",
                    "실행 범위 확인", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string? scopeManifest = null;
            if (csharpFiles.Length > 0)
            {
                try
                {
                    scopeManifest = ScopeManifestWriter.WriteTemp(csharpFiles);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "범위 manifest 생성 실패: " + ex.Message, "범위 확인",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            var jobs = csharpFiles.Length > 0
                ? BuildJobs(target, scopeManifest!, runTrackA, runTrackB)
                : new List<RunnerJob>();
            var cOptions = new CFamilyBasicFixer.Options
            {
                // C에는 var가 없고 C++의 auto도 의미가 달라 기본 var 3종은 적용하지 않는다.
                // C/C++ 탭에서 선택한 코드 규칙과 주석 규칙을 서로 독립적으로 실행한다.
                CompoundStatements = runTrackA && _cFamilyCompoundStatements,
                FinalElse = runTrackA && _cFamilyFinalElse,
                MissingElse = runTrackA && _cFamilyMissingElse,
                SwitchDefault = runTrackA && _cFamilySwitchDefault,
                LogicalParentheses = runTrackA && _cFamilyLogicalParentheses,
                UnsignedSuffix = runTrackA && _cFamilyUnsignedSuffix,
                IgnoredReturn = runTrackA && _cFamilyIgnoredReturn,
                SizeOfPointee = runTrackA && _cFamilySizeOfPointee,
                FixedWidthTypes = runTrackA && _cFamilyFixedWidthTypes,
                TrailingComment = runTrackB && _cFamilyTrailingComment,
                CommentSpace = runTrackB && _cFamilyCommentSpace,
                CommentPeriod = runTrackB && _cFamilyCommentPeriod,
                CommentCapitalize = runTrackB && _cFamilyCommentCapitalize,
            };
            bool hasCFamilyWork = cFamilyFiles.Length > 0 &&
                (cOptions.CompoundStatements || cOptions.FinalElse || cOptions.MissingElse || cOptions.SwitchDefault ||
                 cOptions.LogicalParentheses || cOptions.UnsignedSuffix || cOptions.IgnoredReturn ||
                 cOptions.SizeOfPointee || cOptions.FixedWidthTypes || cOptions.TrailingComment || cOptions.CommentSpace ||
                 cOptions.CommentPeriod || cOptions.CommentCapitalize);
            if (jobs.Count == 0 && !hasCFamilyWork)
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
            _lastTrackCOutputDir = null;
            OpenTrackCOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog("target: " + target);
            AppendLog("scope: " + selectedFiles.Count + " selected / " + scope.TotalFiles + " discovered"
                      + (scope.ExcludedFiles > 0 ? " / " + scope.ExcludedFiles + " excluded" : ""));
            AppendLog("jobs: " + jobs.Count);
            AppendLog("커밋: 하지 않음 (러너에 -NoCommit 고정 — 검토 후 git 으로 직접 커밋하세요)");
            AppendLog(new string('-', 72));

            try
            {
                if (hasCFamilyWork)
                {
                    AppendLog("C/C++ 출력 안내: 별도 결과 파일을 만들지 않고 선택한 원본 소스 파일을 직접 수정합니다.");
                    AppendLog("C/C++ 적용 규칙: " + string.Join(", ", new[]
                    {
                        cOptions.CompoundStatements ? "조건문·반복문 중괄호" : null,
                        cOptions.FinalElse ? "최종 else" : null,
                        cOptions.MissingElse ? "단독 if의 else 누락" : null,
                        cOptions.SwitchDefault ? "switch default" : null,
                        cOptions.LogicalParentheses ? "논리식 괄호" : null,
                        cOptions.UnsignedSuffix ? "unsigned 상수 U 접미사" : null,
                        cOptions.IgnoredReturn ? "반환값 무시 표시" : null,
                        cOptions.SizeOfPointee ? "sizeof 포인터 보정" : null,
                        cOptions.FixedWidthTypes ? "고정폭 정수 타입" : null,
                        cOptions.TrailingComment ? "뒤 주석 이동" : null,
                        cOptions.CommentSpace ? "주석 공백" : null,
                        cOptions.CommentPeriod ? "주석 마침표" : null,
                        cOptions.CommentCapitalize ? "주석 첫 영문 대문자" : null,
                    }.OfType<string>()));
                    int changed = await Task.Run(() => CFamilyBasicFixer.Apply(cFamilyFiles, cOptions, _cts.Token, AppendLog));
                    AppendLog("C/C++ 선택 규칙 완료: " + changed + "개 파일 변경");
                    if (changed == 0)
                        AppendLog("C/C++ 안내: 선택한 규칙과 일치하는 코드 또는 주석이 없거나 이미 규칙에 맞게 작성되어 있습니다.");
                }
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
                if (!string.IsNullOrWhiteSpace(_currentPreviewPath) && File.Exists(_currentPreviewPath))
                    await LoadPrimarySourcePathAsync(_currentPreviewPath);
                foreach (SplitSourceView split in _splitSourceViews.ToList())
                {
                    if (File.Exists(split.Path)) await LoadSourceTextAsync(split.Path, split.Viewer, primaryGeneration: null);
                }
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

        // Track C(XLS 분리) 실행(사용자 트리거). 입력은 xls 하나이고 프로젝트 경로는 쓰지 않는다. 범위 트리에서
        // 고른 항목이 있으면 그 xls 원본 경로를 그대로 manifest 로 넘겨(RootPath 없이) 팀 분담 필터로 쓰고, 아무것도
        // 고르지 않으면 전건이다. export + CheckerRuleMapper.Apply(캐시 반영)로 실행 전에 저장해 둔 지정이 자동
        // 부착되고, 이후 요약을 실행 결과(상태·건수)로 최신화한다.
        private async Task RunTrackCInteractiveAsync()
        {
            string trackCXls = TrackCXlsPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(trackCXls) || !File.Exists(trackCXls))
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
            _lastTrackCOutputDir = null;
            OpenTrackCOutputButton.IsEnabled = false;
            AppendLog("");
            AppendLog(">>> XLS 분리 실행(Track C)" + (filesFrom.Length > 0 ? " (범위 필터 적용)" : " (범위 선택 없음 · 전건)"));
            AppendLog("입력 xls: " + trackCXls);
            AppendLog(new string('-', 72));
            try
            {
                _lastTrackCOutputDir = await RunTrackCAsync(trackCXls, sourceRoot, filesFrom, _cts.Token);
                OpenTrackCOutputButton.IsEnabled = Directory.Exists(_lastTrackCOutputDir);
                await RefreshTrackCSummaryFromOutputAsync(_lastTrackCOutputDir);
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
                // 자동 지점 3: Track C 실행 완료 후(메인 창) — 실행 결과가 반영된 요약/매핑 패널이 찍힌다.
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
            if (IsCSharpSection()) return;
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

        private void OpenTrackCOutputButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsCSharpSection()) return;
            if (string.IsNullOrEmpty(_lastTrackCOutputDir) || !Directory.Exists(_lastTrackCOutputDir))
            {
                MessageBox.Show(this, "열 수 있는 Track C 출력 폴더가 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = _lastTrackCOutputDir, UseShellExecute = true });
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

        private void CSharpTargetPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSummary();
            _ = RefreshCSharpScopeAsync(showErrors: false);
        }

        private void CSharpBrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            var dialog = new OpenFileDialog
            {
                Title = "C# 솔루션 또는 프로젝트 선택",
                Filter = "Solution/Project (*.sln;*.csproj)|*.sln;*.csproj|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            string current = CSharpTargetPathBox.Text.Trim().Trim('"');
            if (File.Exists(current)) dialog.InitialDirectory = Path.GetDirectoryName(current);
            else if (Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) == true) CSharpTargetPathBox.Text = dialog.FileName;
        }

        private void CSharpBrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            var dialog = new OpenFolderDialog { Title = "C# 소스 폴더 선택" };
            string current = CSharpTargetPathBox.Text.Trim().Trim('"');
            if (Directory.Exists(current)) dialog.InitialDirectory = current;
            else if (File.Exists(current)) dialog.InitialDirectory = Path.GetDirectoryName(current);
            if (dialog.ShowDialog(this) == true) CSharpTargetPathBox.Text = dialog.FolderName;
        }

        private async void CSharpRefreshScopeButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCSharpScopeAsync(showErrors: true);
        }

        private void CSharpSelectAllScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in CSharpScopeRoots) root.SetSubtree(true);
            UpdateSummary();
        }

        private void CSharpClearScopeButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (SourceScopeNode root in CSharpScopeRoots) root.SetSubtree(false);
            UpdateSummary();
        }

        private async void CSharpScopeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(e.NewValue is SourceScopeNode node) || !node.IsFile) return;
            CSharpSourcePathText.Text = node.FullPath;
            await LoadSourceTextAsync(node.FullPath, CSharpSourceCodeViewer, primaryGeneration: null);
        }

        // XLS 경로가 설정되는 순간(찾아보기 선택 OR 시작 인자 --trackc-xls 프리필) 실행(export) 없이 체커와 검출
        // 경로만 파싱해 요약·범위 트리를 즉시 채운다. 경로 상자는 IsReadOnly 라 프로그램적 설정 시에만 발생한다.
        private void TrackCXlsPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _ = RefreshTrackCSummaryFromXlsAsync();
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

            string xls = TrackCXlsPathBox.Text.Trim().Trim('"');
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
                HashSet<string>? previousExpandedPaths = null;
                string expectedRoot = ResolveTargetRoot(target);
                if (previousScope != null && SamePath(previousScope.RootPath, expectedRoot))
                {
                    previousExpandedPaths = CaptureExpandedPaths(previousScope.RootNode);
                    int previousSelectable = previousScope.RootNode.EnumerateFiles()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    IReadOnlyList<string> selected = previousScope.SelectedFiles;
                    // 사용자가 [소스 파일 등록]에서 전부 해제한 상태(0개)도 선택 상태다.
                    // 0개를 "선택 정보 없음"으로 취급하면 새로고침 때 전부 선택으로 되돌아간다.
                    if (selected.Count < previousSelectable)
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
                if (previousExpandedPaths != null)
                {
                    RestoreExpandedPaths(scope.RootNode, previousExpandedPaths);
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

        private async Task RefreshCSharpScopeAsync(bool showErrors)
        {
            if (!IsLoaded && !showErrors) return;

            string target = CSharpTargetPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)))
            {
                _currentCSharpScope = null;
                CSharpScopeRoots.Clear();
                CSharpScopeStatusText.Text = "대상 경로를 선택하세요.";
                UpdateSummary();
                return;
            }

            _csharpScopeCts?.Cancel();
            _csharpScopeCts?.Dispose();
            _csharpScopeCts = new CancellationTokenSource();
            CancellationToken token = _csharpScopeCts.Token;

            try
            {
                CSharpScopeStatusText.Text = "C# 소스 파일을 탐색하는 중...";
                SourceScope? previousScope = _currentCSharpScope;
                HashSet<string>? previousSelection = null;
                HashSet<string>? previousExpandedPaths = null;
                string expectedRoot = ResolveTargetRoot(target);
                if (previousScope != null && SamePath(previousScope.RootPath, expectedRoot))
                {
                    previousExpandedPaths = CaptureExpandedPaths(previousScope.RootNode);
                    int previousSelectable = previousScope.RootNode.EnumerateFiles()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    IReadOnlyList<string> selected = previousScope.SelectedFiles;
                    if (selected.Count < previousSelectable)
                    {
                        previousSelection = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                    }
                }

                SourceScope scope = await SourceScopeDiscovery.DiscoverCSharpAsync(
                    target, IncludeGeneratedFiles, token);
                if (token.IsCancellationRequested) return;
                if (previousSelection != null) scope.RootNode.ApplySelection(previousSelection);
                if (previousExpandedPaths != null) RestoreExpandedPaths(scope.RootNode, previousExpandedPaths);

                _currentCSharpScope = scope;
                CSharpScopeRoots.Clear();
                CSharpScopeRoots.Add(scope.RootNode);
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _currentCSharpScope = null;
                CSharpScopeRoots.Clear();
                CSharpScopeStatusText.Text = "범위 탐색 실패: " + ex.Message;
                if (showErrors)
                {
                    MessageBox.Show(this, ex.Message, "C# 범위 탐색 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task<SourceScope> EnsureCSharpScopeAsync(string target)
        {
            string expectedRoot = ResolveTargetRoot(target);
            if (_currentCSharpScope != null && SamePath(_currentCSharpScope.RootPath, expectedRoot))
            {
                return _currentCSharpScope;
            }

            SourceScope scope = await SourceScopeDiscovery.DiscoverCSharpAsync(
                target, IncludeGeneratedFiles, CancellationToken.None);
            _currentCSharpScope = scope;
            CSharpScopeRoots.Clear();
            CSharpScopeRoots.Add(scope.RootNode);
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

        private static HashSet<string> CaptureExpandedPaths(SourceScopeNode root)
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<SourceScopeNode>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                SourceScopeNode node = pending.Pop();
                if (!node.IsFile && node.IsExpanded) expanded.Add(node.FullPath);
                foreach (SourceScopeNode child in node.Children) pending.Push(child);
            }
            return expanded;
        }

        private static void RestoreExpandedPaths(SourceScopeNode root, ISet<string> expandedPaths)
        {
            var pending = new Stack<SourceScopeNode>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                SourceScopeNode node = pending.Pop();
                if (!node.IsFile) node.IsExpanded = expandedPaths.Contains(node.FullPath);
                foreach (SourceScopeNode child in node.Children) pending.Push(child);
            }
        }

        private static bool IsCFamilyFile(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".c", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".h", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase);
        }

        private List<RunnerJob> BuildJobs(string target, string filesFrom, bool runTrackA, bool runTrackB)
        {
            var jobs = new List<RunnerJob>();
            if (!runTrackA && !runTrackB)
            {
                return jobs;
            }

            string logDir = ResolveTargetRoot(target);
            Directory.CreateDirectory(logDir);

            if (runTrackA)
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

            if (runTrackB)
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

        private async Task<string> RunTrackCAsync(string inputXls, string sourceRoot, string filesFrom, CancellationToken cancellationToken)
        {
            // 익스포터 산출물(<체커 키>\{ID}_{파일명}_{라인}.md)을 사용자가 지정한 출력 폴더에 그대로 생성한다.
            // 선행 문서(체커 가이드/프롬프트/판정 계약)는 필요하지 않다 — 입력은 xls 하나뿐이다.
            string outputRoot = ResolveTrackCOutputRoot(inputXls, TrackCOutputPathBox.Text);
            var log = new DispatcherTextWriter(Dispatcher, AppendLog);

            DateTime startedUtc = DateTime.UtcNow;
            var elapsed = Stopwatch.StartNew();

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExportOptions exportOptions = BuildTrackCExportOptions(inputXls, outputRoot, sourceRoot, filesFrom);

                log.WriteLine("");
                log.WriteLine(">>> Track C 체커별 md 분리");
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
                WriteTrackCReport(exportOptions, parse, map, startedUtc, elapsed.ElapsedMilliseconds, log);

                return parse.OutputDir;
            }, cancellationToken);
        }

        // Track C 실행 1회의 진단 리포트(json + 사람이 읽는 .log 요약)를 세션 로그 폴더에 남긴다. 어떤 실패도
        // 실행 결과를 바꾸지 않는다(경고만 로그). 출력 폴더에는 절대 쓰지 않는다.
        private void WriteTrackCReport(ExportOptions exportOptions, ExportResult parse, MapResult map,
                                       DateTime startedUtc, long elapsedMs, TextWriter log)
        {
            string? reportPath = _sessionLog.NewTrackCReportPath();
            if (reportPath == null)
            {
                log.WriteLine("실행 리포트: 로그 폴더를 쓸 수 없어 생략했습니다 (" + _sessionLog.LogDirectory + ")");
                return;
            }

            try
            {
                TrackCRunReport payload = TrackCReportWriter.Build(exportOptions, parse, map, _guidesDir, startedUtc, elapsedMs);
                if (TrackCReportWriter.TryWrite(reportPath, payload, out string? error))
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

        private ExportOptions BuildTrackCExportOptions(string inputXls, string outputDir, string sourceRoot, string filesFrom)
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
        private async Task RefreshTrackCSummaryFromXlsAsync()
        {
            int gen = ++_mappingRefreshGen;
            string xls = TrackCXlsPathBox.Text.Trim().Trim('"');
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
            _trackCCheckers = checkers;
            UpdateTrackCMappingSummary();
            _ruleManager?.UpdateCheckers(_trackCCheckers);   // 관리창이 열려 있으면 체커 매핑 목록도 갱신
        }

        // 실행 후: 출력 폴더의 검출 체커(CheckerRuleMapper.ListCheckers)로 요약을 갱신한다(지정 반영 후 상태).
        private async Task RefreshTrackCSummaryFromOutputAsync(string? outputDir)
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
            _trackCCheckers = checkers;
            UpdateTrackCMappingSummary();
            _ruleManager?.UpdateCheckers(_trackCCheckers);
        }

        // 메인 요약 "검출 체커 N종 · 매핑 M · 미매핑 K"(지정 기준: assignment 가 있고 그 규칙 파일이 실제 존재하는
        // 체커만 매핑으로 센다 — 실행 시 실제 부착되는 것과 일치). 파일명이 체커키와 같아도 지정 안 했으면 미매핑.
        private void UpdateTrackCMappingSummary()
        {
            int total = _trackCCheckers.Count;
            if (total == 0)
            {
                TrackCMappingSummary.Text = "XLS를 선택하면 검출 체커가 요약됩니다.";
                return;
            }

            Dictionary<string, string> assignments = CheckerRuleStore.LoadAssignments(_guidesDir);
            int mapped = _trackCCheckers.Count(c =>
                assignments.TryGetValue(c.Key, out string? rule)
                && !string.IsNullOrWhiteSpace(rule)
                && CheckerRuleStore.RuleExists(_guidesDir, rule!));
            int unmapped = total - mapped;
            TrackCMappingSummary.Text = "검출 체커 " + total + "종 · 매핑 " + mapped + " · 미매핑 " + unmapped;
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
                _ruleManager.UpdateCheckers(_trackCCheckers);
                return;
            }

            var win = new RuleManagerWindow(_guidesDir, _trackCCheckers) { Owner = this };
            win.Closed += (_, _) =>
            {
                _ruleManager = null;
                UpdateTrackCMappingSummary();   // 지정 변경(_assignments.json) 반영
            };
            _ruleManager = win;
            win.Show();
            // 자동 지점 2: 관리창 오픈 직후(레이아웃/행 생성이 끝나는 ContextIdle 에 찍는다).
            _snapshots?.CaptureWhenIdle(win, "manager-open");
        }

        private static string ResolveTrackCOutputRoot(string inputXls, string configuredOutput)
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
            AddRuleInfo(ASObjectInitializer, "연속 대입을 object initializer로 통합",
                "객체 생성 직후 연속된 단순 속성/필드 대입을 initializer로 합칩니다.",
                "체커: PRACTICE.OBJECT_INITIALIZATION.NOT_USED_INITIALIZER. 연속 구간만 처리합니다.",
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
            AddRuleInfo(ASForVar, "for 루프 초기화 변수를 var로 변경",
                "for 초기화절의 명시 타입을 var로 바꿉니다.",
                "체커: 루프 변수 암시적 타입 사용 권장.",
                "for (int i = 0; i < count; i++)\r\n// ->\r\nfor (var i = 0; i < count; i++)");
            AddRuleInfo(ASFieldSplit, "한 줄 다중 필드 선언 분리",
                "한 줄에 여러 필드를 선언한 구문을 필드별 선언으로 나눕니다.",
                "체커: 한 줄에 하나의 선언문 배치.",
                "private int x, y;\r\n// ->\r\nprivate int x;\r\nprivate int y;");
            AddRuleInfo(ASEmptyStmt, "불필요한 빈 문장 제거",
                "불필요한 빈 문장 세미콜론을 제거합니다.",
                "체커: 한 줄에 하나의 구문/불필요 문장 계열.",
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

            ActiveTrack track = CurrentTrack();
            UpdateXlsScopeSummary();
            SourceScope? activeLocalScope = IsCSharpSection() ? _currentCSharpScope : _currentScope;
            int selectedFiles = activeLocalScope?.SelectedFiles.Count ?? 0;
            int totalFiles = activeLocalScope?.TotalFiles ?? 0;
            int excludedFiles = activeLocalScope?.ExcludedFiles ?? 0;

            if (activeLocalScope != null)
            {
                string status = $"{selectedFiles}개 선택 / {totalFiles}개 발견"
                    + (excludedFiles > 0 ? $" / {excludedFiles}개 제외" : "");
                if (IsCSharpSection()) CSharpScopeStatusText.Text = status;
                else ScopeStatusText.Text = status;
            }

            string target = (IsCSharpSection() ? CSharpTargetPathBox.Text : TargetPathBox.Text).Trim();
            SummaryTargetText.Text = string.IsNullOrEmpty(target)
                ? "대상 경로가 필요합니다."
                : target;

            switch (track)
            {
                case ActiveTrack.A:
                {
                    int csharpCount = CountChecked(ASObjectVarSafe, ASObviousVar, ASArrayVarSafe, ASParens, ASForeachCast,
                        ASObjectInitializer, ASNullVar, ASObjectVarNarrowing, ASLocalConst, ASArrayVarNarrowing,
                        ASForVar, ASFieldSplit, ASEmptyStmt, ASForHoist);
                    int cFamilyCount = new[]
                    {
                        _cFamilyCompoundStatements,
                        _cFamilyFinalElse,
                        _cFamilyMissingElse,
                        _cFamilySwitchDefault,
                        _cFamilyLogicalParentheses,
                        _cFamilyUnsignedSuffix,
                        _cFamilyIgnoredReturn,
                        _cFamilySizeOfPointee,
                        _cFamilyFixedWidthTypes
                    }.Count(selected => selected);
                    int review = CountChecked(ASForeachCast, ASNullVar, ASObjectVarNarrowing, ASLocalConst,
                        ASArrayVarNarrowing, ASForHoist);
                    SummaryRulesText.Text = IsCSharpSection()
                        ? $"C# 코드 규칙 · 선택 {csharpCount}개"
                        : $"C/C++ 코드 규칙 · 선택 {cFamilyCount}개";
                    SummaryModeText.Text = IsCSharpSection()
                        ? $"{ModeNotice} · 검토필요 {review} · 선택 파일 {selectedFiles}"
                        : $"{ModeNotice} · 선택 파일 {selectedFiles}";
                    break;
                }
                case ActiveTrack.B:
                {
                    int csharpCount = CountChecked(BTrailing, BSpace, BPeriod, BCapitalize, BFlatten, BMemberBlank,
                        BOneDeclaration, BOneStatement, BContinuation, BLinqAlign, BBlockPromote);
                    int cFamilyCount = new[]
                    {
                        _cFamilyTrailingComment,
                        _cFamilyCommentSpace,
                        _cFamilyCommentPeriod,
                        _cFamilyCommentCapitalize
                    }.Count(selected => selected);
                    int review = CountChecked(BBlockPromote);
                    SummaryRulesText.Text = IsCSharpSection()
                        ? $"C# 주석·레이아웃 · 선택 {csharpCount}개"
                        : $"C/C++ 주석·레이아웃 · 선택 {cFamilyCount}개";
                    SummaryModeText.Text = IsCSharpSection()
                        ? $"{ModeNotice} · 검토필요 {review} · 선택 파일 {selectedFiles}"
                        : $"{ModeNotice} · 선택 파일 {selectedFiles}";
                    break;
                }
                case ActiveTrack.C:
                {
                    // A/B 요약 패널은 XLS 화면에 보이지 않는다(범위·체커 요약은 그 화면 자체에 있다). 화면을
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

            throw new DirectoryNotFoundException("sparrow-static-analysis skill root를 찾을 수 없습니다.");
        }

        private void SetRunning(bool running)
        {
            // 실행 중에는 무조건 비활성. 종료 후에는 활성 트랙(옵션 탭이면 비활성)에 맞춰 상태를 복원한다.
            if (running) RunButton.IsEnabled = false;
            else UpdateRunButtonForTrack();
            StopButton.IsEnabled = running;
            RunXlsMenuItem.IsEnabled = !running;
            RunCodeRulesMenuItem.IsEnabled = !running;
            StopXlsMenuItem.IsEnabled = running;
            OpenXlsOutputMenuItem.IsEnabled = !running && Directory.Exists(_lastTrackCOutputDir ?? "");
            RunCommentLayoutMenuItem.IsEnabled = !running;
            OpenTargetFolderMenuItem.IsEnabled = !running;
            CommitPerRuleMenuItem.IsEnabled = !running;
            BrowseFileButton.IsEnabled = !running;
            BrowseFolderButton.IsEnabled = !running;
            RefreshScopeButton.IsEnabled = !running;
            SelectAllScopeButton.IsEnabled = !running;
            ClearScopeButton.IsEnabled = !running;
            ScopeTree.IsEnabled = !running;
            CSharpBrowseFileButton.IsEnabled = !running;
            CSharpBrowseFolderButton.IsEnabled = !running;
            CSharpRefreshScopeButton.IsEnabled = !running;
            CSharpSelectAllScopeButton.IsEnabled = !running;
            CSharpClearScopeButton.IsEnabled = !running;
            CSharpScopeTree.IsEnabled = !running;
            CSharpTargetPathBox.IsEnabled = !running;
            BrowseTrackCXlsButton.IsEnabled = !running;
            BrowseTrackCOutputButton.IsEnabled = !running;
            SelectAllXlsScopeButton.IsEnabled = !running;
            ClearXlsScopeButton.IsEnabled = !running;
            XlsScopeTree.IsEnabled = !running;
            // 실행 중 대분류 전환 금지(실행 트랙이 화면 선택으로 정해지므로 도중에 바뀌면 안 된다).
            SectionTabs.IsEnabled = !running;
            RulesTabs.IsEnabled = !running;
            TargetPathBox.IsEnabled = !running;
            TrackCXlsPathBox.IsEnabled = !running;
            TrackCOutputPathBox.IsEnabled = !running;
            if (running)
            {
                if (_activeTaskName == "준비됨")
                {
                    _activeTaskName = CurrentTrack() == ActiveTrack.C ? "XLS 분리" : "코드 자동수정";
                }
                StatusText.Text = "실행 중";
                ActiveTaskStatusText.Text = _activeTaskName;
                ExecutionProgressBar.Value = 0;
                ExecutionProgressBar.IsIndeterminate = true;
                ProgressStatusText.Text = "진행 중";
            }
            else
            {
                ExecutionProgressBar.IsIndeterminate = false;
                bool completed = StatusText.Text == "완료";
                ExecutionProgressBar.Value = completed ? 100 : 0;
                ProgressStatusText.Text = completed ? "100%" : "0%";
                ActiveTaskStatusText.Text = completed ? _activeTaskName + " 완료" : _activeTaskName;
                _activeTaskName = "준비됨";
            }
            UpdateFileMenuAvailability();
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
            // C/C++ 수정기는 백그라운드 스레드에서 실행된다. WPF 컨트롤은 생성한 UI 스레드에서만
            // 접근할 수 있으므로 모든 화면 로그 갱신을 Dispatcher로 되돌린다.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
                return;
            }

            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            _logWindow?.AppendLine(line);
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
        //   --trackc-xls <path>   : Track C 탭 선택 + XLS 경로 프리필
        //   --trackc-out <dir>    : Track C 출력 폴더 프리필
        //   --guides-dir <dir>    : 체커 규칙 라이브러리/지정 폴더 override (실 references\checkers 오염 방지)
        //   --log-dir <dir>       : 세션 로그/Track C 리포트 폴더 override (기본 %LOCALAPPDATA%\SparrowRunner\logs)
        //   --screenshot-dir <dir>: 창 스냅샷 PNG 폴더. 주면 자동 지점(메인창 로드/관리창 오픈/Track C 완료)에서
        //                           스스로 캡처하고, 그 폴더에 capture.request 파일이 생기면 활성 창을 즉시 캡처한다.
        //                           주지 않으면 스냅샷 기능 전체가 비활성이다(기존 동작 불변).
        //   --trackc-autorun      : 로드 완료 후 Track C 실행 자동 트리거
        //   --open-rule-manager   : 로드 완료 후 [체커 규칙 관리] 창 자동 오픈(검출 체커 로드된 상태)
        private sealed class StartupOptions
        {
            public string? TrackCXls { get; private set; }
            public string? TrackCOut { get; private set; }
            public string? GuidesDir { get; private set; }
            public string? LogDir { get; private set; }
            public string? ScreenshotDir { get; private set; }
            public bool TrackCAutorun { get; private set; }
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
                        case "--trackc-xls":
                            o.TrackCXls = NextValue(args, ref i);
                            break;
                        case "--trackc-out":
                            o.TrackCOut = NextValue(args, ref i);
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
                        case "--trackc-autorun":
                            o.TrackCAutorun = true;
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

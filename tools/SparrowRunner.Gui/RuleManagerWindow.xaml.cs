using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SparrowXlsExport.Core;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// The separate [체커 규칙 관리] window (opened from the main [XLS 분리] tab). Two areas:
    ///   A. 규칙 라이브러리  — named-rule CRUD (list + name box + editor; [새 규칙]/[규칙 저장]/[삭제]/rename).
    ///                        xls-independent; edits the "&lt;이름&gt;.md" files under the guides directory.
    ///   B. 체커 매핑        — one row per detected checker (key + count + rule ComboBox incl. "— 없음 —").
    ///                        The user assigns a library rule to a checker; [지정 저장] writes _assignments.json.
    ///
    /// There is NO name-based auto-mapping: a combo shows "— 없음 —" unless an explicit assignment (that still names
    /// an existing rule) pre-selects it. Assignments are remembered — reopening prefills the same selection.
    ///
    /// All storage goes through <see cref="CheckerRuleStore"/> using the guides directory passed in by the main
    /// window (which the tests override via --guides-dir to keep the real references\checkers cache untouched).
    /// </summary>
    public partial class RuleManagerWindow : Window
    {
        private readonly string _guidesDir;

        // The rule currently loaded in the A-area editor (null = a brand-new unsaved rule). Used to detect a rename.
        private string? _editingOriginalName;

        /// <summary>Rule library names (bound to <c>RuleList</c>).</summary>
        public ObservableCollection<string> RuleNames { get; } = new ObservableCollection<string>();

        /// <summary>Checker mapping rows (bound to <c>AssignList</c>).</summary>
        public ObservableCollection<CheckerAssignmentRow> AssignRows { get; } = new ObservableCollection<CheckerAssignmentRow>();

        public RuleManagerWindow(string guidesDir, IEnumerable<(string Key, int Count)> checkers)
        {
            InitializeComponent();
            _guidesDir = guidesDir;
            RuleList.ItemsSource = RuleNames;
            LoadRules();
            UpdateCheckers(checkers);
        }

        // Rebuild the checker mapping rows for a (possibly new) detection set. Called on construction and whenever
        // the main window's detected checkers change (xls reselected / after a run). Preserves nothing transient —
        // selections are re-derived from the persisted assignments each time.
        public void UpdateCheckers(IEnumerable<(string Key, int Count)> checkers)
        {
            foreach (CheckerAssignmentRow old in AssignRows) old.PropertyChanged -= Row_PropertyChanged;
            AssignRows.Clear();

            Dictionary<string, string> assignments = CheckerRuleStore.LoadAssignments(_guidesDir);
            var rows = (checkers ?? Enumerable.Empty<(string Key, int Count)>())
                .Select(c => new CheckerAssignmentRow(
                    c.Key, c.Count, RuleNames,
                    assignments.TryGetValue(c.Key, out string? rn) ? rn : null))
                // 미지정 우선 정렬(손봐야 할 체커를 위로), 그룹 내 체커 키 사전순.
                .OrderBy(r => r.IsAssigned ? 1 : 0)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .ToList();

            foreach (CheckerAssignmentRow row in rows)
            {
                row.PropertyChanged += Row_PropertyChanged;
                AssignRows.Add(row);
            }

            UpdateAssignEmptyState();
            UpdateAssignStatus();
        }

        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CheckerAssignmentRow.SelectedRule)
                || e.PropertyName == nameof(CheckerAssignmentRow.IsAssigned))
            {
                UpdateAssignStatus();
            }
        }

        // ===== A. 규칙 라이브러리 =====

        // Reload the rule library names and re-point every checker combo to the new set (a deleted/renamed rule
        // that a row still pointed at falls back to "— 없음 —" via CheckerAssignmentRow.SetAvailableRules).
        private void LoadRules()
        {
            IReadOnlyList<string> names = CheckerRuleStore.ListRules(_guidesDir);
            RuleNames.Clear();
            foreach (string n in names) RuleNames.Add(n);
            foreach (CheckerAssignmentRow row in AssignRows) row.SetAvailableRules(RuleNames);
            UpdateRuleEmptyState();

            // 첫 규칙 자동 선택: 목록에 규칙이 있는데 이름/내용이 빈칸이면 "규칙이 비었나?" 로 오해한다. 창을 열면
            // (그리고 저장/삭제로 목록이 바뀌어 선택이 풀리면) 첫 규칙을 골라 내용까지 실어 둔다. 이미 고른 규칙이
            // 있으면 건드리지 않는다(호출자가 저장 직후 특정 규칙을 다시 고르는 경로를 방해하지 않는다).
            if (RuleList.SelectedItem == null && RuleNames.Count > 0)
            {
                RuleList.SelectedIndex = 0;   // SelectionChanged 가 이름/본문을 로드한다
            }
        }

        private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuleList.SelectedItem is not string name)
            {
                return;
            }
            _editingOriginalName = name;
            RuleNameBox.Text = name;
            RuleEditor.Text = CheckerRuleStore.ReadRule(_guidesDir, name) ?? "";
            RuleStatusText.Text = "";
        }

        private void RuleNewButton_Click(object sender, RoutedEventArgs e)
        {
            RuleList.SelectedItem = null;
            _editingOriginalName = null;
            RuleNameBox.Text = "";
            RuleEditor.Text = "";
            RuleStatusText.Text = "";
            RuleNameBox.Focus();
        }

        private void RuleSaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = RuleNameBox.Text.Trim();
            if (!CheckerRuleStore.IsValidRuleName(name))
            {
                MessageBox.Show(this,
                    "규칙 이름이 올바르지 않습니다. 경로 구분자나 파일명에 쓸 수 없는 문자, '_' 로 시작하는 이름은 사용할 수 없습니다.",
                    "규칙 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string content = RuleEditor.Text;
            try
            {
                bool isRename = _editingOriginalName != null
                                && !string.Equals(_editingOriginalName, name, StringComparison.Ordinal);

                // Rows currently pointing at the old name (only relevant on rename) — restore them to the new name
                // after LoadRules resets them (the old rule no longer exists in the library).
                List<CheckerAssignmentRow> affected = isRename
                    ? AssignRows.Where(r => string.Equals(r.SelectedRule, _editingOriginalName, StringComparison.Ordinal)).ToList()
                    : new List<CheckerAssignmentRow>();

                CheckerRuleStore.WriteRule(_guidesDir, name, content);
                if (isRename)
                {
                    CheckerRuleStore.DeleteRule(_guidesDir, _editingOriginalName!);
                    MigrateAssignments(_editingOriginalName!, name);
                }

                _editingOriginalName = name;
                LoadRules();
                foreach (CheckerAssignmentRow row in affected) row.SelectedRule = name;
                RuleList.SelectedItem = name;
                UpdateAssignStatus();
                Flash(RuleStatusText, "저장됨");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "규칙 저장 실패: " + ex.Message, "규칙 저장",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RuleDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            string? target = RuleList.SelectedItem as string;
            if (target == null)
            {
                string typed = RuleNameBox.Text.Trim();
                if (CheckerRuleStore.RuleExists(_guidesDir, typed)) target = typed;
            }
            if (target == null)
            {
                MessageBox.Show(this, "삭제할 규칙을 목록에서 선택하세요.", "규칙 삭제",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(this,
                "'" + target + "' 규칙을 삭제할까요?\n이 규칙 파일이 삭제되고, 이 규칙을 가리키던 체커 지정은 해제됩니다.",
                "규칙 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                CheckerRuleStore.DeleteRule(_guidesDir, target);
                RemoveAssignmentsForRule(target);   // clean dangling assignments on disk

                if (string.Equals(_editingOriginalName, target, StringComparison.Ordinal))
                {
                    _editingOriginalName = null;
                    RuleNameBox.Text = "";
                    RuleEditor.Text = "";
                }
                LoadRules();          // resets any combo still pointing at the deleted rule to "— 없음 —"
                UpdateAssignStatus();
                Flash(RuleStatusText, "삭제됨");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "규칙 삭제 실패: " + ex.Message, "규칙 삭제",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== B. 체커 매핑 =====

        // Persist the current checker→rule selections. Merges into the existing _assignments.json so assignments
        // for checkers NOT in the current detection set are preserved (assignment memory across different xls).
        private void AssignSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Dictionary<string, string> map = CheckerRuleStore.LoadAssignments(_guidesDir);
                foreach (CheckerAssignmentRow row in AssignRows)
                {
                    if (row.IsAssigned) map[row.Key] = row.SelectedRule;
                    else map.Remove(row.Key);
                }
                CheckerRuleStore.SaveAssignments(_guidesDir, map);
                UpdateAssignStatus();
                Flash(AssignSavedText, "지정 저장됨");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "지정 저장 실패: " + ex.Message, "지정 저장",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ===== helpers =====

        private void MigrateAssignments(string oldRule, string newRule)
        {
            Dictionary<string, string> map = CheckerRuleStore.LoadAssignments(_guidesDir);
            bool changed = false;
            foreach (string key in map.Keys.ToList())
            {
                if (string.Equals(map[key], oldRule, StringComparison.Ordinal)) { map[key] = newRule; changed = true; }
            }
            if (changed) CheckerRuleStore.SaveAssignments(_guidesDir, map);
        }

        private void RemoveAssignmentsForRule(string ruleName)
        {
            Dictionary<string, string> map = CheckerRuleStore.LoadAssignments(_guidesDir);
            bool changed = false;
            foreach (string key in map.Keys.ToList())
            {
                if (string.Equals(map[key], ruleName, StringComparison.Ordinal)) { map.Remove(key); changed = true; }
            }
            if (changed) CheckerRuleStore.SaveAssignments(_guidesDir, map);
        }

        private void UpdateRuleEmptyState()
        {
            RuleEmptyText.Visibility = RuleNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateAssignEmptyState()
        {
            AssignEmptyText.Visibility = AssignRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateAssignStatus()
        {
            int total = AssignRows.Count;
            int assigned = AssignRows.Count(r => r.IsAssigned);
            AssignStatusText.Text = total == 0 ? "" : ("지정 " + assigned + " · 미지정 " + (total - assigned));
        }

        private static void Flash(TextBlock target, string message)
        {
            target.Text = message;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                target.Text = "";
            };
            timer.Start();
        }
    }
}

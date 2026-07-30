using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// One checker row in the RuleManagerWindow 체커 매핑 area: the detected checker key + its finding count + the
    /// currently-selected library rule (or the <see cref="NoneLabel"/> sentinel = unassigned). The row's ComboBox
    /// binds <see cref="AvailableRules"/> (sentinel first, then rule names) to ItemsSource and <see cref="SelectedRule"/>
    /// to SelectedItem (two-way).
    ///
    /// There is NO name-based auto-mapping: <see cref="SelectedRule"/> starts at the sentinel unless an explicit
    /// assignment (from _assignments.json) names a rule that still exists in the library. A rule file merely NAMED
    /// like the checker key does NOT pre-select the combo.
    /// </summary>
    public sealed class CheckerAssignmentRow : INotifyPropertyChanged
    {
        /// <summary>Sentinel shown as the first combo entry = "no rule assigned to this checker".</summary>
        public const string NoneLabel = "— 없음 —";

        private string _selectedRule = NoneLabel;

        public CheckerAssignmentRow(string key, int itemCount, IEnumerable<string> ruleNames, string? assignedRule)
        {
            Key = key;
            ItemCount = itemCount;
            AvailableRules = new ObservableCollection<string>();
            SetAvailableRules(ruleNames);
            // Pre-select ONLY when the assignment names a rule that actually exists in the library; else sentinel.
            if (assignedRule != null && AvailableRules.Contains(assignedRule)) SelectedRule = assignedRule;
        }

        /// <summary>Original detected checker key. Also the assignment lookup key.</summary>
        public string Key { get; }

        public int ItemCount { get; }
        public string CountText => ItemCount + "건";

        /// <summary>Combo entries: <see cref="NoneLabel"/> first, then the library rule names.</summary>
        public ObservableCollection<string> AvailableRules { get; }

        public string SelectedRule
        {
            get => _selectedRule;
            set
            {
                string v = string.IsNullOrEmpty(value) ? NoneLabel : value;
                if (string.Equals(_selectedRule, v, StringComparison.Ordinal)) return;
                _selectedRule = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAssigned));
            }
        }

        /// <summary>True when a real rule (not the sentinel) is selected.</summary>
        public bool IsAssigned => !string.Equals(_selectedRule, NoneLabel, StringComparison.Ordinal);

        /// <summary>The rule name to persist for this checker, or null when unassigned.</summary>
        public string? AssignedRuleOrNull => IsAssigned ? _selectedRule : null;

        // Rebuild the dropdown after the rule library changes (add/delete/rename). Preserves the current selection
        // when the rule still exists; otherwise falls back to the sentinel (a deleted rule cannot stay selected).
        public void SetAvailableRules(IEnumerable<string> ruleNames)
        {
            var desired = new List<string> { NoneLabel };
            foreach (string r in ruleNames)
            {
                if (!desired.Contains(r)) desired.Add(r);
            }

            // ⚠️ Clear() 를 쓰면 안 된다. ObservableCollection.Clear() 는 CollectionChanged(Reset) 을 내고,
            // 바인딩된 ComboBox 는 SelectedItem 을 유지하면서도 표시(ContentPresenter)가 비어버린다 —
            // UIA 로는 값이 정상으로 보이는데 화면만 빈칸이라, 스냅샷 PNG 로만 잡히는 결함이었다.
            // 그래서 Reset 을 일으키지 않는 diff(제거·삽입·이동)로 목록을 맞춘다: 선택 항목이 컬렉션에서
            // 사라지는 순간이 없으므로 표시가 유지된다.
            for (int i = AvailableRules.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(AvailableRules[i])) AvailableRules.RemoveAt(i);
            }
            for (int i = 0; i < desired.Count; i++)
            {
                int at = AvailableRules.IndexOf(desired[i]);
                if (at < 0) AvailableRules.Insert(Math.Min(i, AvailableRules.Count), desired[i]);
                else if (at != i) AvailableRules.Move(at, i);
            }

            if (!AvailableRules.Contains(_selectedRule)) SelectedRule = NoneLabel;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

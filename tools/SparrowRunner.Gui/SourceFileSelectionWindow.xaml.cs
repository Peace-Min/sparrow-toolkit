using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace SparrowRunner.Gui
{
    public sealed class SourceFileChoice : INotifyPropertyChanged
    {
        private bool _isSelected;
        public SourceFileChoice(string fullPath, string displayPath, bool isSelected)
        {
            FullPath = fullPath;
            DisplayPath = displayPath;
            _isSelected = isSelected;
        }
        public string FullPath { get; }
        public string DisplayPath { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class SourceFileSelectionWindow : Window
    {
        public ObservableCollection<SourceFileChoice> Choices { get; } = new ObservableCollection<SourceFileChoice>();
        public IReadOnlyCollection<string> SelectedFiles => Choices.Where(x => x.IsSelected).Select(x => x.FullPath).ToArray();

        public SourceFileSelectionWindow(string rootPath, IEnumerable<string> allFiles, ISet<string> selectedFiles)
        {
            InitializeComponent();
            DataContext = this;
            foreach (string file in allFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string display;
                try { display = Path.GetRelativePath(rootPath, file); }
                catch { display = file; }
                Choices.Add(new SourceFileChoice(file, display, selectedFiles.Contains(file)));
            }
            UpdateSummary();
            foreach (SourceFileChoice choice in Choices) choice.PropertyChanged += (_, _) => UpdateSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var x in Choices) x.IsSelected = true; }
        private void ClearAll_Click(object sender, RoutedEventArgs e) { foreach (var x in Choices) x.IsSelected = false; }
        private void Register_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
        private void UpdateSummary() { if (SummaryText != null) SummaryText.Text = $"{Choices.Count(x => x.IsSelected)}개 선택 / {Choices.Count}개 발견"; }
    }
}

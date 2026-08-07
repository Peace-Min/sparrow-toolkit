using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SparrowRunner.Gui
{
    public sealed class RuleSettingOption
    {
        public RuleSettingOption(
            string key,
            string label,
            string group,
            bool isSelected,
            bool isDefault,
            string? details = null,
            string? before = null,
            string? after = null)
        {
            Key = key;
            Label = label;
            Group = group;
            IsSelected = isSelected;
            IsDefault = isDefault;
            Details = details ?? "";
            Before = before ?? "";
            After = after ?? "";
        }

        public string Key { get; }
        public string Label { get; }
        public string Group { get; }
        public bool IsSelected { get; }
        public bool IsDefault { get; }
        public string Details { get; }
        public string Before { get; }
        public string After { get; }
    }

    /// <summary>C/C++와 C# 설정을 독립적으로 관리하는 코드/주석 규칙 선택 창.</summary>
    public sealed class RuleSettingsWindow : Window
    {
        private readonly Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly Dictionary<TabItem, IReadOnlyList<RuleSettingOption>> _tabOptions =
            new Dictionary<TabItem, IReadOnlyList<RuleSettingOption>>();
        private readonly IReadOnlyList<RuleSettingOption> _cFamilyOptions;
        private readonly IReadOnlyList<RuleSettingOption> _csharpOptions;
        private readonly Brush _textBrush;
        private readonly Brush _secondaryTextBrush;
        private readonly Brush _lineBrush;
        private readonly Brush _panelBrush;
        private readonly Brush _buttonBrush;
        private readonly Brush _codeBackgroundBrush;
        private TabControl? _languageTabs;

        private sealed class PreviewControls
        {
            public PreviewControls(TextBlock title, TextBlock body, TextBox before, TextBox after)
            {
                Title = title;
                Body = body;
                Before = before;
                After = after;
            }

            public TextBlock Title { get; }
            public TextBlock Body { get; }
            public TextBox Before { get; }
            public TextBox After { get; }
        }

        public RuleSettingsWindow(
            string title,
            IReadOnlyList<RuleSettingOption> cFamilyOptions,
            IReadOnlyList<RuleSettingOption> csharpOptions,
            bool darkTheme)
        {
            _cFamilyOptions = cFamilyOptions;
            _csharpOptions = csharpOptions;
            Title = title;
            Width = 820;
            Height = 700;
            MinWidth = 700;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            FontSize = 13;
            ShowInTaskbar = false;

            _textBrush = BrushFrom(darkTheme ? "#E6EDF3" : "#191F28");
            _secondaryTextBrush = BrushFrom(darkTheme ? "#AAB4C0" : "#59636E");
            _lineBrush = BrushFrom(darkTheme ? "#3A3F4A" : "#D8DDE3");
            _panelBrush = BrushFrom(darkTheme ? "#23262F" : "#FFFFFF");
            _buttonBrush = BrushFrom(darkTheme ? "#2C303A" : "#F6F8FA");
            _codeBackgroundBrush = BrushFrom(darkTheme ? "#0B1220" : "#F6F8FA");
            Background = BrushFrom(darkTheme ? "#181A20" : "#F6F8FA");
            Foreground = _textBrush;

            Content = BuildContent();
        }

        public IReadOnlyDictionary<string, bool> Selections =>
            _boxes.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true, StringComparer.Ordinal);

        private UIElement BuildContent()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _languageTabs = new TabControl
            {
                Margin = new Thickness(18, 14, 18, 12),
                Background = _panelBrush,
                BorderBrush = _lineBrush,
                Foreground = _textBrush
            };

            var cFamilyTab = new TabItem { Header = "C/C++", Foreground = _textBrush };
            cFamilyTab.Content = BuildRuleTab(
                _cFamilyOptions,
                showPreview: _cFamilyOptions.Any(option => !string.IsNullOrWhiteSpace(option.Details)));
            _tabOptions[cFamilyTab] = _cFamilyOptions;
            _languageTabs.Items.Add(cFamilyTab);

            var csharpTab = new TabItem { Header = "C#", Foreground = _textBrush };
            csharpTab.Content = BuildRuleTab(_csharpOptions, showPreview: true);
            _tabOptions[csharpTab] = _csharpOptions;
            _languageTabs.Items.Add(csharpTab);
            _languageTabs.SelectedItem = cFamilyTab;

            Grid.SetRow(_languageTabs, 0);
            root.Children.Add(_languageTabs);

            var footer = new Border
            {
                Background = _panelBrush,
                BorderBrush = _lineBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 12, 18, 12)
            };
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectionButtons = new StackPanel { Orientation = Orientation.Horizontal };
            selectionButtons.Children.Add(MakeButton("현재 탭 전체 선택", (_, _) => SetCurrentTab(true)));
            selectionButtons.Children.Add(MakeButton("현재 탭 전체 해제", (_, _) => SetCurrentTab(false), new Thickness(7, 0, 0, 0)));
            selectionButtons.Children.Add(MakeButton("현재 탭 기본값", (_, _) => RestoreCurrentTabDefaults(), new Thickness(7, 0, 0, 0)));
            footerGrid.Children.Add(selectionButtons);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = MakeButton("취소", (_, _) => { DialogResult = false; Close(); });
            cancel.IsCancel = true;
            actionButtons.Children.Add(cancel);
            var ok = MakeButton("확인", (_, _) => { DialogResult = true; Close(); }, new Thickness(7, 0, 0, 0));
            ok.IsDefault = true;
            ok.Background = BrushFrom("#0064FF");
            ok.BorderBrush = BrushFrom("#0064FF");
            ok.Foreground = Brushes.White;
            actionButtons.Children.Add(ok);
            Grid.SetColumn(actionButtons, 1);
            footerGrid.Children.Add(actionButtons);
            footer.Child = footerGrid;
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            return root;
        }

        private UIElement BuildRuleTab(IReadOnlyList<RuleSettingOption> options, bool showPreview)
        {
            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (showPreview)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(238) });
            }

            PreviewControls? previewControls = null;
            if (showPreview)
            {
                Border preview = BuildPreviewPanel(out previewControls);
                Grid.SetRow(preview, 1);
                grid.Children.Add(preview);
            }

            var groups = new StackPanel();
            foreach (IGrouping<string, RuleSettingOption> group in options.GroupBy(option => option.Group))
            {
                groups.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _textBrush,
                    Margin = new Thickness(0, groups.Children.Count == 0 ? 0 : 18, 0, 8)
                });

                var choices = new UniformGrid { Columns = 2 };
                foreach (RuleSettingOption option in group)
                {
                    var checkBox = new CheckBox
                    {
                        Content = option.Label,
                        IsChecked = option.IsSelected,
                        Foreground = _textBrush,
                        Margin = new Thickness(0, 5, 18, 5),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Tag = option.Key,
                        ToolTip = showPreview ? option.Label + Environment.NewLine + option.Details : null
                    };
                    AutomationProperties.SetAutomationId(checkBox, option.Key);
                    _boxes[option.Key] = checkBox;
                    if (previewControls != null)
                    {
                        PreviewControls controls = previewControls;
                        checkBox.MouseEnter += (_, _) => ShowPreview(option, controls);
                        checkBox.GotKeyboardFocus += (_, _) => ShowPreview(option, controls);
                        checkBox.Click += (_, _) => ShowPreview(option, controls);
                        ToolTipService.SetShowDuration(checkBox, 12000);
                    }
                    choices.Children.Add(checkBox);
                }
                groups.Children.Add(choices);
            }

            var scroll = new ScrollViewer
            {
                Content = groups,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 0);
            grid.Children.Add(scroll);

            if (previewControls != null && options.Count > 0)
            {
                ShowPreview(options[0], previewControls);
            }

            return grid;
        }

        private Border BuildPreviewPanel(out PreviewControls controls)
        {
            var panel = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(14, 10, 14, 12),
                Background = _panelBrush,
                BorderBrush = _lineBrush,
                BorderThickness = new Thickness(1)
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var description = new StackPanel();
            var previewTitle = new TextBlock
            {
                Text = "규칙 설명",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _textBrush
            };
            var previewBody = new TextBlock
            {
                Text = "규칙에 마우스를 올리면 기준, 설명과 변경 예시가 표시됩니다.",
                Margin = new Thickness(0, 4, 0, 8),
                Foreground = _secondaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetAutomationId(previewTitle, "RulePreviewTitle");
            AutomationProperties.SetAutomationId(previewBody, "RulePreviewDescription");
            description.Children.Add(previewTitle);
            description.Children.Add(previewBody);
            grid.Children.Add(description);

            var examples = new Grid();
            examples.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            examples.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            examples.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBox beforeBox = BuildExampleBox("Before", examples, 0);
            TextBox afterBox = BuildExampleBox("After", examples, 2);
            Grid.SetRow(examples, 1);
            grid.Children.Add(examples);

            panel.Child = grid;
            controls = new PreviewControls(previewTitle, previewBody, beforeBox, afterBox);
            return panel;
        }

        private TextBox BuildExampleBox(string title, Grid parent, int column)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = title,
                Margin = new Thickness(0, 0, 0, 5),
                Foreground = _secondaryTextBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            });
            var box = new TextBox
            {
                IsReadOnly = true,
                IsUndoEnabled = false,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                Background = _codeBackgroundBrush,
                Foreground = _textBrush,
                BorderBrush = _lineBrush,
                Padding = new Thickness(9)
            };
            AutomationProperties.SetAutomationId(box, "RulePreview" + title);
            Grid.SetRow(box, 1);
            grid.Children.Add(box);
            Grid.SetColumn(grid, column);
            parent.Children.Add(grid);
            return box;
        }

        private static void ShowPreview(RuleSettingOption option, PreviewControls controls)
        {
            controls.Title.Text = option.Label;
            controls.Body.Text = option.Details;
            controls.Before.Text = option.Before;
            controls.After.Text = option.After;
        }

        private Button MakeButton(string text, RoutedEventHandler click, Thickness? margin = null)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 82,
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = margin ?? new Thickness(0),
                Background = _buttonBrush,
                BorderBrush = _lineBrush,
                Foreground = _textBrush
            };
            button.Click += click;
            return button;
        }

        private IReadOnlyList<RuleSettingOption> CurrentTabOptions()
        {
            if (_languageTabs?.SelectedItem is TabItem selected && _tabOptions.TryGetValue(selected, out var options))
            {
                return options;
            }
            return Array.Empty<RuleSettingOption>();
        }

        private void SetCurrentTab(bool selected)
        {
            foreach (RuleSettingOption option in CurrentTabOptions())
            {
                _boxes[option.Key].IsChecked = selected;
            }
        }

        private void RestoreCurrentTabDefaults()
        {
            foreach (RuleSettingOption option in CurrentTabOptions())
            {
                _boxes[option.Key].IsChecked = option.IsDefault;
            }
        }

        private static SolidColorBrush BrushFrom(string color) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}

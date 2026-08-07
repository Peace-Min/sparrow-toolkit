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
        public RuleSettingOption(string key, string label, string group, bool isSelected, bool isDefault)
        {
            Key = key;
            Label = label;
            Group = group;
            IsSelected = isSelected;
            IsDefault = isDefault;
        }

        public string Key { get; }
        public string Label { get; }
        public string Group { get; }
        public bool IsSelected { get; }
        public bool IsDefault { get; }
    }

    /// <summary>메인 화면에서 분리된 코드/주석 규칙 선택 창.</summary>
    public sealed class RuleSettingsWindow : Window
    {
        private readonly Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly IReadOnlyList<RuleSettingOption> _options;
        private readonly Brush _textBrush;
        private readonly Brush _lineBrush;
        private readonly Brush _panelBrush;
        private readonly Brush _buttonBrush;

        public RuleSettingsWindow(string title, IReadOnlyList<RuleSettingOption> options, bool darkTheme)
        {
            _options = options;
            Title = title;
            Width = 780;
            Height = 570;
            MinWidth = 660;
            MinHeight = 470;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            FontSize = 13;
            ShowInTaskbar = false;

            _textBrush = BrushFrom(darkTheme ? "#E6EDF3" : "#191F28");
            _lineBrush = BrushFrom(darkTheme ? "#3A3F4A" : "#D8DDE3");
            _panelBrush = BrushFrom(darkTheme ? "#23262F" : "#FFFFFF");
            _buttonBrush = BrushFrom(darkTheme ? "#2C303A" : "#F6F8FA");
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

            var groups = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
            foreach (IGrouping<string, RuleSettingOption> group in _options.GroupBy(option => option.Group))
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
                        Tag = option.Key
                    };
                    AutomationProperties.SetAutomationId(checkBox, option.Key);
                    _boxes[option.Key] = checkBox;
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
            root.Children.Add(scroll);

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
            selectionButtons.Children.Add(MakeButton("모두 선택", (_, _) => SetAll(true)));
            selectionButtons.Children.Add(MakeButton("모두 해제", (_, _) => SetAll(false), new Thickness(7, 0, 0, 0)));
            selectionButtons.Children.Add(MakeButton("기본값", (_, _) => RestoreDefaults(), new Thickness(7, 0, 0, 0)));
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

        private void SetAll(bool selected)
        {
            foreach (CheckBox box in _boxes.Values) box.IsChecked = selected;
        }

        private void RestoreDefaults()
        {
            foreach (RuleSettingOption option in _options)
            {
                _boxes[option.Key].IsChecked = option.IsDefault;
            }
        }

        private static SolidColorBrush BrushFrom(string color) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}

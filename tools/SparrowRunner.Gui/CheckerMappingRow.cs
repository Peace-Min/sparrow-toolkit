using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SparrowRunner.Gui
{
    // The old Track C master-detail 매핑 패널 (and its CheckerMappingRow model) has been removed: the main window
    // now shows only a slim summary + [체커 규칙 관리] button, and all rule CRUD / checker assignment lives in the
    // separate RuleManagerWindow (see RuleManagerWindow.xaml.cs + CheckerAssignmentRow.cs). This file is retained
    // only for the shared value converter below, which both windows use for placeholder overlays.

    /// <summary>Empty/whitespace string -> Visible (else Collapsed). Drives 에디터/입력 placeholder overlays.</summary>
    public sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

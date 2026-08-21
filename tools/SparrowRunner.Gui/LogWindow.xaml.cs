using System.Windows;

namespace SparrowRunner.Gui
{
    public partial class LogWindow : Window
    {
        public LogWindow() { InitializeComponent(); }
        public void SetLog(string text) { LogTextBox.Text = text; LogTextBox.ScrollToEnd(); }
        public void AppendLine(string line)
        {
            LogTextBox.AppendText(line + System.Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }
    }
}

using System.Windows;
using GithubWatcher.Desktop.Services;

namespace GithubWatcher.Desktop;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshLog();
    }

    public void RefreshLog()
    {
        LogTextBox.Text = AppLogger.ReadRecentLogText();
        LogTextBox.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshLog();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

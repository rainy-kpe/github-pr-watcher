using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GithubWatcher.Desktop.Models;
using GithubWatcher.Desktop.Services;

namespace GithubWatcher.Desktop;

public partial class MainWindow : Window
{
    private static readonly bool IsInDesignMode =
        (bool)DesignerProperties.IsInDesignModeProperty
            .GetMetadata(typeof(DependencyObject))
            .DefaultValue;

    public ObservableCollection<PullRequestInfo> PullRequests { get; } = [];

    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        if (IsInDesignMode)
        {
            PullRequests.Add(new PullRequestInfo
            {
                Repository = "octocat/Hello-World",
                Number = 101,
                Title = "Improve onboarding docs for first-time contributors",
                Author = "octocat",
                Url = "https://github.com/octocat/Hello-World/pull/101",
                UpdatedAt = DateTimeOffset.Now,
                CommentCount = 4,
                IsApproved = false,
            });

            PullRequests.Add(new PullRequestInfo
            {
                Repository = "dotnet/runtime",
                Number = 202,
                Title = "Refactor HTTP handler pipeline to reduce allocations",
                Author = "davidfowl",
                Url = "https://github.com/dotnet/runtime/pull/202",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-20),
                CommentCount = 12,
                IsApproved = true,
            });

            PullRequests.Add(new PullRequestInfo
            {
                Repository = "microsoft/vscode",
                Number = 303,
                Title = "Add support for compact command palette layout",
                Author = "aeschli",
                Url = "https://github.com/microsoft/vscode/pull/303",
                UpdatedAt = DateTimeOffset.Now.AddHours(-2),
                CommentCount = 2,
                IsApproved = false,
            });
        }

        Loaded += (_, _) => PositionBottomRight();
    }

    public void UpdateItems(IReadOnlyList<PullRequestInfo> items)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateItems(items));
            return;
        }

        PullRequests.Clear();
        foreach (var item in items)
        {
            PullRequests.Add(item);
        }

        AppLogger.Log($"Main window list updated with {items.Count} PRs");

        Dispatcher.BeginInvoke(PositionBottomRight, DispatcherPriority.Loaded);
    }

    public void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowFromTray);
            return;
        }

        PositionBottomRight();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        AppLogger.Log("Main window shown from tray");
    }

    public void ToggleFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ToggleFromTray);
            return;
        }

        if (IsVisible)
        {
            Hide();
            AppLogger.Log("Main window hidden from tray toggle");
            return;
        }

        ShowFromTray();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void PrList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PrList.SelectedItem is not PullRequestInfo pr)
        {
            return;
        }

        try
        {
            AppLogger.Log($"Opening PR in browser: {pr.Url}");
            Process.Start(new ProcessStartInfo
            {
                FileName = pr.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Failed to open PR URL: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, "Could not open URL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PrList.SelectedItem = null;
        }
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : MinHeight;

        Left = workArea.Right - windowWidth;
        Top = workArea.Bottom - windowHeight;
    }
}

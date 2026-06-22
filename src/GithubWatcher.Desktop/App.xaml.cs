using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using GithubWatcher.Desktop.Config;
using GithubWatcher.Desktop.Models;
using GithubWatcher.Desktop.Services;
using Microsoft.Toolkit.Uwp.Notifications;
using Forms = System.Windows.Forms;

namespace GithubWatcher.Desktop;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _cts = new();

    private AppConfig? _config;
    private GitHubService? _gitHubService;
    private MainWindow? _mainWindow;
    private LogWindow? _logWindow;
    private Forms.NotifyIcon? _notifyIcon;
    private DispatcherTimer? _pollTimer;
    private Icon? _currentIcon;
    private readonly Dictionary<string, int> _knownCommentCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset CreatedAt, DateTimeOffset? MergedAt)> _knownPrStates = new(StringComparer.OrdinalIgnoreCase);
    private UserPreferenceChangedEventHandler? _userPreferenceChangedHandler;
    private bool _pollInProgress;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogger.Log("Application startup");

        ThemeManager.ApplySystemTheme(Resources);
        _userPreferenceChangedHandler = (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                Dispatcher.Invoke(() => ThemeManager.ApplySystemTheme(Resources));
            }
        };
        SystemEvents.UserPreferenceChanged += _userPreferenceChangedHandler;

        _mainWindow = new MainWindow();
        AppLogger.Log("Main window created");

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) =>
        {
            AppLogger.Log("Tray menu: Open clicked");
            _mainWindow.ShowFromTray();
        });
        menu.Items.Add("Show log", null, (_, _) => ShowLogWindow());
        menu.Items.Add("Settings", null, (_, _) =>
        {
            AppLogger.Log("Tray menu: Settings clicked");
            ShowSettingsDialog();
        });
        menu.Items.Add("Exit", null, (_, _) =>
        {
            AppLogger.Log("Tray menu: Exit clicked");
            ExitApplication();
        });

        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "GitHub PR Watcher",
            Icon = CreateBadgeIcon(0),
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                AppLogger.Log("Tray icon left-clicked");
                _mainWindow.ToggleFromTray();
            }
        };

        _pollTimer = new DispatcherTimer();
        _pollTimer.Tick += (_, _) =>
        {
            _pollTimer?.Stop();
            _ = PollAsync();
        };

        try
        {
            ReloadConfigAndRestartPolling();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Startup config load failed: {ex.Message}");
            var saved = ShowSettingsDialog();
            if (!saved)
            {
                ExitApplication();
            }
        }
    }

    private async Task PollAsync()
    {
        if (_pollInProgress || _config is null || _gitHubService is null || _mainWindow is null)
        {
            return;
        }

        _pollInProgress = true;

        try
        {
            AppLogger.Log("Polling started");
            var prs = await _gitHubService.FetchOpenPullRequestsAsync(_config.Repositories, _cts.Token);
            AppLogger.Log($"Polling completed. Open PR count: {prs.Count}");
            UpdateTrayCount(prs.Count);
            await NotifyOnNewCommentsAsync(prs, _cts.Token);
            await NotifyOnPrStateChangeAsync(prs);
            _mainWindow.UpdateItems(prs);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Polling failed: {ex.Message}");
            AppLogger.Log($"Polling failed: {ex.Message}");
        }
        finally
        {
            _pollInProgress = false;
            ScheduleNextPoll();
        }
    }

    private void UpdateTrayCount(int count)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var icon = CreateBadgeIcon(count);
        _notifyIcon.Icon = icon;

        _currentIcon?.Dispose();
        _currentIcon = icon;

        var safeText = count > 999 ? "999+ open PRs" : $"{count} open PRs";
        _notifyIcon.Text = $"GitHub PR Watcher - {safeText}";
    }

    private bool ShowSettingsDialog()
    {
        AppLogger.Log("Opening settings dialog");
        EditableSettings settings;
        try
        {
            settings = AppConfig.LoadEditableSettings();
        }
        catch
        {
            settings = new EditableSettings();
        }

        var dialog = new SettingsWindow(settings);
        if (_mainWindow is not null && _mainWindow.IsLoaded && _mainWindow.IsVisible)
        {
            dialog.Owner = _mainWindow;
        }

        var result = dialog.ShowDialog();
        if (result != true)
        {
            AppLogger.Log("Settings dialog canceled");
            return false;
        }

        try
        {
            AppConfig.SaveEditableSettings(dialog.Settings);
            AppLogger.Log("Settings saved");
            ReloadConfigAndRestartPolling();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Settings save failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                ex.Message,
                "Settings Save Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private void ReloadConfigAndRestartPolling()
    {
        _config = AppConfig.Load();
        _knownCommentCounts.Clear();
        _knownPrStates.Clear();
        AppLogger.Log("Configuration loaded and tracking data cleared");

        _gitHubService?.Dispose();
        _gitHubService = new GitHubService(_config.GitHubToken);
        AppLogger.Log("GitHub service initialized");

        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, _config.PollIntervalSeconds));
            AppLogger.Log($"Polling timer configured ({_pollTimer.Interval.TotalSeconds}s)");
        }

        _ = PollAsync();
    }

    private void ScheduleNextPoll()
    {
        if (_pollTimer is null || _config is null || _cts.IsCancellationRequested)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, _config.PollIntervalSeconds));
        _pollTimer.Start();
        AppLogger.Log($"Next poll scheduled in {_pollTimer.Interval.TotalSeconds}s");
    }

    private async Task NotifyOnNewCommentsAsync(IReadOnlyList<PullRequestInfo> prs, CancellationToken cancellationToken)
    {
        if (_notifyIcon is null || _gitHubService is null)
        {
            return;
        }

        if (_knownCommentCounts.Count == 0)
        {
            UpdateKnownCommentCounts(prs);
            AppLogger.Log("Comment baseline initialized");
            return;
        }

        PullRequestInfo? firstUpdatedPr = null;
        var changedPrCount = 0;

        foreach (var pr in prs)
        {
            if (!_knownCommentCounts.TryGetValue(pr.Url, out var previousCount))
            {
                continue;
            }

            if (pr.CommentCount <= previousCount)
            {
                continue;
            }

            changedPrCount++;
            firstUpdatedPr ??= pr;
        }

        UpdateKnownCommentCounts(prs);

        if (firstUpdatedPr is null)
        {
            return;
        }

        var latestCommentText = await _gitHubService.FetchLatestCommentTextAsync(firstUpdatedPr.Repository, firstUpdatedPr.Number, cancellationToken);
        var body = string.IsNullOrWhiteSpace(latestCommentText)
            ? "New comment detected."
            : TruncateForNotification(latestCommentText);

        new ToastContentBuilder()
            .SetProtocolActivation(new Uri(firstUpdatedPr.Url))
            .AddText(changedPrCount == 1
                ? $"New comment on {firstUpdatedPr.RepositoryName} #{firstUpdatedPr.Number}"
                : $"New comments on {changedPrCount} PRs")
            .AddText(body)
            .Show();

        AppLogger.Log($"Notification shown for new comments on {firstUpdatedPr.Repository} #{firstUpdatedPr.Number}");
    }

    private void UpdateKnownCommentCounts(IReadOnlyList<PullRequestInfo> prs)
    {
        _knownCommentCounts.Clear();
        foreach (var pr in prs)
        {
            _knownCommentCounts[pr.Url] = pr.CommentCount;
        }
    }

    private async Task NotifyOnPrStateChangeAsync(IReadOnlyList<PullRequestInfo> openPrs)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        // Detect newly opened PRs (in openPrs but not in _knownPrStates)
        var newlyOpenedPrs = new List<PullRequestInfo>();
        foreach (var pr in openPrs)
        {
            if (!_knownPrStates.ContainsKey(pr.Url))
            {
                newlyOpenedPrs.Add(pr);
            }
        }

        // Detect merged PRs (in _knownPrStates but not in openPrs)
        var newlyMergedPrs = new List<(string Url, PullRequestInfo? Info)>();
        foreach (var knownUrl in _knownPrStates.Keys.ToList())
        {
            if (!openPrs.Any(pr => pr.Url == knownUrl))
            {
                // This PR is no longer in the open list, so it was merged/closed
                // Try to find it in the current list to get its info
                var prInfo = openPrs.FirstOrDefault(pr => pr.Url == knownUrl);
                newlyMergedPrs.Add((knownUrl, prInfo));
            }
        }

        // Update known states for currently open PRs
        foreach (var pr in openPrs)
        {
            _knownPrStates[pr.Url] = (pr.CreatedAt, pr.MergedAt);
        }

        // Show notifications
        if (newlyOpenedPrs.Count > 0)
        {
            var firstNewPr = newlyOpenedPrs[0];
            new ToastContentBuilder()
                .SetProtocolActivation(new Uri(firstNewPr.Url))
                .AddText(newlyOpenedPrs.Count == 1
                    ? $"New PR: {firstNewPr.RepositoryName} #{firstNewPr.Number}"
                    : $"{newlyOpenedPrs.Count} new PRs opened")
                .AddText($"by {firstNewPr.Author}: {firstNewPr.Title}")
                .Show();

            AppLogger.Log($"Notification shown for {newlyOpenedPrs.Count} newly opened PR(s)");
        }

        if (newlyMergedPrs.Count > 0)
        {
            var firstMergedItem = newlyMergedPrs[0];
            var displayUrl = firstMergedItem.Info?.Url ?? firstMergedItem.Url;
            var displayTitle = firstMergedItem.Info?.Title ?? "PR";
            var displayRepo = firstMergedItem.Info?.RepositoryName ?? "unknown";
            var displayNumber = firstMergedItem.Info?.Number ?? 0;

            new ToastContentBuilder()
                .SetProtocolActivation(new Uri(displayUrl))
                .AddText(newlyMergedPrs.Count == 1
                    ? $"PR merged: {displayRepo} #{displayNumber}"
                    : $"{newlyMergedPrs.Count} PRs merged")
                .AddText(displayTitle)
                .Show();

            AppLogger.Log($"Notification shown for {newlyMergedPrs.Count} merged PR(s)");

            // Remove merged PRs from tracking
            foreach (var (url, _) in newlyMergedPrs)
            {
                _knownPrStates.Remove(url);
            }
        }
    }

    private static string TruncateForNotification(string text)
    {
        const int maxLength = 220;
        var compact = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (compact.Length <= maxLength)
        {
            return compact;
        }

        return compact[..(maxLength - 3)] + "...";
    }

    private static readonly Icon AppBaseIcon = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var icoPath = Path.Combine(exeDir, "app.ico");
        if (File.Exists(icoPath))
        {
            return new Icon(icoPath, 32, 32);
        }

        return SystemIcons.Application;
    }

    private static Icon CreateBadgeIcon(int count)
    {
        using var baseBitmap = AppBaseIcon.ToBitmap();
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.DrawImage(baseBitmap, 0, 0, 32, 32);

        if (count <= 0)
        {
            var hIconBase = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(hIconBase).Clone(); }
            finally { DestroyIcon(hIconBase); }
        }

        using var background = new SolidBrush(Color.FromArgb(220, Color.FromArgb(198, 40, 40)));
        graphics.FillEllipse(background, 8, 0, 23, 23);

        var text = count > 99 ? "99+" : count.ToString();
        using var font = new Font("Segoe UI", text.Length > 2 ? 16.0f : 20.0f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var layout = new RectangleF(8, 0, 24, 24);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        graphics.DrawString(text, font, textBrush, layout, format);

        var hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private void ExitApplication()
    {
        AppLogger.Log("Application exit requested");

        if (_userPreferenceChangedHandler is not null)
        {
            SystemEvents.UserPreferenceChanged -= _userPreferenceChangedHandler;
            _userPreferenceChangedHandler = null;
        }

        _pollTimer?.Stop();
        _cts.Cancel();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _mainWindow?.CloseForExit();
        _mainWindow = null;

        _logWindow?.Close();
        _logWindow = null;

        _currentIcon?.Dispose();
        _currentIcon = null;

        _gitHubService?.Dispose();
        _gitHubService = null;

        _pollTimer = null;

        _cts.Dispose();
        AppLogger.Log("Application shutdown complete");
        Shutdown();
    }

    private void ShowLogWindow()
    {
        AppLogger.Log("Opening log window");

        if (_logWindow is null || !_logWindow.IsLoaded)
        {
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
        }

        _logWindow.RefreshLog();
        _logWindow.Show();
        _logWindow.Activate();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

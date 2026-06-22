using System.Windows;
using GithubWatcher.Desktop.Config;

namespace GithubWatcher.Desktop;

public partial class SettingsWindow : Window
{
    public EditableSettings Settings { get; }

    public SettingsWindow(EditableSettings settings)
    {
        InitializeComponent();

        Settings = new EditableSettings
        {
            GitHubClassicToken = settings.GitHubClassicToken,
            Repositories = settings.Repositories,
            PollIntervalSeconds = settings.PollIntervalSeconds,
        };

        GitHubClassicTokenTextBox.Text = Settings.GitHubClassicToken;
        PollIntervalTextBox.Text = Settings.PollIntervalSeconds.ToString();
        RepositoriesTextBox.Text = string.Join(Environment.NewLine,
            Settings.Repositories
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var classicToken = GitHubClassicTokenTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(classicToken))
        {
            System.Windows.MessageBox.Show(this, "Enter GITHUB_CLASSIC_TOKEN.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PollIntervalTextBox.Text.Trim(), out var poll) || poll < 10)
        {
            System.Windows.MessageBox.Show(this, "Poll interval must be an integer >= 10.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var repositories = RepositoriesTextBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (repositories.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Add at least one repository in owner/repo format.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var repository in repositories)
        {
            var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                System.Windows.MessageBox.Show(this, $"Invalid repository: {repository}", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Settings.GitHubClassicToken = classicToken;
        Settings.PollIntervalSeconds = poll;
        Settings.Repositories = string.Join(',', repositories);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

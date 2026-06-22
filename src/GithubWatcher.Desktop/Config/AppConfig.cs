using Microsoft.Win32;

namespace GithubWatcher.Desktop.Config;

public sealed class EditableSettings
{
    public string GitHubClassicToken { get; set; } = string.Empty;
    public string Repositories { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
}

public sealed class AppConfig
{
    private const string RegistrySubKeyPath = "Software\\GithubWatcher\\Desktop";

    public required string GitHubToken { get; init; }
    public required IReadOnlyList<string> Repositories { get; init; }
    public required int PollIntervalSeconds { get; init; }

    public static AppConfig Load()
    {
        var settings = LoadEditableSettings();

        var effectiveToken = settings.GitHubClassicToken;

        if (string.IsNullOrWhiteSpace(effectiveToken))
        {
            throw new InvalidOperationException("Missing token. Set GITHUB_CLASSIC_TOKEN.");
        }

        if (string.IsNullOrWhiteSpace(settings.Repositories))
        {
            throw new InvalidOperationException("Missing REPOSITORIES.");
        }

        var repositories = settings.Repositories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (repositories.Count == 0)
        {
            throw new InvalidOperationException("REPOSITORIES must include at least one value in owner/repo format.");
        }

        foreach (var repository in repositories)
        {
            var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Invalid repository '{repository}'. Use owner/repo format.");
            }
        }

        if (settings.PollIntervalSeconds < 10)
        {
            throw new InvalidOperationException("POLL_INTERVAL_SECONDS must be at least 10.");
        }

        return new AppConfig
        {
            GitHubToken = effectiveToken,
            Repositories = repositories,
            PollIntervalSeconds = settings.PollIntervalSeconds,
        };
    }

    public static EditableSettings LoadEditableSettings()
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(RegistrySubKeyPath, writable: false);

        static string? ReadRegistryString(RegistryKey? key, string valueName)
        {
            return key?.GetValue(valueName) as string;
        }

        string? Read(string key)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            var fromRegistry = ReadRegistryString(registryKey, key);
            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                return fromRegistry.Trim();
            }

            return null;
        }

        var settings = new EditableSettings
        {
            GitHubClassicToken = Read("GITHUB_CLASSIC_TOKEN") ?? string.Empty,
            Repositories = Read("REPOSITORIES") ?? string.Empty,
            PollIntervalSeconds = 60,
        };

        var pollIntervalRaw = Read("POLL_INTERVAL_SECONDS");
        if (!string.IsNullOrWhiteSpace(pollIntervalRaw) && int.TryParse(pollIntervalRaw, out var parsedInterval))
        {
            settings.PollIntervalSeconds = parsedInterval;
        }

        return settings;
    }

    public static void SaveEditableSettings(EditableSettings settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open registry key for writing settings.");
        }

        key.SetValue("GITHUB_CLASSIC_TOKEN", settings.GitHubClassicToken.Trim(), RegistryValueKind.String);
        key.SetValue("REPOSITORIES", settings.Repositories.Trim(), RegistryValueKind.String);
        key.SetValue("POLL_INTERVAL_SECONDS", settings.PollIntervalSeconds.ToString(), RegistryValueKind.String);
    }
}

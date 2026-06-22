using System.Globalization;
using System.IO;

namespace GithubWatcher.Desktop.Services;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GithubWatcher",
        "Desktop");

    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");

    public static void Log(string message)
    {
        var now = DateTimeOffset.Now;
        var entry = $"{now:O} | {message}";

        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);

            var lines = ReadRecentLinesUnsafe(now.AddDays(-1));
            lines.Add(entry);

            File.WriteAllLines(LogPath, lines);
        }
    }

    public static string ReadRecentLogText()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);

            var lines = ReadRecentLinesUnsafe(DateTimeOffset.Now.AddDays(-1));
            File.WriteAllLines(LogPath, lines);

            return lines.Count == 0
                ? "No log entries yet."
                : string.Join(Environment.NewLine, lines);
        }
    }

    private static List<string> ReadRecentLinesUnsafe(DateTimeOffset cutoff)
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        var lines = File.ReadAllLines(LogPath);
        var recent = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (!TryParseTimestamp(line, out var timestamp))
            {
                continue;
            }

            if (timestamp >= cutoff)
            {
                recent.Add(line);
            }
        }

        return recent;
    }

    private static bool TryParseTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var separatorIndex = line.IndexOf(" | ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var timestampText = line[..separatorIndex];
        return DateTimeOffset.TryParseExact(timestampText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
}

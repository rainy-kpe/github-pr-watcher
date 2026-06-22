# GitHub PR Watcher Desktop (.NET 10)

Windows desktop tray application that monitors open GitHub pull requests.

Features:

- Tray icon badge showing current open PR count
- Click tray icon to open a window listing open PRs
- Double-click a PR row (or use Open PR button) to open it in browser
- Polling with conditional requests (`ETag` / `If-None-Match`) for faster refresh

## Requirements

- Windows
- .NET 10 SDK
- GitHub token (fine-grained or classic)

## Setup

1. Create `.env` from example:

   ```powershell
   Copy-Item .env.example .env
   ```

2. Edit `.env`:
   - `GITHUB_TOKEN` or `GITHUB_CLASSIC_TOKEN`
   - `REPOSITORIES` as comma-separated `owner/repo`
   - optional `GITHUB_API_BASE_URL` for GitHub Enterprise Server
   - optional `POLL_INTERVAL_SECONDS` (default 60, minimum 10)

3. Run the app:

   ```powershell
   dotnet run --project src/GithubWatcher.Desktop/GithubWatcher.Desktop.csproj
   ```

## Notes

- Closing the PR window hides it to tray; use tray menu Exit to quit.
- Tray icon badge caps at `99+`.
- Token precedence: `GITHUB_TOKEN` first, then `GITHUB_CLASSIC_TOKEN`.

## Token permissions

For private repos, token needs read access to:

- Pull requests
- Issues
- Metadata

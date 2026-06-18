# GitHub PR Watcher (Windows Notifications)

Node.js app that monitors pull requests across one or more GitHub repositories and shows a Windows notification when:

- A new PR is created
- A PR is closed
- A new comment is added to a PR (issue-thread or review comment)
- A PR is approved

## Requirements

- Node.js 18+
- Windows (for native notifications)
- GitHub personal access token

## Setup

1. Install dependencies:

   ```bash
   npm install
   ```

2. Create `.env` from example:

   ```bash
   copy .env.example .env
   ```

3. Edit `.env`:
   - `GITHUB_TOKEN`: your GitHub token (fine-grained or classic)
   - `GITHUB_CLASSIC_TOKEN`: optional alias for classic PAT if you prefer not to use `GITHUB_TOKEN`
   - `GITHUB_API_BASE_URL`: set only for GitHub Enterprise Server (for example `https://github.company.com/api/v3`)
   - `REPOSITORIES`: comma-separated `owner/repo` list
   - Optional tuning:
     - `POLL_INTERVAL_SECONDS` (default 60, minimum 10)
     - `STATE_FILE` (default `./state/watcher-state.json`)
     - `NOTIFY_ON_STARTUP` (`false` by default)
     - `APP_NAME` notification app name

4. Start watcher:

   ```bash
   npm start
   ```

## Notes

- The app stores local state in `state/watcher-state.json` to avoid duplicate notifications.
- On the first run, notifications are suppressed by default for existing PRs/comments.
- If you set `NOTIFY_ON_STARTUP=true`, notifications may be shown for existing events.
- Clicking a notification opens the related PR in your default browser.
- On each polling cycle, the console logs each open PR with approval status and total comments.

## Token permissions

For private repositories, make sure token includes read access for:

- Pull requests
- Issues
- Metadata

Classic PAT guidance:

- Private repos: `repo` scope
- Public repos only: `public_repo` scope
- If organization uses SSO, authorize the token for that organization

## Troubleshooting 404 Not Found

If all repositories return `404 Not Found`, one of these is usually true:

- Repository `owner/repo` value is incorrect
- Token does not have access to the private repository
- GitHub Enterprise API URL is not configured (`GITHUB_API_BASE_URL`)

For fine-grained tokens, ensure the token is granted access to each repository listed in `REPOSITORIES`.

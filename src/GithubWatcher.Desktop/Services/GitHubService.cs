using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GithubWatcher.Desktop.Models;

namespace GithubWatcher.Desktop.Services;

public sealed class GitHubService : IDisposable
{
    private const string GitHubApiBaseUrl = "https://api.github.com";

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, (string? ETag, List<PullRequestInfo> Cached)> _repoCache =
        new(StringComparer.OrdinalIgnoreCase);

    public GitHubService(string token)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{GitHubApiBaseUrl}/"),
            Timeout = TimeSpan.FromSeconds(60),
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubWatcherDesktop/1.0");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<IReadOnlyList<PullRequestInfo>> FetchOpenPullRequestsAsync(IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        AppLogger.Log($"Fetching open PRs for {repositories.Count} repositories");
        var all = new List<PullRequestInfo>();

        foreach (var repository in repositories)
        {
            var repoPullRequests = await FetchOpenPullRequestsForRepoAsync(repository, cancellationToken);
            all.AddRange(repoPullRequests);
        }

        return all
            .OrderBy(p => p.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => p.UpdatedAt)
            .ToList();
    }

    public async Task<string?> FetchLatestCommentTextAsync(string repository, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var owner = parts[0];
        var repo = parts[1];

        var latestIssueComment = await FetchLatestIssueCommentAsync(owner, repo, pullRequestNumber, cancellationToken);
        var latestReviewComment = await FetchLatestReviewCommentAsync(owner, repo, pullRequestNumber, cancellationToken);

        var latest = latestIssueComment?.CreatedAt >= latestReviewComment?.CreatedAt
            ? latestIssueComment
            : latestReviewComment;

        if (latest is null || string.IsNullOrWhiteSpace(latest.Body))
        {
            return null;
        }

        return latest.Body.Trim();
    }

    private async Task<IReadOnlyList<PullRequestInfo>> FetchOpenPullRequestsForRepoAsync(string repository, CancellationToken cancellationToken)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var owner = parts[0];
        var repo = parts[1];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/pulls?state=open&per_page=100");
        if (_repoCache.TryGetValue(repository, out var cached) && !string.IsNullOrWhiteSpace(cached.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        using var response = await SendAsyncLogged(request, cancellationToken, $"Load open PRs for {repository}");

        if (response.StatusCode == HttpStatusCode.NotModified && _repoCache.TryGetValue(repository, out var existing))
        {
            AppLogger.Log($"Using cached PR list for {repository} ({existing.Cached.Count} PRs)");
            return existing.Cached;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            AppLogger.Log($"GitHub request failed for {repository}: {(int)response.StatusCode} {response.ReasonPhrase}");
            throw new InvalidOperationException($"GitHub request failed for {repository}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<PullRequestResponse>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        var prs = new List<PullRequestInfo>(payload.Count);
        foreach (var pr in payload)
        {
            var isApproved = await IsApprovedAsync(owner, repo, pr.Number, cancellationToken);
            var commentCount = await FetchCommentCountAsync(owner, repo, pr.Number, cancellationToken);

            prs.Add(new PullRequestInfo
            {
                Repository = repository,
                Number = pr.Number,
                Title = pr.Title ?? "(no title)",
                Author = pr.User?.Login ?? "unknown",
                Url = pr.HtmlUrl ?? $"https://github.com/{owner}/{repo}/pull/{pr.Number}",
                CreatedAt = pr.CreatedAt,
                UpdatedAt = pr.UpdatedAt,
                MergedAt = pr.MergedAt,
                CommentCount = commentCount,
                IsApproved = isApproved,
            });

            AppLogger.Log($"PR {repository} #{pr.Number} comment count: {commentCount}");
        }

        var etag = response.Headers.ETag?.Tag;
        _repoCache[repository] = (etag, prs);
        AppLogger.Log($"Fetched {prs.Count} open PRs for {repository}");

        return prs;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<bool> IsApprovedAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var response = await GetAsyncLogged($"repos/{owner}/{repo}/pulls/{number}/reviews?per_page=100", cancellationToken, $"Load reviews for {owner}/{repo}#{number}");
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<PullRequestReviewResponse>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        return payload.Any(r => string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> FetchCommentCountAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var issueCommentCount = await FetchIssueCommentCountAsync(owner, repo, number, cancellationToken);
        var reviewCommentCount = await FetchReviewCommentCountAsync(owner, repo, number, cancellationToken);
        return issueCommentCount + reviewCommentCount;
    }

    private async Task<int> FetchIssueCommentCountAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var response = await GetAsyncLogged($"repos/{owner}/{repo}/issues/{number}", cancellationToken, $"Load issue details for {owner}/{repo}#{number}");
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<IssueResponse>(stream, _jsonOptions, cancellationToken);
        return payload?.Comments ?? 0;
    }

    private async Task<int> FetchReviewCommentCountAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var response = await GetAsyncLogged($"repos/{owner}/{repo}/pulls/{number}", cancellationToken, $"Load pull request details for {owner}/{repo}#{number}");
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<PullRequestDetailsResponse>(stream, _jsonOptions, cancellationToken);
        return payload?.ReviewComments ?? 0;
    }

    private async Task<CommentResponse?> FetchLatestIssueCommentAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var response = await GetAsyncLogged($"repos/{owner}/{repo}/issues/{number}/comments?per_page=1&sort=created&direction=desc", cancellationToken, $"Load latest issue comment for {owner}/{repo}#{number}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<CommentResponse>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        return payload.FirstOrDefault();
    }

    private async Task<CommentResponse?> FetchLatestReviewCommentAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        using var response = await GetAsyncLogged($"repos/{owner}/{repo}/pulls/{number}/comments?per_page=1&sort=created&direction=desc", cancellationToken, $"Load latest review comment for {owner}/{repo}#{number}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<CommentResponse>>(stream, _jsonOptions, cancellationToken)
            ?? [];

        return payload.FirstOrDefault();
    }

    private async Task<HttpResponseMessage> GetAsyncLogged(string requestUri, CancellationToken cancellationToken, string operation)
    {
        AppLogger.Log($"HTTP GET {requestUri} ({operation})");
        var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        AppLogger.Log($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} GET {requestUri}");
        return response;
    }

    private async Task<HttpResponseMessage> SendAsyncLogged(HttpRequestMessage request, CancellationToken cancellationToken, string operation)
    {
        var requestUri = request.RequestUri?.ToString() ?? "(unknown-uri)";
        AppLogger.Log($"HTTP {request.Method} {requestUri} ({operation})");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        AppLogger.Log($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} {request.Method} {requestUri}");
        return response;
    }

    private sealed class PullRequestResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("comments")]
        public int Comments { get; init; }

        [JsonPropertyName("review_comments")]
        public int ReviewComments { get; init; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("merged_at")]
        public DateTimeOffset? MergedAt { get; init; }

        [JsonPropertyName("user")]
        public UserResponse? User { get; init; }
    }

    private sealed class UserResponse
    {
        [JsonPropertyName("login")]
        public string? Login { get; init; }
    }

    private sealed class PullRequestReviewResponse
    {
        [JsonPropertyName("state")]
        public string? State { get; init; }
    }

    private sealed class IssueResponse
    {
        [JsonPropertyName("comments")]
        public int Comments { get; init; }
    }

    private sealed class PullRequestDetailsResponse
    {
        [JsonPropertyName("review_comments")]
        public int ReviewComments { get; init; }
    }

    private sealed class CommentResponse
    {
        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }
    }
}

namespace GithubWatcher.Desktop.Models;

public sealed class PullRequestInfo
{
    public required string Repository { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset? MergedAt { get; init; }
    public required int CommentCount { get; init; }
    public required bool IsApproved { get; init; }

    public string RepositoryName
    {
        get
        {
            var slashIndex = Repository.IndexOf('/');
            if (slashIndex < 0 || slashIndex == Repository.Length - 1)
            {
                return Repository;
            }

            return Repository[(slashIndex + 1)..];
        }
    }
}

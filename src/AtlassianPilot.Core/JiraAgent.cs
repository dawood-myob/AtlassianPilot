namespace AtlassianPilot.Core;

public sealed record JiraIssueSummary(
    string Key,
    string Summary,
    string Status,
    string Assignee,
    string Priority,
    string IssueType,
    Uri Url);

public sealed record JiraIssueSearchResult(
    IReadOnlyList<JiraIssueSummary> Issues,
    bool HasMore,
    string? NextPageToken,
    int? Total,
    int PageSize = 50);

public sealed record JiraAgentProgress(string Message, JiraIssueSearchResult? SearchResult = null, string? Jql = null);

public sealed record JiraAgentReply(string Content, JiraIssueSearchResult? SearchResult, string? Jql);

/// <summary>Executes bounded, read-only Jira issue searches using the saved Jira connection.</summary>
public interface IJiraIssueSearchService
{
    Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Maintains a Copilot conversation with access only to read-only Jira issue search.</summary>
public interface IJiraAgent : IAsyncDisposable
{
    Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task NewConversationAsync(CancellationToken cancellationToken = default);
}

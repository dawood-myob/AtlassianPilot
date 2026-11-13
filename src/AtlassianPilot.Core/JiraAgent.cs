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

public sealed record JiraIssueDetails(string Key, Uri Url, string FieldsJson, string Summary = "", string Status = "Unknown",
    string Assignee = "Unassigned", string Priority = "Unspecified", string IssueType = "Unknown", string Description = "");

public sealed record JiraIssueDraft(string ProjectKey, string IssueType, string Summary, string Description, string? ParentKey);

public sealed record JiraCreateField(string Id, string Name, string SchemaType, bool Required, bool HasDefault);
public sealed record JiraCreateMetadata(string ProjectKey, string IssueType, string IssueTypeId, IReadOnlyList<JiraCreateField> Fields);

public interface IJiraCreateMetadataService
{
    Task<JiraCreateMetadata> GetAsync(string projectKey, string issueType, CancellationToken cancellationToken = default);
}

public sealed record JiraAgentProgress(string Message, JiraIssueSearchResult? SearchResult = null, string? Jql = null);

public sealed record JiraAgentReply(string Content, JiraIssueSearchResult? SearchResult, string? Jql, JiraIssueDetails? IssueDetails = null, JiraIssueDraft? Draft = null);

/// <summary>Retrieves a single issue without modifying Jira.</summary>
public interface IJiraIssueDetailsService
{
    Task<JiraIssueDetails> GetAsync(string issueKey, CancellationToken cancellationToken = default);
}

/// <summary>Executes bounded, read-only Jira issue searches using the saved Jira connection.</summary>
public interface IJiraIssueSearchService
{
    Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Maintains a Copilot conversation with access only to read-only Jira issue tools.</summary>
public interface IJiraAgent : IAsyncDisposable
{
    Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task NewConversationAsync(CancellationToken cancellationToken = default);
}

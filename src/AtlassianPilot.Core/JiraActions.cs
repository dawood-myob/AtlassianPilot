namespace AtlassianPilot.Core;

public sealed record JiraActionPreview(Guid Id, string Action, string IssueKey, string Summary, string PayloadJson,
    DateTimeOffset ExpiresAt);
public sealed record JiraWorkflowTransition(string Id, string Name);
public sealed record JiraIssueLinkType(string Name, string Inward, string Outward);
public sealed record JiraActionResult(string Message, string? IssueKey = null, Uri? IssueUrl = null);

/// <summary>Prepares exact Jira write payloads and submits only an explicitly approved, single-use preview.</summary>
public interface IJiraIssueActionService
{
    Task<IReadOnlyList<JiraWorkflowTransition>> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JiraIssueLinkType>> GetLinkTypesAsync(CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareUpdateAsync(string issueKey, string? summary, string? description, CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareCommentAsync(string issueKey, string comment, CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareAssignmentAsync(string issueKey, string accountId, CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareTransitionAsync(string issueKey, string transitionId, CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareLinkAsync(string issueKey, string linkedIssueKey, string linkType, CancellationToken cancellationToken = default);
    Task<JiraActionPreview> PrepareCloneAsync(string sourceIssueKey, string projectKey, string issueType, CancellationToken cancellationToken = default);
    Task<JiraActionResult> ExecuteApprovedAsync(Guid previewId, CancellationToken cancellationToken = default);
    void DiscardPreview(Guid previewId);
}

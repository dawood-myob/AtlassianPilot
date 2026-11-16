namespace AtlassianPilot.Core;

public sealed record JiraIssueType(string Id, string Name);
public sealed record JiraDraftContent(string Summary, string Description);

public interface IJiraDraftAgent
{
    Task<JiraDraftContent> RefineAsync(string instructions, JiraDraftContent? currentDraft, CancellationToken cancellationToken = default);
}

public sealed record JiraCreationTemplate(JiraCreateMetadata Metadata, string InitialFieldsJson, string ReferenceNotice);
public sealed record JiraCreationPreview(Guid Id, string SiteUrl, string Summary, string FieldsJson);
public sealed record JiraCreatedIssue(string Key, Uri Url);

/// <summary>Preparation is read-only. Only explicitly approved, single-use previews can be submitted.</summary>
public interface IJiraIssueCreationService
{
    Task<IReadOnlyList<JiraIssueType>> GetIssueTypesAsync(string projectKey, CancellationToken cancellationToken = default);
    Task<JiraCreationTemplate> LoadTemplateAsync(string projectKey, string issueType, string? referenceKey, CancellationToken cancellationToken = default);
    Task<JiraCreationPreview> PrepareAsync(JiraIssueDraft draft, string additionalFieldsJson, CancellationToken cancellationToken = default);
    Task<JiraCreatedIssue> CreateApprovedAsync(Guid previewId, CancellationToken cancellationToken = default);
    void DiscardPreview(Guid previewId);
}

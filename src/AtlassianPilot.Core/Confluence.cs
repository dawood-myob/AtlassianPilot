namespace AtlassianPilot.Core;

public sealed record ConfluencePageSummary(string Id, string Title, string SpaceKey, string Excerpt, Uri Url);
public sealed record ConfluencePage(string Id, string Title, string SpaceKey, string StorageContent, int Version, Uri Url);
public sealed record ConfluencePageDraft(string SpaceKey, string Title, string Content, string? PageId = null);
public sealed record ConfluencePagePreview(Guid Id, string SiteUrl, string Action, string Title, string SpaceKey, string ContentPreview, string PayloadJson);
public sealed record ConfluencePageSaved(string Id, string Title, string SpaceKey, Uri Url, bool Updated);
public sealed record ConfluenceAgentProgress(string Message);
public sealed record ConfluenceAgentReply(string Content, IReadOnlyList<ConfluencePageSummary> Pages, ConfluencePage? Page, ConfluencePageDraft? Draft,
    string? SearchQuery = null, string? SearchSpace = null, bool HasMore = false, string? NextStart = null);

public interface IConfluenceWorkspaceService
{
    Task<(IReadOnlyList<ConfluencePageSummary> Pages, bool HasMore, string? NextStart)> SearchAsync(string query, string? spaceKey = null, int limit = 20, string? start = null, CancellationToken cancellationToken = default);
    Task<ConfluencePage> GetAsync(string pageId, CancellationToken cancellationToken = default);
    Task<ConfluencePagePreview> PrepareAsync(ConfluencePageDraft draft, CancellationToken cancellationToken = default);
    Task<ConfluencePageSaved> SaveApprovedAsync(Guid previewId, CancellationToken cancellationToken = default);
    void DiscardPreview(Guid previewId);
}

public interface IConfluenceAgent : IAsyncDisposable
{
    Task<ConfluenceAgentReply> SendAsync(string prompt, IProgress<ConfluenceAgentProgress>? progress = null, CancellationToken cancellationToken = default);
    Task NewConversationAsync(CancellationToken cancellationToken = default);
}

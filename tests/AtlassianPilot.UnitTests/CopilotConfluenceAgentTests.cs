using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class CopilotConfluenceAgentTests
{
    [Fact]
    public async Task UnauthenticatedRuntimeCannotCreateConfluenceAgentSession()
    {
        var runtime = new FakeCopilotRuntime { Auth = new(false, null) };
        await using var agent = new CopilotConfluenceAgent(runtime, new NeverWorkspace(), new Settings());
        var error = await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Find alert pages"));
        Assert.Contains("Sign in", error.Message);
        Assert.Null(runtime.AgentTools);
    }

    [Fact]
    public async Task AgentRegistersOnlySearchReadAndLocalProposalTools()
    {
        var runtime = new FakeCopilotRuntime { Models = [new("chosen-model", "Chosen model")] };
        await using var agent = new CopilotConfluenceAgent(runtime, new NeverWorkspace(), new Settings("chosen-model"));
        await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Search the runbooks"));
        var tools = runtime.AgentTools!;
        Assert.Equal(3, tools.Count);
        var search = tools.Single(tool => tool.Name == "search_confluence_pages");
        var searchProperties = search.JsonSchema.GetProperty("properties");
        Assert.True(searchProperties.TryGetProperty("query", out _));
        Assert.True(searchProperties.TryGetProperty("spaceKey", out _));
        Assert.True(searchProperties.TryGetProperty("limit", out _));
        Assert.True(searchProperties.TryGetProperty("start", out _));
        var page = tools.Single(tool => tool.Name == "get_confluence_page");
        Assert.Contains("Read-only", page.Description!);
        Assert.True(page.JsonSchema.GetProperty("properties").TryGetProperty("pageId", out _));
        var draft = tools.Single(tool => tool.Name == "propose_confluence_page");
        Assert.Contains("Cannot create or update", draft.Description!);
        Assert.True(draft.JsonSchema.GetProperty("properties").TryGetProperty("spaceKey", out _));
        Assert.True(draft.JsonSchema.GetProperty("properties").TryGetProperty("pageId", out _));
        Assert.Equal("chosen-model", runtime.AgentModel);
        Assert.Contains("untrusted data", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title-only changes", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("always call propose_confluence_page", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicitly approve", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Required for every requested page change", draft.Description!);
    }

    [Fact]
    public async Task UnavailableModelIsRejectedBeforeCreatingSession()
    {
        var runtime = new FakeCopilotRuntime();
        await using var agent = new CopilotConfluenceAgent(runtime, new NeverWorkspace(), new Settings("missing-model"));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Search for runbooks"));
        Assert.Contains("not currently available", error.Message);
        Assert.Null(runtime.AgentTools);
    }

    private sealed class NeverWorkspace : IConfluenceWorkspaceService
    {
        public Task<(IReadOnlyList<ConfluencePageSummary> Pages, bool HasMore, string? NextStart)> SearchAsync(string query, string? spaceKey = null, int limit = 20, string? start = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected API call");
        public Task<ConfluencePage> GetAsync(string pageId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected API call");
        public Task<ConfluencePagePreview> PrepareAsync(ConfluencePageDraft draft, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected API call");
        public Task<ConfluencePageSaved> SaveApprovedAsync(Guid previewId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected API call");
        public void DiscardPreview(Guid previewId) => throw new InvalidOperationException("Unexpected API call");
    }

    private sealed class Settings(string model = "") : ISettingsStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSettings(kind, Model: model));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

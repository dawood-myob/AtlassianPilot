using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class CopilotJiraAgentTests
{
    [Fact]
    public async Task UnauthenticatedRuntimeCannotCreateAgentSessionOrSearch()
    {
        var runtime = new FakeCopilotRuntime { Auth = new(false, null) };
        var search = new NeverSearch();
        await using var agent = new CopilotJiraAgent(runtime, search, new NeverDetails(), new NeverMetadata(), new Settings());
        var error = await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Find my issues"));
        Assert.Contains("Sign in", error.Message);
        Assert.Null(runtime.AgentTools);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task AgentRegistersReadOnlyToolsWithTypedArgumentsAndSelectedModel()
    {
        var runtime = new FakeCopilotRuntime();
        var search = new NeverSearch();
        await using var agent = new CopilotJiraAgent(runtime, search, new NeverDetails(), new NeverMetadata(), new Settings("test-model"));
        await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Find my issues"));
        Assert.Equal(4, runtime.AgentTools!.Count);
        var tool = runtime.AgentTools.Single(candidate => candidate.Name == "search_jira_issues");
        Assert.Equal("search_jira_issues", tool.Name);
        Assert.Contains("read-only", tool.Description!, StringComparison.OrdinalIgnoreCase);
        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("jql", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.True(properties.TryGetProperty("nextPageToken", out _));
        Assert.False(properties.TryGetProperty("invocation", out _));
        var details = runtime.AgentTools.Single(candidate => candidate.Name == "get_jira_issue_details");
        Assert.True(details.JsonSchema.GetProperty("properties").TryGetProperty("issueKey", out _));
        Assert.False(details.JsonSchema.GetProperty("properties").TryGetProperty("invocation", out _));
        var draft = runtime.AgentTools.Single(candidate => candidate.Name == "draft_jira_issue");
        Assert.Contains("Does not access or modify Jira", draft.Description!);
        Assert.True(draft.JsonSchema.GetProperty("properties").TryGetProperty("projectKey", out _));
        Assert.False(draft.JsonSchema.GetProperty("properties").TryGetProperty("invocation", out _));
        var metadata = runtime.AgentTools.Single(candidate => candidate.Name == "get_jira_create_fields");
        Assert.True(metadata.JsonSchema.GetProperty("properties").TryGetProperty("projectKey", out _));
        Assert.True(metadata.JsonSchema.GetProperty("properties").TryGetProperty("issueType", out _));
        Assert.Equal("test-model", runtime.AgentModel);
        Assert.Contains("Never invent", runtime.AgentSystemMessage!);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task UnavailableSavedModelCannotCreateSession()
    {
        var runtime = new FakeCopilotRuntime();
        await using var agent = new CopilotJiraAgent(runtime, new NeverSearch(), new NeverDetails(), new NeverMetadata(), new Settings("missing-model"));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => agent.SendAsync("Find my issues"));
        Assert.Contains("not currently available", error.Message);
        Assert.Null(runtime.AgentTools);
    }

    private sealed class NeverMetadata : IJiraCreateMetadataService
    {
        public Task<JiraCreateMetadata> GetAsync(string projectKey, string issueType, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected metadata retrieval");
    }

    private sealed class NeverDetails : IJiraIssueDetailsService
    {
        public Task<JiraIssueDetails> GetAsync(string issueKey, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected detail retrieval");
    }

    private sealed class NeverSearch : IJiraIssueSearchService
    {
        public int Calls { get; private set; }
        public Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected search");
        }
    }

    private sealed class Settings(string model = "") : ISettingsStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationSettings(kind, Model: model));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class CopilotJiraDraftAgentTests
{
    [Fact]
    public async Task RefinementRegistersOnlyAContentProposalToolWithNoJiraWriteTools()
    {
        var runtime = new FakeCopilotRuntime();
        var agent = new CopilotJiraDraftAgent(runtime, new Settings());
        await Assert.ThrowsAsync<IntegrationException>(() => agent.RefineAsync("Rough notes", null));
        var tool = Assert.Single(runtime.AgentTools!);
        Assert.Equal("propose_jira_content", tool.Name);
        Assert.Equal(new[] { "summary", "description" }, tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Contains("Correct grammar", runtime.AgentSystemMessage!);
        Assert.Contains("Do not invent", runtime.AgentSystemMessage!);
        Assert.Contains("cannot access Jira", runtime.AgentSystemMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedRuntimeCannotRefineAndDoesNotCreateASession()
    {
        var runtime = new FakeCopilotRuntime { Auth = new(false, null) };
        var error = await Assert.ThrowsAsync<IntegrationException>(() => new CopilotJiraDraftAgent(runtime, new Settings()).RefineAsync("Notes", null));
        Assert.Contains("Sign in", error.Message);
        Assert.Null(runtime.AgentTools);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16001)]
    public async Task InvalidInputCannotReachCopilot(int length)
    {
        var runtime = new FakeCopilotRuntime();
        await Assert.ThrowsAsync<IntegrationException>(() => new CopilotJiraDraftAgent(runtime, new Settings()).RefineAsync(new string('x', length), null));
        Assert.Null(runtime.AgentTools);
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSettings(kind));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

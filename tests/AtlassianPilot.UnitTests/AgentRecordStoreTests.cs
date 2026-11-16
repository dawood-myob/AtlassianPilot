using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class AgentRecordStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AtlassianPilot-records-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAgentRecordStore records;
    private readonly SqliteSettingsStore settings;

    public AgentRecordStoreTests()
    {
        var database = Path.Combine(directory, "workspace.db");
        settings = new(database, new DpapiSecretProtector());
        records = new(database);
    }

    [Fact]
    public async Task ConversationRoundTripsByAgentAndCanBeDeleted()
    {
        await settings.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var jira = new AgentConversation(Guid.NewGuid().ToString("N"), IntegrationKind.Jira, "Find my issues", now, now,
            [new("You", "Find my open issues", now), new("Jira Agent", "Two results", now.AddSeconds(1))]);
        var confluence = new AgentConversation(Guid.NewGuid().ToString("N"), IntegrationKind.Confluence, "Alert runbook", now, now,
            [new("You", "Find alert runbooks", now)]);
        await records.SaveAsync(jira);
        await records.SaveAsync(confluence);
        var all = await records.ListConversationsAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Find my issues", all.Single(item => item.Agent == IntegrationKind.Jira).Title);
        Assert.Equal(2, (await records.ListConversationsAsync(IntegrationKind.Jira)).Single().Messages.Count);
        Assert.Single(await records.ListConversationsAsync(IntegrationKind.Confluence));
        await records.DeleteAsync(jira.Id);
        Assert.Single(await records.ListConversationsAsync());
    }

    [Fact]
    public async Task ExpiredConversationsAreNotReturnedAndInvalidConversationCannotBeStored()
    {
        await settings.InitializeAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-31);
        await records.SaveAsync(new(Guid.NewGuid().ToString("N"), IntegrationKind.Jira, "Old", old, old, [new("You", "old content", old)]));
        Assert.Empty(await records.ListConversationsAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => records.SaveAsync(new("not-a-guid", IntegrationKind.Jira, "Invalid", old, old, [new("You", "text", old)])));
    }

    [Fact]
    public async Task ActivityLogFiltersByAgentAndLiteralTextAndClearsWithoutRecordingPrompts()
    {
        await settings.InitializeAsync();
        await records.RecordAsync(IntegrationKind.Jira, "Search issues", "Completed");
        await records.RecordAsync(IntegrationKind.Confluence, "Read page", "Failed");
        Assert.Equal(2, (await records.ListActivitiesAsync()).Count);
        var jira = Assert.Single(await records.ListActivitiesAsync(IntegrationKind.Jira));
        Assert.Equal("Search issues", jira.Action);
        Assert.Equal("Failed", Assert.Single(await records.ListActivitiesAsync(filter: "failed")).Outcome);
        Assert.Empty(await records.ListActivitiesAsync(filter: "%"));
        await Assert.ThrowsAsync<ArgumentException>(() => records.RecordAsync(IntegrationKind.Jira, "Search issues", "Prompt: private text"));
        await records.ClearAsync();
        Assert.Empty(await records.ListActivitiesAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

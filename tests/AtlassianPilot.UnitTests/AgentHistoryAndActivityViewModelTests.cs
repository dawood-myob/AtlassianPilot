using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.UnitTests;

public sealed class AgentHistoryAndActivityViewModelTests
{
    [Fact]
    public async Task DeleteAllHistoryWorksWhenSelectedFilterHasNoConversations()
    {
        var store = new ConversationHistory();
        var viewModel = new AgentHistoryViewModel(store);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.HasSelectedConversation);
        viewModel.SelectedAgent = "Jira";
        Assert.Contains(nameof(viewModel.HasSelectedConversation), changedProperties);
        Assert.False(viewModel.HasSelectedConversation);
        Assert.Empty(viewModel.Conversations);
        viewModel.ConfirmDeleteAll += () => true;

        await viewModel.DeleteAllAsync();

        Assert.Equal(1, store.DeleteAllCalls);
    }

    [Fact]
    public async Task ClearActivityWorksWhenCurrentFilterHasNoEntries()
    {
        var store = new ActivityLog();
        var viewModel = new ActivityLogViewModel(store) { Filter = "no matching entry" };
        await viewModel.InitializeAsync();
        Assert.Empty(viewModel.Entries);
        viewModel.ConfirmClear += () => true;

        await viewModel.ClearAsync();

        Assert.Equal(1, store.ClearCalls);
    }

    private sealed class ConversationHistory : IConversationHistoryStore
    {
        private readonly AgentConversation existing = new("confluence-1", IntegrationKind.Confluence, "Runbook",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        public int DeleteAllCalls { get; private set; }
        public Task SaveAsync(AgentConversation conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentConversation>> ListConversationsAsync(IntegrationKind? agent = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConversation>>(agent is null || existing.Agent == agent ? [existing] : []);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) { DeleteAllCalls++; return Task.CompletedTask; }
    }

    private sealed class ActivityLog : IActivityLogStore
    {
        public int ClearCalls { get; private set; }
        public Task RecordAsync(IntegrationKind agent, string action, string outcome, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentActivity>> ListActivitiesAsync(IntegrationKind? agent = null, string? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentActivity>>(new[] { new AgentActivity(1, DateTimeOffset.UtcNow, IntegrationKind.Confluence, "Search pages", "Completed") }
                .Where(entry => (agent is null || entry.Agent == agent) && (string.IsNullOrWhiteSpace(filter) || entry.Action.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToArray());
        public Task ClearAsync(CancellationToken cancellationToken = default) { ClearCalls++; return Task.CompletedTask; }
    }
}

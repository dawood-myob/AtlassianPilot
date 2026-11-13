using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.UnitTests;

public sealed class JiraAgentViewModelTests
{
    [Fact]
    public async Task SearchAndLoadMoreUseActualPagesAndQuery()
    {
        var first = Issue("ABC-1");
        var second = Issue("ABC-2");
        var agent = new FakeAgent { Reply = new("Jira returned 1 issue(s) on this page.", new([first], true, "next", null, 1), "project = ABC") };
        var search = new FakeSearch { Result = new([second], false, null, null) };
        var viewModel = new JiraAgentViewModel(agent, search, new FakeSavedSearchStore()) { Prompt = "Find ABC issues" };

        await viewModel.SendAsync();
        Assert.Equal("Find ABC issues", agent.Prompt);
        Assert.Equal("JQL: project = ABC", viewModel.Query);
        Assert.Equal("ABC-1", Assert.Single(viewModel.Issues).Key);
        Assert.True(viewModel.CanLoadMore);
        await viewModel.LoadMoreAsync();
        Assert.Equal("project = ABC", search.Query);
        Assert.Equal("next", search.Token);
        Assert.Equal(1, search.PageSize);
        Assert.Equal(new[] { "ABC-1", "ABC-2" }, viewModel.Issues.Select(issue => issue.Key));
        Assert.False(viewModel.CanLoadMore);
        Assert.Contains("All available pages", viewModel.Status);
    }

    [Fact]
    public async Task FailureDoesNotLeaveOldResultsOrReportSuccess()
    {
        var agent = new FakeAgent { Reply = new("one result", new([Issue("ABC-1")], false, null, null), "project = ABC") };
        var viewModel = new JiraAgentViewModel(agent, new FakeSearch(), new FakeSavedSearchStore()) { Prompt = "first" };
        await viewModel.SendAsync();
        agent.Error = new IntegrationException("Jira denied this search.");
        viewModel.Prompt = "second";
        await viewModel.SendAsync();
        Assert.Empty(viewModel.Issues);
        Assert.Equal("No search has been run.", viewModel.Query);
        Assert.Equal("Jira denied this search.", viewModel.Status);
        Assert.False(viewModel.Busy);
    }

    [Fact]
    public async Task DraftIsDisplayedAsPreviewAndClearedOnNewConversation()
    {
        var draft = new JiraIssueDraft("ABC", "Story", "Example", "Requirements", "ABC-123");
        var agent = new FakeAgent { Reply = new("Draft prepared", null, null, Draft: draft) };
        var viewModel = new JiraAgentViewModel(agent, new FakeSearch(), new FakeSavedSearchStore()) { Prompt = "Prepare a story" };
        await viewModel.SendAsync();
        Assert.Equal(draft, viewModel.Draft);
        Assert.True(viewModel.HasDraft);
        Assert.Contains("no Jira issue was created", viewModel.Status);
        await viewModel.NewConversationAsync();
        Assert.Null(viewModel.Draft);
        Assert.False(viewModel.HasDraft);
    }

    [Fact]
    public async Task DetailsAreDisplayedAndClearedOnTheNextRequest()
    {
        var details = new JiraIssueDetails("ABC-1", new Uri("https://site.atlassian.net/browse/ABC-1"), "{\"summary\":\"Example\"}");
        var agent = new FakeAgent { Reply = new("Details returned", null, null, details) };
        var viewModel = new JiraAgentViewModel(agent, new FakeSearch(), new FakeSavedSearchStore()) { Prompt = "Show ABC-1" };
        await viewModel.SendAsync();
        Assert.Equal(details, viewModel.IssueDetails);
        Assert.True(viewModel.HasIssueDetails);
        Assert.Contains("paginated", viewModel.Status);
        agent.Error = new IntegrationException("Issue unavailable.");
        viewModel.Prompt = "Show ABC-2";
        await viewModel.SendAsync();
        Assert.Null(viewModel.IssueDetails);
        Assert.False(viewModel.HasIssueDetails);
        Assert.Equal("Issue unavailable.", viewModel.Status);
    }

    [Fact]
    public async Task NewConversationClearsVisibleSession()
    {
        var agent = new FakeAgent { Reply = new("one result", new([Issue("ABC-1")], false, null, null), "project = ABC") };
        var viewModel = new JiraAgentViewModel(agent, new FakeSearch(), new FakeSavedSearchStore()) { Prompt = "first" };
        await viewModel.SendAsync();
        await viewModel.NewConversationAsync();
        Assert.True(agent.Reset);
        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.Issues);
        Assert.Equal("New conversation started.", viewModel.Status);
    }

    [Fact]
    public async Task ExamplesAreEditableAndNeverExecuteAutomatically()
    {
        var agent = new FakeAgent();
        var viewModel = new JiraAgentViewModel(agent, new FakeSearch(), new FakeSavedSearchStore()) { Prompt = "existing draft" };
        await viewModel.InitializeAsync();
        viewModel.SelectedSearch = viewModel.Searches.Single(choice => choice.Name.Contains("Bugs in a project"));
        Assert.Equal("existing draft", viewModel.Prompt);
        viewModel.UseSelectedSearch();
        Assert.Contains("[project key]", viewModel.Prompt);
        Assert.Null(agent.Prompt);
        Assert.False(viewModel.HasSelectedSavedSearch);
    }

    [Fact]
    public async Task SaveRenameUpdateReloadAndDeletePreserveUserChoices()
    {
        var store = new FakeSavedSearchStore();
        var viewModel = new JiraAgentViewModel(new FakeAgent(), new FakeSearch(), store) { Prompt = "Find bugs in ABC", SearchName = "ABC bugs" };
        await viewModel.InitializeAsync();
        await viewModel.SaveSearchAsync(false);
        Assert.Single(store.Items);
        var id = store.Items[0].Id;
        viewModel.SearchName = "Renamed";
        viewModel.Prompt = "Find open bugs in ABC";
        await viewModel.SaveSearchAsync(true);
        Assert.Equal(id, Assert.Single(store.Items).Id);
        var reloaded = new JiraAgentViewModel(new FakeAgent(), new FakeSearch(), store);
        await reloaded.InitializeAsync();
        reloaded.SelectedSearch = reloaded.Searches.Single(choice => choice.Name == "Renamed");
        reloaded.UseSelectedSearch();
        Assert.Equal("Find open bugs in ABC", reloaded.Prompt);
        await reloaded.DeleteSearchAsync();
        Assert.Empty(store.Items);
        Assert.Equal("Find open bugs in ABC", reloaded.Prompt);
        Assert.Equal(5, reloaded.Searches.Count);
    }

    [Fact]
    public async Task DuplicateNamesCannotOverwriteAnExistingSearch()
    {
        var store = new FakeSavedSearchStore();
        var viewModel = new JiraAgentViewModel(new FakeAgent(), new FakeSearch(), store) { Prompt = "First", SearchName = "My search" };
        await viewModel.InitializeAsync();
        await viewModel.SaveSearchAsync(false);
        viewModel.Prompt = "Different";
        viewModel.SearchName = "my SEARCH";
        await viewModel.SaveSearchAsync(false);
        Assert.Equal("First", Assert.Single(store.Items).Request);
        Assert.Contains("already", viewModel.Status);
    }

    [Fact]
    public async Task StorageFailureLeavesSavedSearchAndDraftUnchanged()
    {
        var store = new FakeSavedSearchStore();
        var viewModel = new JiraAgentViewModel(new FakeAgent(), new FakeSearch(), store) { Prompt = "Original", SearchName = "Original name" };
        await viewModel.InitializeAsync();
        await viewModel.SaveSearchAsync(false);
        store.Error = new InvalidOperationException("private error");
        viewModel.Prompt = "Changed draft";
        viewModel.SearchName = "Changed name";
        await viewModel.SaveSearchAsync(true);
        Assert.Equal("Original", Assert.Single(store.Items).Request);
        Assert.Equal("Original name", viewModel.SelectedSearch!.Name);
        Assert.Equal("Changed draft", viewModel.Prompt);
        Assert.Contains("Could not save", viewModel.Status);
        Assert.False(viewModel.Busy);
    }

    private static JiraIssueSummary Issue(string key) => new(key, "Summary", "Open", "User", "High", "Story", new Uri("https://site.atlassian.net/browse/" + key));

    private sealed class FakeAgent : IJiraAgent
    {
        public JiraAgentReply Reply { get; set; } = new("No search", null, null);
        public Exception? Error { get; set; }
        public string? Prompt { get; private set; }
        public bool Reset { get; private set; }
        public Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            if (Error is not null) throw Error;
            return Task.FromResult(Reply);
        }
        public Task NewConversationAsync(CancellationToken cancellationToken = default) { Reset = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSavedSearchStore : ISavedJiraSearchStore
    {
        public List<SavedJiraSearch> Items { get; set; } = [];
        public Exception? Error { get; set; }
        public Task<IReadOnlyList<SavedJiraSearch>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SavedJiraSearch>>(Items.ToList());
        public Task SaveAsync(IReadOnlyList<SavedJiraSearch> searches, CancellationToken cancellationToken = default)
        {
            if (Error is not null) throw Error;
            Items = searches.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSearch : IJiraIssueSearchService
    {
        public JiraIssueSearchResult Result { get; set; } = new([], false, null, null);
        public string? Query { get; private set; }
        public string? Token { get; private set; }
        public int PageSize { get; private set; }
        public Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null, CancellationToken cancellationToken = default)
        {
            Query = jql;
            Token = nextPageToken;
            PageSize = maxResults;
            return Task.FromResult(Result);
        }
    }
}

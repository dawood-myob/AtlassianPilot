using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.UnitTests;

public sealed class ConfluenceAgentViewModelTests
{
    [Fact]
    public async Task NaturalLanguageSearchShowsOnlyReturnedPagesAndCopilotStatus()
    {
        var agent = new FakeAgent { Reply = new("Two matching pages were found.", [Page("101", "Alert runbook"), Page("102", "On-call process")], null, null, "alerts", "ENG", true, "20") };
        var model = Create(agent, new FakeWorkspace());
        var changedProperties = new List<string?>();
        model.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        model.Prompt = "Find pages about alerts";
        await model.SendAsync();
        Assert.Contains(nameof(model.HasPages), changedProperties);
        Assert.True(model.HasPages);
        Assert.Equal(2, model.Pages.Count);
        Assert.Equal("101", model.Pages[0].Id);
        Assert.True(model.CanLoadMore);
        Assert.Equal("", model.Prompt);
        Assert.Equal(2, model.Messages.Count);
        Assert.Contains("search complete", model.Status);
        model.ConfirmDiscardConversation += () => true;
        await model.NewConversationAsync();
        Assert.Empty(model.Pages);
        Assert.False(model.HasPages);
    }

    [Fact]
    public async Task PageFetchedForAnalysisIsShownAsResultAndCanBeLoadedForEditing()
    {
        var page = new ConfluencePage("123456", "Tycho - Application Guide, Releases and Roadmap", "MSS",
            "<p>Completed project with no future plans.</p>", 7,
            new Uri("https://site.atlassian.net/wiki/pages/viewpage.action?pageId=123456"));
        var agent = new FakeAgent { Reply = new("The page is ready for review.", [], page, null) };
        var workspace = new FakeWorkspace { PageToLoad = page };
        var model = Create(agent, workspace);
        model.Prompt = "Remove roadmap from the title";

        await model.SendAsync();

        var result = Assert.Single(model.Pages);
        Assert.Equal(page.Id, result.Id);
        Assert.Equal(page.Title, result.Title);
        Assert.True(model.HasPages);
        Assert.Contains("Load page for editing", model.Messages[^1].Text);
        Assert.Equal(0, workspace.Reads);

        await model.LoadPageAsync(result.Id);

        Assert.Equal("123456", model.PageId);
        Assert.Equal(page.Title, model.Title);
        Assert.Equal("Completed project with no future plans.", model.Content);
        Assert.Equal(1, workspace.Reads);
    }

    [Fact]
    public async Task DecliningNewConversationKeepsTheCurrentDraftAndMessages()
    {
        var model = Ready(new FakeWorkspace());
        model.Prompt = "Unsaved notes";
        model.Messages.Add(new("You", "Earlier request"));
        model.ConfirmDiscardConversation += () => false;
        await model.NewConversationAsync();
        Assert.Equal("Unsaved notes", model.Prompt);
        Assert.Equal("Earlier request", Assert.Single(model.Messages).Text);
    }

    [Fact]
    public async Task ChatUpdateProposalPopulatesPageIdAndRevisedContentForApprovedUpdate()
    {
        var agent = new FakeAgent { Reply = new("Update proposal ready.", [], null,
            new("MSS", "Tycho - Application Guide, Releases", "Completed project with no future plans.", "123456")) };
        var workspace = new FakeWorkspace();
        var model = Create(agent, workspace);
        model.Prompt = "Remove roadmap from the title";

        await model.SendAsync();

        Assert.Equal("MSS", model.SpaceKey);
        Assert.Equal("123456", model.PageId);
        Assert.Equal("Tycho - Application Guide, Releases", model.Title);
        Assert.Equal("Completed project with no future plans.", model.Content);
        await model.PrepareSaveAsync();
        Assert.Equal("Update", model.Preview!.Action);
        Assert.Equal(0, workspace.Saves);
    }

    [Fact]
    public async Task StartingNewConversationClearsChatButKeepsIndependentPageDraft()
    {
        var model = Ready(new FakeWorkspace());
        model.Title = "Draft title";
        model.Content = "Draft content";
        model.Prompt = "Old prompt";
        model.Messages.Add(new("You", "Earlier request"));
        model.ConfirmDiscardConversation += () => true;

        await model.NewConversationAsync();

        Assert.Empty(model.Messages);
        Assert.Equal("", model.Prompt);
        Assert.Equal("Draft title", model.Title);
        Assert.Equal("Draft content", model.Content);
    }

    [Fact]
    public async Task CopilotDraftIsEditableButCannotSaveWithoutSeparateApproval()
    {
        var workspace = new FakeWorkspace();
        var agent = new FakeAgent { Reply = new("I drafted the page.", [], null, new("ENG", "Improved runbook", "Acceptance criteria\n- Alert routes are verified.")) };
        var model = Create(agent, workspace);
        model.Prompt = "Draft an alert runbook";
        await model.SendAsync();
        Assert.Equal("ENG", model.SpaceKey);
        Assert.Equal("Improved runbook", model.Title);
        Assert.Contains("Alert routes", model.Content);
        Assert.Equal(0, workspace.Saves);
        await model.PrepareSaveAsync();
        Assert.True(model.HasPreview);
        await model.SaveApprovedAsync();
        Assert.Equal(0, workspace.Saves);
        Assert.True(model.HasPreview);
        Assert.Contains("not approved", model.Status);
    }

    [Fact]
    public async Task ExplicitApprovalSavesExactlyOnceAndPreventsDuplicateSave()
    {
        var workspace = new FakeWorkspace();
        var model = Ready(workspace);
        await model.PrepareSaveAsync();
        model.ConfirmSave += preview => preview.Action == "Create" && preview.Title == "Runbook";
        await model.SaveApprovedAsync();
        await model.SaveApprovedAsync();
        Assert.Equal(1, workspace.Saves);
        Assert.Equal("201", model.SavedPage!.Id);
        Assert.True(model.HasSavedPage);
        Assert.Contains("Created", model.Status);
        await model.PrepareSaveAsync();
        Assert.False(model.HasPreview);
        Assert.Contains("already saved", model.Status);
    }

    [Fact]
    public async Task EditingAfterPreviewDiscardsApproval()
    {
        var workspace = new FakeWorkspace();
        var model = Ready(workspace);
        await model.PrepareSaveAsync();
        var preview = model.Preview;
        model.Content = "Changed content";
        Assert.False(model.HasPreview);
        Assert.Equal(preview!.Id, workspace.Discarded);
        model.ConfirmSave += _ => true;
        await model.SaveApprovedAsync();
        Assert.Equal(0, workspace.Saves);
    }

    [Fact]
    public async Task ChatCanCreateAnotherPageAfterAnEarlierApprovedCreate()
    {
        var agent = new FakeAgent { Reply = new("First draft", [], null, new("ENG", "First page", "First content")) };
        var workspace = new FakeWorkspace();
        var model = Create(agent, workspace);
        model.Prompt = "Create first page";
        await model.SendAsync();
        await model.PrepareSaveAsync();
        model.ConfirmSave += _ => true;
        await model.SaveApprovedAsync();
        Assert.True(model.HasSavedPage);

        agent.Reply = new("Second draft", [], null, new("ENG", "Second page", "Second content"));
        model.Prompt = "Create another page";
        await model.SendAsync();

        Assert.False(model.HasSavedPage);
        Assert.Equal("Second page", model.Title);
        await model.PrepareSaveAsync();
        await model.SaveApprovedAsync();
        Assert.Equal(2, workspace.Saves);
        Assert.Equal("Second page", model.SavedPage!.Title);
    }

    [Fact]
    public async Task LoadPageUsesActualPageDetailsForUpdateDraft()
    {
        var workspace = new FakeWorkspace();
        var model = Create(new FakeAgent(), workspace);
        await model.LoadPageAsync("101");
        Assert.Equal("101", model.PageId);
        Assert.Equal("ENG", model.SpaceKey);
        Assert.Equal("Alert runbook", model.Title);
        Assert.Equal("Current & verified text", model.Content);
        await model.PrepareSaveAsync();
        Assert.Equal("Update", model.Preview!.Action);
        Assert.Equal(1, workspace.Reads);
        Assert.Equal(0, workspace.Saves);
    }

    private static ConfluenceAgentViewModel Ready(FakeWorkspace workspace)
    {
        var model = Create(new FakeAgent(), workspace);
        model.SpaceKey = "ENG";
        model.Title = "Runbook";
        model.Content = "Reviewed content";
        return model;
    }
    private static ConfluenceAgentViewModel Create(FakeAgent agent, FakeWorkspace workspace) => new(agent, workspace, new Settings());
    private static ConfluencePageSummary Page(string id, string title) => new(id, title, "ENG", "Relevant excerpt", new Uri($"https://site.atlassian.net/wiki/pages/viewpage.action?pageId={id}"));

    private sealed class FakeAgent : IConfluenceAgent
    {
        public ConfluenceAgentReply Reply { get; set; } = new("No results.", [], null, null);
        public Task<ConfluenceAgentReply> SendAsync(string prompt, IProgress<ConfluenceAgentProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Reply);
        public Task NewConversationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkspace : IConfluenceWorkspaceService
    {
        public int Reads { get; private set; }
        public int Saves { get; private set; }
        public Guid? Discarded { get; private set; }
        private string savedTitle = "Runbook";
        public ConfluencePage? PageToLoad { get; init; }
        public Task<(IReadOnlyList<ConfluencePageSummary> Pages, bool HasMore, string? NextStart)> SearchAsync(string query, string? spaceKey = null, int limit = 20, string? start = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<ConfluencePageSummary>, bool, string?)>(([Page("103", "Later page")], false, null));
        public Task<ConfluencePage> GetAsync(string pageId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(PageToLoad ?? new ConfluencePage(pageId, "Alert runbook", "ENG", "<p>Current &amp; verified text</p>", 4, new Uri("https://site.atlassian.net/wiki/pages/viewpage.action?pageId=" + pageId)));
        }
        public Task<ConfluencePagePreview> PrepareAsync(ConfluencePageDraft draft, CancellationToken cancellationToken = default)
        {
            savedTitle = draft.Title;
            return Task.FromResult(new ConfluencePagePreview(Guid.NewGuid(), "https://site.atlassian.net/wiki", string.IsNullOrWhiteSpace(draft.PageId) ? "Create" : "Update", draft.Title, draft.SpaceKey, draft.Content, "{\"title\":\"" + draft.Title + "\"}"));
        }
        public Task<ConfluencePageSaved> SaveApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(new ConfluencePageSaved("201", savedTitle, "ENG", new Uri("https://site.atlassian.net/wiki/pages/viewpage.action?pageId=201"), false));
        }
        public void DiscardPreview(Guid previewId) => Discarded = previewId;
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSettings(kind, DefaultScope: "ENG"));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

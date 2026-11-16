using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class ConfluenceAgentViewModel : ObservableObject
{
    private readonly IConfluenceAgent agent;
    private readonly IConfluenceWorkspaceService workspace;
    private readonly ISettingsStore settings;
    private readonly IConversationHistoryStore? conversationHistory;
    private readonly IActivityLogStore? activityLog;
    private string? conversationId;
    private DateTimeOffset conversationCreated;
    private string prompt = "";
    private string status = "Search pages, ask for a summary, or ask Copilot to propose a new or improved page.";
    private string searchQuery = "";
    private string? searchSpace;
    private string? nextStart;
    private bool isEditorOpen;
    private string spaceKey = "";
    private string pageId = "";
    private string title = "";
    private string content = "";
    private string previewJson = "";
    private string savedPageUrl = "";
    private bool busy;
    private bool saving;
    private CancellationTokenSource? operation;
    private ConfluencePagePreview? preview;
    private ConfluencePageSaved? savedPage;

    public ConfluenceAgentViewModel(IConfluenceAgent agent, IConfluenceWorkspaceService workspace, ISettingsStore settings,
        IConversationHistoryStore? conversationHistory = null, IActivityLogStore? activityLog = null)
    {
        this.agent = agent;
        this.workspace = workspace;
        this.settings = settings;
        this.conversationHistory = conversationHistory;
        this.activityLog = activityLog;
        SendCommand = new AsyncCommand(SendAsync, ReportError);
        NewConversationCommand = new AsyncCommand(NewConversationAsync, ReportError);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, ReportError);
        LoadPageCommand = new RelayCommand(parameter => _ = LoadPageAsync(parameter as string));
        PrepareSaveCommand = new AsyncCommand(PrepareSaveAsync, ReportError);
        SaveCommand = new AsyncCommand(SaveApprovedAsync, ReportError);
        NewDraftCommand = new RelayCommand(_ => NewDraft());
        CancelCommand = new RelayCommand(_ => operation?.Cancel());
    }

    public ObservableCollection<JiraChatMessage> Messages { get; } = [];
    public ObservableCollection<ConfluencePageSummary> Pages { get; } = [];
    public ICommand SendCommand { get; }
    public ICommand NewConversationCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoadPageCommand { get; }
    public ICommand PrepareSaveCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand NewDraftCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? BusyChanged;
    public event Action? CopilotRequestStarted;
    public event Action<bool>? CopilotRequestFinished;
    public event Func<ConfluencePagePreview, bool>? ConfirmSave;
    public event Func<bool>? ConfirmDiscardDraft;
    public event Func<bool>? ConfirmDiscardConversation;

    public string Prompt { get => prompt; set => Set(ref prompt, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string SpaceKey { get => spaceKey; set { if (SetValue(ref spaceKey, value)) InvalidatePreview(); } }
    public string PageId { get => pageId; set { if (SetValue(ref pageId, value)) InvalidatePreview(); } }
    public string Title { get => title; set { if (SetValue(ref title, value)) InvalidatePreview(); } }
    public string Content { get => content; set { if (SetValue(ref content, value)) InvalidatePreview(); } }
    public string PreviewJson { get => previewJson; private set => Set(ref previewJson, value); }
    public string SavedPageUrl { get => savedPageUrl; private set { Set(ref savedPageUrl, value); Changed(nameof(HasSavedPage)); } }
    public ConfluencePagePreview? Preview { get => preview; private set { Set(ref preview, value); Changed(nameof(HasPreview)); Changed(nameof(CanSave)); } }
    public ConfluencePageSaved? SavedPage { get => savedPage; private set { Set(ref savedPage, value); Changed(nameof(HasSavedPage)); } }
    public bool HasPreview => Preview is not null;
    public bool HasSavedPage => SavedPage is not null;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); Changed(nameof(CanCancel)); Changed(nameof(CanSave)); Changed(nameof(CanLoadMore)); BusyChanged?.Invoke(); } }
    public bool CanEdit => !Busy;
    public bool CanCancel => Busy && !saving;
    public bool CanSave => !Busy && Preview is not null;
    public bool CanLoadMore => !Busy && nextStart is not null;
    public bool HasPages => Pages.Count > 0;
    public bool IsEditorOpen { get => isEditorOpen; set => Set(ref isEditorOpen, value); }
    public bool HasUnsavedDraft => Messages.Count > 0 || Prompt.Length > 0 || PageId.Length > 0 || Title.Length > 0 || Content.Length > 0;

    public async Task InitializeAsync()
    {
        SpaceKey = (await settings.LoadAsync(IntegrationKind.Confluence)).DefaultScope;
    }

    public async Task SendAsync()
    {
        if (Busy) return;
        var input = Prompt.Trim();
        if (input.Length is 0 or > 4000) { Status = "Enter a Confluence request of 1-4000 characters."; return; }
        StartOperation();
        string? activityAction = null;
        var activityOutcome = "Failed";
        Pages.Clear();
        Changed(nameof(HasPages));
        nextStart = null;
        InvalidatePreview();
        try
        {
            Messages.Add(new("You", input));
            Prompt = "";
            CopilotRequestStarted?.Invoke();
            var progress = new Progress<ConfluenceAgentProgress>(value => { if (Busy) Status = value.Message; });
            ConfluenceAgentReply reply;
            try { reply = await agent.SendAsync(input, progress, operation!.Token); }
            catch { CopilotRequestFinished?.Invoke(false); throw; }
            CopilotRequestFinished?.Invoke(true);
            Messages.Add(new("Confluence Agent", reply.Content));
            activityAction = reply.Pages.Count > 0 ? "Search pages" : reply.Page is not null ? "Read page" : reply.Draft is not null ? (reply.Draft.PageId is null ? "Create page" : "Update page") : null;
            activityOutcome = reply.Draft is not null ? "Prepared" : "Completed";
            foreach (var page in reply.Pages) Pages.Add(page);
            if (reply.Pages.Count > 0)
            {
                searchQuery = reply.SearchQuery ?? input;
                searchSpace = reply.SearchSpace;
                nextStart = reply.HasMore ? reply.NextStart : null;
            }
            if (reply.Page is not null)
            {
                if (!Pages.Any(page => page.Id == reply.Page.Id))
                {
                    var excerpt = HtmlToText(reply.Page.StorageContent);
                    if (excerpt.Length > 240) excerpt = excerpt[..240] + "...";
                    Pages.Add(new(reply.Page.Id, reply.Page.Title, reply.Page.SpaceKey, excerpt, reply.Page.Url));
                }
                Messages.Add(new("Page analysis", $"Loaded '{reply.Page.Title}' from space {reply.Page.SpaceKey} for analysis. Page content was treated as untrusted input. Select its result below and choose Load page for editing to prepare a reviewed update."));
            }
            Changed(nameof(HasPages));
            if (reply.Draft is not null)
            {
                SavedPage = null;
                SavedPageUrl = "";
                SpaceKey = reply.Draft.SpaceKey;
                PageId = reply.Draft.PageId ?? "";
                Title = reply.Draft.Title;
                Content = reply.Draft.Content;
                IsEditorOpen = true;
                Messages.Add(new("Draft ready", "Proposed page content is editable below. Review it carefully; nothing has been saved."));
                Status = "Copilot proposed page content. Review the content and exact preview before approving a save.";
            }
            else Status = reply.Pages.Count > 0 ? "Confluence search complete. Select a result to inspect it, or ask Copilot to analyze or draft content." : "Confluence Agent response received.";
        }
        catch (OperationCanceledException) { activityOutcome = "Cancelled"; Status = "Confluence request cancelled."; CopilotRequestFinished?.Invoke(false); }
        catch (Exception exception) { ReportError(exception); CopilotRequestFinished?.Invoke(false); }
        finally
        {
            await PersistConversationAsync();
            if (activityAction is not null) await RecordActivityAsync(activityAction, activityOutcome);
            FinishOperation();
        }
    }

    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore) return;
        StartOperation();
        try
        {
            var result = await workspace.SearchAsync(searchQuery, searchSpace, 20, nextStart, operation!.Token);
            foreach (var page in result.Pages) Pages.Add(page);
            Changed(nameof(HasPages));
            nextStart = result.HasMore ? result.NextStart : null;
            Status = result.HasMore ? "More Confluence pages are available." : "All available Confluence search results are loaded.";
            await RecordActivityAsync("Search pages", "Completed");
        }
        catch (OperationCanceledException) { Status = "Loading more Confluence results was cancelled."; await RecordActivityAsync("Search pages", "Cancelled"); }
        catch (Exception exception) { ReportError(exception); await RecordActivityAsync("Search pages", "Failed"); }
        finally { FinishOperation(); }
    }

    public async Task LoadPageAsync(string? id)
    {
        if (Busy || string.IsNullOrWhiteSpace(id)) return;
        StartOperation();
        try
        {
            var page = await workspace.GetAsync(id, operation!.Token);
            SpaceKey = page.SpaceKey;
            PageId = page.Id;
            Title = page.Title;
            Content = HtmlToText(page.StorageContent);
            IsEditorOpen = true;
            Status = $"Loaded {page.Title}. Editing replaces page body with plain paragraphs; review the full preview before approving.";
            await RecordActivityAsync("Read page", "Completed");
        }
        catch (OperationCanceledException) { Status = "Loading the Confluence page was cancelled."; await RecordActivityAsync("Read page", "Cancelled"); }
        catch (Exception exception) { ReportError(exception); await RecordActivityAsync("Read page", "Failed"); }
        finally { FinishOperation(); }
    }

    public async Task PrepareSaveAsync()
    {
        if (Busy) return;
        if (SavedPage is not null) { Status = "This draft already saved a page. Start a new draft before saving another."; return; }
        InvalidatePreview();
        StartOperation();
        var action = string.IsNullOrWhiteSpace(PageId) ? "Create page" : "Update page";
        var outcome = "Failed";
        try
        {
            Preview = await workspace.PrepareAsync(new(SpaceKey.Trim(), Title.Trim(), Content, string.IsNullOrWhiteSpace(PageId) ? null : PageId.Trim()), operation!.Token);
            PreviewJson = Preview.PayloadJson;
            outcome = "Prepared";
            Status = "Review the exact Confluence request. The approval expires after 15 minutes; any edit invalidates it.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { await RecordActivityAsync(action, outcome); FinishOperation(); }
    }

    public async Task SaveApprovedAsync()
    {
        if (!CanSave || Preview is not { } approved) return;
        saving = true;
        StartOperation();
        try
        {
            if (ConfirmSave?.Invoke(approved) != true) { Status = "Save not approved. No Confluence changes were sent."; await RecordActivityAsync(approved.Action == "Create" ? "Create page" : "Update page", "Declined"); return; }
            Preview = null;
            StartOperationStatus("Saving the approved page exactly once...");
            SavedPage = await workspace.SaveApprovedAsync(approved.Id, operation!.Token);
            SavedPageUrl = SavedPage.Url.AbsoluteUri;
            Status = $"{(SavedPage.Updated ? "Updated" : "Created")} Confluence page '{SavedPage.Title}'.";
            Messages.Add(new("Confluence", $"{Status} The page was saved only after your explicit approval."));
            await RecordActivityAsync(SavedPage.Updated ? "Update page" : "Create page", "Completed");
        }
        catch (Exception exception)
        {
            ReportError(exception);
            if (approved.Action is "Create" or "Update")
                await RecordActivityAsync(approved.Action == "Create" ? "Create page" : "Update page", exception.Message.Contains("may already have saved", StringComparison.OrdinalIgnoreCase) ? "Unknown outcome" : "Failed");
        }
        finally { saving = false; await PersistConversationAsync(); FinishOperation(); }
    }

    public void NewDraft()
    {
        if (Busy || (HasUnsavedDraft && ConfirmDiscardDraft?.Invoke() != true)) return;
        InvalidatePreview();
        Prompt = "";
        PageId = "";
        Title = "";
        Content = "";
        IsEditorOpen = true;
        SavedPage = null;
        SavedPageUrl = "";
        conversationId = null;
        conversationCreated = default;
        Messages.Clear();
        Pages.Clear();
        Changed(nameof(HasPages));
        nextStart = null;
        Status = "New Confluence draft started. The configured default space remains selected.";
    }

    public async Task NewConversationAsync()
    {
        if (Busy || ((Messages.Count > 0 || Prompt.Length > 0) && ConfirmDiscardConversation?.Invoke() != true)) return;
        StartOperation();
        try
        {
            await agent.NewConversationAsync(operation!.Token);
            Messages.Clear();
            Prompt = "";
            conversationId = null;
            conversationCreated = default;
            Pages.Clear();
            Changed(nameof(HasPages));
            nextStart = null;
            Status = "New Confluence conversation started. Existing saved pages were not changed.";
        }
        catch (OperationCanceledException) { Status = "Starting a new conversation was cancelled."; }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    private async Task PersistConversationAsync()
    {
        if (conversationHistory is null || Messages.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        conversationId ??= Guid.NewGuid().ToString("N");
        if (conversationCreated == default) conversationCreated = now;
        var title = Messages.FirstOrDefault(message => message.Speaker == "You")?.Text ?? "Confluence conversation";
        if (title.Length > 120) title = title[..120];
        try
        {
            var messages = Messages.Select(message => new AgentConversationMessage(message.Speaker, message.Text, now)).ToArray();
            await conversationHistory.SaveAsync(new(conversationId, IntegrationKind.Confluence, title, conversationCreated, now, messages));
        }
        catch (Exception) { Status += " Conversation is visible now but could not be saved to local history."; }
    }

    private async Task RecordActivityAsync(string action, string outcome)
    {
        if (activityLog is null) return;
        try { await activityLog.RecordAsync(IntegrationKind.Confluence, action, outcome); }
        catch (Exception) { Status += " Activity log could not be saved locally."; }
    }

    private void StartOperation() { operation = new CancellationTokenSource(); Busy = true; }
    private void StartOperationStatus(string message) { Status = message; }
    private void FinishOperation() { operation?.Dispose(); operation = null; Busy = false; }
    private bool SetValue(ref string field, string value)
    {
        if (field == value) return false;
        Set(ref field, value);
        return true;
    }
    private void InvalidatePreview()
    {
        if (Preview is not null) workspace.DiscardPreview(Preview.Id);
        Preview = null;
        PreviewJson = "";
    }
    private static string HtmlToText(string storage) => System.Net.WebUtility.HtmlDecode(Regex.Replace(storage, "<[^>]*>", " "))
        .Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    private void ReportError(Exception exception) => Status = exception is IntegrationException or ArgumentException
        ? exception.Message
        : exception is OperationCanceledException ? "Operation cancelled; the current draft was retained."
        : "Confluence operation failed. Check Copilot sign-in, Confluence access and the connection. Provider details are hidden to protect credentials.";
}

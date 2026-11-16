using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed record JiraFieldOption(string Label, string Json);

public sealed class JiraCreateFieldViewModel : ObservableObject
{
    private string value = "";
    private JiraFieldOption? selectedOption;
    public JiraCreateField Metadata { get; }
    public event Action? Edited;
    public string Label => $"{Metadata.Name}{(Metadata.Required ? " (required)" : "")}";
    public string Hint => $"{Metadata.Id} / {Metadata.SchemaType}. " + (Metadata.SchemaType switch
    {
        "date" => "Use yyyy-MM-dd.",
        "user" => "Enter the Jira account ID, not a display name.",
        "array" => "Enter a JSON array, for example [\"label\"] or [{\"id\":\"option-id\"}].",
        "number" or "boolean" => "Enter a JSON number or true/false.",
        "string" when Metadata.SchemaCustom?.EndsWith(":textarea", StringComparison.Ordinal) == true => "Enter an Atlassian Document Format JSON object.",
        "string" or "team" or "datetime" => "Enter the value as text.",
        _ => "Enter a Jira field JSON object using the appropriate IDs."
    }) + (Metadata.HasDefault ? " Blank uses the Jira default." : " Blank omits this field.");
    public bool IsChoice => Options.Count > 1;
    public bool IsText => !IsChoice;
    public ObservableCollection<JiraFieldOption> Options { get; } = [new("Use Jira default / omit", "")];
    public JiraFieldOption? SelectedOption { get => selectedOption; set { Set(ref selectedOption, value); Value = value?.Json ?? ""; } }
    public string Value { get => value; set { Set(ref this.value, value); Edited?.Invoke(); } }

    public JiraCreateFieldViewModel(JiraCreateField metadata, JsonNode? initial)
    {
        Metadata = metadata;
        if (metadata.AllowedValuesJson is not null && metadata.SchemaType != "array")
        {
            foreach (var option in JsonNode.Parse(metadata.AllowedValuesJson)!.AsArray())
            {
                if (option is JsonObject obj)
                {
                    var identifier = new[] { "id", "accountId", "value" }.FirstOrDefault(key => obj[key] is not null);
                    if (identifier is null) continue;
                    var label = obj["name"]?.ToString() ?? obj["displayName"]?.ToString() ?? obj["value"]?.ToString() ?? obj[identifier]!.ToString();
                    var json = new JsonObject { [identifier] = obj[identifier]!.DeepClone() }.ToJsonString();
                    Options.Add(new(label, json));
                }
                else if (option is not null) Options.Add(new(option.ToString(), option.ToJsonString()));
            }
        }
        if (IsChoice)
        {
            selectedOption = initial is null ? Options[0] : Options.FirstOrDefault(option => option.Json.Length > 0 && JsonNode.DeepEquals(JsonNode.Parse(option.Json), initial));
            if (selectedOption is null && initial is not null)
            {
                selectedOption = new("Reference value (verify availability)", initial.ToJsonString());
                Options.Add(selectedOption);
            }
            value = selectedOption?.Json ?? "";
        }
        else if (initial is JsonValue scalar && scalar.TryGetValue<string>(out var text)) value = text;
        else if (metadata.SchemaType == "user" && initial is JsonObject user) value = user["accountId"]?.ToString() ?? "";
        else value = initial?.ToJsonString() ?? "";
    }

    public JsonNode? GetValue()
    {
        if (string.IsNullOrWhiteSpace(Value)) return null;
        try
        {
            if (IsChoice) return JsonNode.Parse(Value);
            return Metadata.SchemaType switch
            {
                "user" => new JsonObject { ["accountId"] = Value.Trim() },
                "string" when Metadata.SchemaCustom?.EndsWith(":textarea", StringComparison.Ordinal) == true => JsonNode.Parse(Value),
                "string" or "date" or "datetime" or "team" => JsonValue.Create(Value.Trim()),
                _ => JsonNode.Parse(Value)
            };
        }
        catch (JsonException) { throw new IntegrationException($"'{Metadata.Name}' needs valid JSON. {Hint}"); }
    }
}

public sealed class JiraCreateViewModel : ObservableObject
{
    private readonly IJiraDraftAgent agent;
    private readonly IJiraIssueCreationService creation;
    private readonly ISettingsStore settings;
    private readonly IConversationHistoryStore? conversationHistory;
    private readonly IActivityLogStore? activityLog;
    private string? conversationId;
    private DateTimeOffset conversationCreated;
    private string projectKey = "";
    private JiraIssueType? selectedType;
    private string referenceKey = "";
    private string parentKey = "";
    private string instructions = "";
    private string summary = "";
    private string description = "";
    private string status = "Paste rough notes to get a polished story. Nothing is created until you approve the exact preview.";
    private string referenceNotice = "No reference defaults loaded.";
    private string attemptNotice = "";
    private bool busy;
    private bool creating;
    private CancellationTokenSource? operation;
    private JiraCreationPreview? preview;
    private JiraCreatedIssue? createdIssue;
    private string? loadedProject;
    private string? loadedType;

    public JiraCreateViewModel(IJiraDraftAgent agent, IJiraIssueCreationService creation, ISettingsStore settings,
        IConversationHistoryStore? conversationHistory = null, IActivityLogStore? activityLog = null)
    {
        this.agent = agent;
        this.creation = creation;
        this.settings = settings;
        this.conversationHistory = conversationHistory;
        this.activityLog = activityLog;
        RefineCommand = new AsyncCommand(RefineAsync, ReportError);
        LoadTypesCommand = new AsyncCommand(LoadTypesAsync, ReportError);
        LoadFieldsCommand = new AsyncCommand(LoadFieldsAsync, ReportError);
        PrepareCommand = new AsyncCommand(PrepareAsync, ReportError);
        ApproveCommand = new AsyncCommand(ApproveAndCreateAsync, ReportError);
        CancelCommand = new RelayCommand(_ => operation?.Cancel());
        NewDraftCommand = new RelayCommand(_ => NewDraft());
    }

    public ObservableCollection<JiraChatMessage> Messages { get; } = [];
    public ObservableCollection<JiraIssueType> IssueTypes { get; } = [];
    public ObservableCollection<JiraCreateFieldViewModel> Fields { get; } = [];
    public ObservableCollection<string> FieldNotices { get; } = [];
    public ICommand RefineCommand { get; }
    public ICommand LoadTypesCommand { get; }
    public ICommand LoadFieldsCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NewDraftCommand { get; }
    public event Action? BusyChanged;
    public event Action? CopilotRequestStarted;
    public event Action<bool>? CopilotRequestFinished;
    public event Func<JiraCreationPreview, bool>? ConfirmCreation;
    public event Func<bool>? ConfirmDiscardDraft;
    public string ProjectKey { get => projectKey; set { if (projectKey == value) return; Set(ref projectKey, value); SelectedType = null; IssueTypes.Clear(); InvalidateFields(); } }
    public JiraIssueType? SelectedType { get => selectedType; set { if (selectedType == value) return; Set(ref selectedType, value); InvalidateFields(); } }
    public string ReferenceKey { get => referenceKey; set { if (referenceKey == value) return; Set(ref referenceKey, value); InvalidateFields(); } }
    public string ParentKey { get => parentKey; set { Set(ref parentKey, value); InvalidatePreview(); } }
    public string Instructions { get => instructions; set { Set(ref instructions, value); InvalidatePreview(); } }
    public string Summary { get => summary; set { Set(ref summary, value); InvalidatePreview(); } }
    public string Description { get => description; set { Set(ref description, value); InvalidatePreview(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string ReferenceNotice { get => referenceNotice; private set => Set(ref referenceNotice, value); }
    public string AttemptNotice { get => attemptNotice; private set => Set(ref attemptNotice, value); }
    public JiraCreationPreview? Preview { get => preview; private set { Set(ref preview, value); Changed(nameof(HasPreview)); Changed(nameof(CanApprove)); } }
    public JiraCreatedIssue? CreatedIssue { get => createdIssue; private set { Set(ref createdIssue, value); Changed(nameof(HasCreatedIssue)); } }
    public bool HasCreatedIssue => CreatedIssue is not null;
    public bool HasPreview => Preview is not null;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); Changed(nameof(CanCancel)); Changed(nameof(CanApprove)); BusyChanged?.Invoke(); } }
    public bool CanEdit => !Busy;
    public bool CanCancel => Busy && !creating;
    public bool CanApprove => !Busy && Preview is not null;
    public bool HasUnsavedDraft => Instructions.Length > 0 || Summary.Length > 0 || Description.Length > 0 ||
        ParentKey.Length > 0 || ReferenceKey.Length > 0 || Fields.Any(editor => !string.IsNullOrWhiteSpace(editor.Value));

    public async Task InitializeAsync() => ProjectKey = (await settings.LoadAsync(IntegrationKind.Jira)).DefaultScope;

    public async Task LoadTypesAsync()
    {
        if (Busy) return;
        StartOperation();
        try
        {
            InvalidateFields();
            IssueTypes.Clear();
            SelectedType = null;
            foreach (var type in await creation.GetIssueTypesAsync(ProjectKey.Trim(), operation!.Token)) IssueTypes.Add(type);
            Status = IssueTypes.Count == 0 ? "No issue types available. Check Create issues permission for this project." : "Choose an available issue type, then load its fields. A story description can be used in a Task if Story is not offered by this project.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task LoadFieldsAsync()
    {
        if (Busy) return;
        if (SelectedType is null) { Status = "Load project issue types and choose one first."; return; }
        StartOperation();
        try
        {
            InvalidateFields();
            var template = await creation.LoadTemplateAsync(ProjectKey.Trim(), SelectedType.Name, string.IsNullOrWhiteSpace(ReferenceKey) ? null : ReferenceKey.Trim(), operation!.Token);
            var initial = JsonNode.Parse(template.InitialFieldsJson)!.AsObject();
            foreach (var field in template.Metadata.Fields.Where(field => !new[] { "project", "issuetype", "summary", "description", "parent" }.Contains(field.Id)))
            {
                if (!field.CanSet || field.SchemaType is "unknown" or "any" ||
                    field.SchemaCustom?.Contains(":rank", StringComparison.OrdinalIgnoreCase) == true ||
                    field.SchemaCustom?.Contains(":development", StringComparison.OrdinalIgnoreCase) == true ||
                    new[] { "attachment", "issuelinks", "issuerestriction", "reporter", "comment", "worklog", "status", "resolution", "created", "updated", "creator" }.Contains(field.Id))
                {
                    FieldNotices.Add($"{field.Name} ({field.Id}) is not supported by this workflow{(field.Required && !field.HasDefault ? "; it is required, so creation will be blocked" : " and will be omitted")}.");
                    continue;
                }
                var editor = new JiraCreateFieldViewModel(field, initial[field.Id]);
                editor.Edited += InvalidatePreview;
                Fields.Add(editor);
            }
            loadedProject = ProjectKey.Trim();
            loadedType = SelectedType.Name;
            ReferenceNotice = template.ReferenceNotice;
            Status = "Jira fields loaded. Review any reference defaults and fill required fields. No issue was created.";
        }
        catch (Exception exception) { InvalidateFields(); ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task RefineAsync()
    {
        if (Busy) return;
        if (string.IsNullOrWhiteSpace(Instructions)) { Status = "Paste rough content or type revision instructions first."; return; }
        StartOperation();
        InvalidatePreview();
        var input = Instructions;
        try
        {
            CopilotRequestStarted?.Invoke();
            Status = "Copilot is refining your notes into a proposed story...";
            var current = string.IsNullOrWhiteSpace(Summary) && string.IsNullOrWhiteSpace(Description) ? null : new JiraDraftContent(Summary, Description);
            var result = await agent.RefineAsync(input, current, operation!.Token);
            CopilotRequestFinished?.Invoke(true);
            Messages.Add(new("You", input));
            Summary = result.Summary;
            Description = result.Description;
            Instructions = "";
            Messages.Add(new("Copilot", "Proposed content updated below. Review or edit it, or send further instructions. No Jira issue was created."));
            Status = "Proposed content ready for your review. Check facts and acceptance criteria before approval.";
        }
        catch (Exception exception) { CopilotRequestFinished?.Invoke(false); ReportError(exception); }
        finally { await PersistConversationAsync(); FinishOperation(); }
    }

    public async Task PrepareAsync()
    {
        if (Busy) return;
        if (CreatedIssue is not null) { Status = "This draft already created an issue. Start a new draft before creating another."; return; }
        InvalidatePreview();
        if (SelectedType is null || loadedProject != ProjectKey.Trim() || loadedType != SelectedType.Name)
        { Status = "Load the fields for this project and issue type before preparing approval."; return; }
        if (!string.IsNullOrWhiteSpace(Instructions)) { Status = "Refine your pending instructions or clear them before approving the current content."; return; }
        StartOperation();
        try
        {
            var extra = new JsonObject();
            foreach (var field in Fields)
            {
                var value = field.GetValue();
                if (value is not null) extra[field.Metadata.Id] = value;
            }
            Preview = await creation.PrepareAsync(new(ProjectKey.Trim(), SelectedType.Name, Summary.Trim(), Description.Trim(), string.IsNullOrWhiteSpace(ParentKey) ? null : ParentKey.Trim()), extra.ToJsonString(), operation!.Token);
            Status = "Review the exact Jira payload below. Approval expires in 15 minutes; any edit invalidates it. Jira performs final permission and workflow validation.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task ApproveAndCreateAsync()
    {
        if (!CanApprove || Preview is not { } approved) return;
        creating = true;
        StartOperation();
        var activityOutcome = "Declined";
        try
        {
            if (ConfirmCreation?.Invoke(approved) != true) { Status = "Creation not approved. Nothing was sent to Jira."; return; }
            Preview = null;
            Status = "Creating the approved issue in Jira. This request is sent only once...";
            AttemptNotice = "A creation request was started. If no confirmed result is shown, check Jira before trying again to avoid duplicates.";
            CreatedIssue = await creation.CreateApprovedAsync(approved.Id, operation!.Token);
            activityOutcome = "Completed";
            AttemptNotice = $"Created {CreatedIssue.Key}. Do not submit the same story again unless you intend to create another issue.";
            Status = $"Created {CreatedIssue.Key} successfully. Open the link below.";
            Messages.Add(new("Jira", $"Created {CreatedIssue.Key} after your explicit approval."));
        }
        catch (Exception exception)
        {
            activityOutcome = exception.Message.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown outcome" : "Failed";
            ReportError(exception);
        }
        finally
        {
            await RecordActivityAsync(activityOutcome);
            await PersistConversationAsync();
            creating = false;
            FinishOperation();
        }
    }

    public void NewDraft()
    {
        if (Busy || (HasUnsavedDraft && ConfirmDiscardDraft?.Invoke() != true)) return;
        InvalidatePreview();
        Summary = "";
        Description = "";
        Instructions = "";
        ParentKey = "";
        CreatedIssue = null;
        Messages.Clear();
        conversationId = null;
        conversationCreated = default;
        Status = "New draft started. Project fields and any reference defaults are still shown; review them before reuse.";
    }

    private void InvalidatePreview()
    {
        if (Preview is not null) creation.DiscardPreview(Preview.Id);
        Preview = null;
    }

    private void InvalidateFields()
    {
        InvalidatePreview();
        foreach (var field in Fields) field.Edited -= InvalidatePreview;
        Fields.Clear();
        FieldNotices.Clear();
        loadedProject = null;
        loadedType = null;
        ReferenceNotice = "Fields or reference changed. Load fields again before approval.";
    }

    private async Task PersistConversationAsync()
    {
        if (conversationHistory is null || Messages.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        conversationId ??= Guid.NewGuid().ToString("N");
        if (conversationCreated == default) conversationCreated = now;
        var title = Messages.FirstOrDefault(message => message.Speaker == "You")?.Text ?? "Jira issue draft";
        if (title.Length > 120) title = title[..120];
        try
        {
            var messages = Messages.Select(message => new AgentConversationMessage(message.Speaker, message.Text, now)).ToArray();
            await conversationHistory.SaveAsync(new(conversationId, IntegrationKind.Jira, title, conversationCreated, now, messages));
        }
        catch (Exception) { Status += " Conversation is visible now but could not be saved to local history."; }
    }

    private async Task RecordActivityAsync(string outcome)
    {
        if (activityLog is null) return;
        try { await activityLog.RecordAsync(IntegrationKind.Jira, "Create issue", outcome); }
        catch (Exception) { Status += " Activity log could not be saved locally."; }
    }

    private void StartOperation() { operation = new CancellationTokenSource(); Busy = true; }
    private void FinishOperation() { operation?.Dispose(); operation = null; Busy = false; }
    private void ReportError(Exception exception) => Status = exception is IntegrationException or ArgumentException ? exception.Message :
        exception is OperationCanceledException ? "Operation cancelled. Your draft was kept." : "The operation failed. Your content was kept. Check Copilot, Jira access and your connection; provider details are hidden to protect credentials.";
}

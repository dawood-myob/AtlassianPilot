using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class JiraActionsViewModel : ObservableObject
{
    private readonly IJiraIssueActionService actions;
    private readonly IActivityLogStore activity;
    private CancellationTokenSource? operation;
    private JiraActionPreview? preview;
    private string issueKey = "";
    private string summary = "";
    private string description = "";
    private string comment = "";
    private string accountId = "";
    private string linkedIssueKey = "";
    private string cloneProjectKey = "";
    private string cloneIssueType = "Task";
    private JiraWorkflowTransition? selectedTransition;
    private JiraIssueLinkType? selectedLinkType;
    private string status = "Prepare Jira changes for review. Nothing is sent until you approve the exact preview.";
    private bool busy;
    private JiraActionResult? result;

    public JiraActionsViewModel(IJiraIssueActionService actions, IActivityLogStore activity)
    {
        this.actions = actions;
        this.activity = activity;
        LoadTransitionsCommand = new AsyncCommand(LoadTransitionsAsync, ReportError);
        LoadLinkTypesCommand = new AsyncCommand(LoadLinkTypesAsync, ReportError);
        PrepareUpdateCommand = MakePrepare(PrepareUpdateAsync);
        PrepareCommentCommand = MakePrepare(PrepareCommentAsync);
        PrepareAssignmentCommand = MakePrepare(PrepareAssignmentAsync);
        PrepareTransitionCommand = MakePrepare(PrepareTransitionAsync);
        PrepareLinkCommand = MakePrepare(PrepareLinkAsync);
        PrepareCloneCommand = MakePrepare(PrepareCloneAsync);
        ApproveCommand = new AsyncCommand(ApproveAsync, ReportError);
        CancelCommand = new RelayCommand(_ => operation?.Cancel());
    }

    public ObservableCollection<JiraWorkflowTransition> Transitions { get; } = [];
    public ObservableCollection<JiraIssueLinkType> LinkTypes { get; } = [];
    public ICommand LoadTransitionsCommand { get; }
    public ICommand LoadLinkTypesCommand { get; }
    public ICommand PrepareUpdateCommand { get; }
    public ICommand PrepareCommentCommand { get; }
    public ICommand PrepareAssignmentCommand { get; }
    public ICommand PrepareTransitionCommand { get; }
    public ICommand PrepareLinkCommand { get; }
    public ICommand PrepareCloneCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }
    public event Func<JiraActionPreview, bool>? ConfirmAction;
    public event Action? BusyChanged;

    public string IssueKey { get => issueKey; set { if (SetValue(ref issueKey, value)) { Result = null; InvalidatePreview(); } } }
    public string Summary { get => summary; set { if (SetValue(ref summary, value)) InvalidatePreview(); } }
    public string Description { get => description; set { if (SetValue(ref description, value)) InvalidatePreview(); } }
    public string Comment { get => comment; set { if (SetValue(ref comment, value)) InvalidatePreview(); } }
    public string AccountId { get => accountId; set { if (SetValue(ref accountId, value)) InvalidatePreview(); } }
    public string LinkedIssueKey { get => linkedIssueKey; set { if (SetValue(ref linkedIssueKey, value)) InvalidatePreview(); } }
    public string CloneProjectKey { get => cloneProjectKey; set { if (SetValue(ref cloneProjectKey, value)) InvalidatePreview(); } }
    public string CloneIssueType { get => cloneIssueType; set { if (SetValue(ref cloneIssueType, value)) InvalidatePreview(); } }
    public JiraWorkflowTransition? SelectedTransition { get => selectedTransition; set { if (selectedTransition == value) return; Set(ref selectedTransition, value); InvalidatePreview(); } }
    public JiraIssueLinkType? SelectedLinkType { get => selectedLinkType; set { if (selectedLinkType == value) return; Set(ref selectedLinkType, value); InvalidatePreview(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public JiraActionPreview? Preview { get => preview; private set { Set(ref preview, value); Changed(nameof(HasPreview)); Changed(nameof(CanApprove)); } }
    public bool HasPreview => Preview is not null;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); Changed(nameof(CanApprove)); Changed(nameof(CanCancel)); BusyChanged?.Invoke(); } }
    public bool CanEdit => !Busy;
    public bool CanApprove => !Busy && Preview is not null;
    public bool CanCancel => Busy;
    public JiraActionResult? Result { get => result; private set { Set(ref result, value); Changed(nameof(HasResult)); Changed(nameof(HasIssueUrl)); } }
    public bool HasResult => Result is not null;
    public bool HasIssueUrl => Result?.IssueUrl is not null;

    public async Task LoadTransitionsAsync()
    {
        if (Busy) return;
        StartOperation();
        try
        {
            var items = await actions.GetTransitionsAsync(IssueKey.Trim(), operation!.Token);
            Transitions.Clear();
            foreach (var item in items) Transitions.Add(item);
            SelectedTransition = null;
            Status = items.Count == 0 ? "Jira returned no available transitions for this issue." : "Choose a transition available to your account, then prepare it for approval.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task LoadLinkTypesAsync()
    {
        if (Busy) return;
        StartOperation();
        try
        {
            var items = await actions.GetLinkTypesAsync(operation!.Token);
            LinkTypes.Clear();
            foreach (var item in items) LinkTypes.Add(item);
            SelectedLinkType = null;
            Status = items.Count == 0 ? "No Jira issue link types are available." : "Choose an available link type and review its direction before preparing the link.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task PrepareUpdateAsync() => await PrepareAsync(() => actions.PrepareUpdateAsync(IssueKey.Trim(), NullIfBlank(Summary), NullIfBlank(Description), operation!.Token));
    public async Task PrepareCommentAsync() => await PrepareAsync(() => actions.PrepareCommentAsync(IssueKey.Trim(), Comment, operation!.Token));
    public async Task PrepareAssignmentAsync() => await PrepareAsync(() => actions.PrepareAssignmentAsync(IssueKey.Trim(), AccountId, operation!.Token));
    public async Task PrepareTransitionAsync() => await PrepareAsync(() => SelectedTransition is null ? throw new IntegrationException("Load and select a transition first.") : actions.PrepareTransitionAsync(IssueKey.Trim(), SelectedTransition.Id, operation!.Token));
    public async Task PrepareLinkAsync() => await PrepareAsync(() => SelectedLinkType is null ? throw new IntegrationException("Load and select an issue link type first.") : actions.PrepareLinkAsync(IssueKey.Trim(), LinkedIssueKey.Trim(), SelectedLinkType.Name, operation!.Token));
    public async Task PrepareCloneAsync() => await PrepareAsync(() => actions.PrepareCloneAsync(IssueKey.Trim(), CloneProjectKey.Trim(), CloneIssueType.Trim(), operation!.Token));

    private async Task PrepareAsync(Func<Task<JiraActionPreview>> prepare)
    {
        if (Busy) return;
        InvalidatePreview();
        Result = null;
        StartOperation();
        try
        {
            Preview = await prepare();
            Status = "Review the exact Jira payload. Approval expires in 15 minutes; any edit invalidates it. Jira performs final permission and workflow validation.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    public async Task ApproveAsync()
    {
        if (!CanApprove || Preview is not { } approved) return;
        StartOperation();
        var outcome = "Declined";
        try
        {
            if (ConfirmAction?.Invoke(approved) != true) { Status = "Action not approved. No Jira changes were sent."; return; }
            Preview = null;
            Status = "Submitting the approved Jira action exactly once...";
            Result = await actions.ExecuteApprovedAsync(approved.Id, operation!.Token);
            outcome = "Completed";
            Status = Result.Message;
        }
        catch (Exception exception)
        {
            outcome = exception is OperationCanceledException ? "Cancelled" :
                exception.Message.Contains("outcome", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("check Jira", StringComparison.OrdinalIgnoreCase) ? "Unknown outcome" : "Failed";
            ReportError(exception);
        }
        finally
        {
            await RecordActivityAsync(approved.Action, outcome);
            FinishOperation();
        }
    }

    private ICommand MakePrepare(Func<Task> action) => new AsyncCommand(action, ReportError);
    private void StartOperation() { operation = new CancellationTokenSource(); Busy = true; }
    private void FinishOperation() { operation?.Dispose(); operation = null; Busy = false; }
    private void InvalidatePreview()
    {
        if (Preview is not null) actions.DiscardPreview(Preview.Id);
        Preview = null;
    }
    private async Task RecordActivityAsync(string action, string outcome)
    {
        try { await activity.RecordAsync(IntegrationKind.Jira, action, outcome); }
        catch (Exception) { Status += " Activity log could not be saved locally."; }
    }
    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private bool SetValue(ref string field, string value)
    {
        if (field == value) return false;
        Set(ref field, value);
        return true;
    }
    public void ReportError(Exception exception) => Status = exception is IntegrationException or ArgumentException
        ? exception.Message
        : exception is OperationCanceledException ? "Jira action was cancelled; no automatic retry was made. Check Jira if submission had already started."
        : "Jira action failed. Check Jira permissions and connection. Provider details are hidden.";
}

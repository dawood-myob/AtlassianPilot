using System.Text.Json.Nodes;
using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.UnitTests;

public sealed class JiraCreateViewModelTests
{
    [Fact]
    public async Task RefinementPreservesInstructionsForRevisionAndNeverCreates()
    {
        var agent = new DraftAgent();
        var service = new CreationService();
        var vm = Create(agent, service);
        vm.Instructions = "rough migration notes";
        await vm.RefineAsync();
        Assert.Equal("Migrate alerts", vm.Summary);
        Assert.Contains("Acceptance criteria", vm.Description);
        Assert.Equal("", vm.Instructions);
        Assert.Equal(2, vm.Messages.Count);
        vm.Instructions = "Make the criteria more specific";
        await vm.RefineAsync();
        Assert.Equal("Make the criteria more specific", agent.Instructions);
        Assert.Equal(new JiraDraftContent("Migrate alerts", "Acceptance criteria: prefer Teams, assess on-call if unsupported."), agent.Current);
        Assert.Equal(0, service.Creates);
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public async Task CreatingRequiresPreparedPreviewAndExplicitConfirmation()
    {
        var service = new CreationService();
        var vm = await ReadyAsync(service);
        await vm.ApproveAndCreateAsync();
        Assert.Equal(0, service.Creates);
        await vm.PrepareAsync();
        Assert.True(vm.CanApprove);
        vm.ConfirmCreation += _ => false;
        await vm.ApproveAndCreateAsync();
        Assert.Equal(0, service.Creates);
        Assert.True(vm.HasPreview);
        Assert.Contains("not approved", vm.Status);
    }

    [Fact]
    public async Task ApprovalCreatesExactlyOneIssueAndShowsItsLink()
    {
        var service = new CreationService();
        var vm = await ReadyAsync(service);
        await vm.PrepareAsync();
        vm.ConfirmCreation += preview => preview.SiteUrl == "https://site.atlassian.net";
        await vm.ApproveAndCreateAsync();
        await vm.ApproveAndCreateAsync();
        Assert.Equal(1, service.Creates);
        Assert.False(vm.HasPreview);
        Assert.Equal("ABC-2", vm.CreatedIssue!.Key);
        Assert.Equal("https://site.atlassian.net/browse/ABC-2", vm.CreatedIssue.Url.AbsoluteUri);
        Assert.Contains("successfully", vm.Status);
        await vm.PrepareAsync();
        Assert.False(vm.HasPreview);
        Assert.Contains("already created", vm.Status);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("description")]
    [InlineData("instructions")]
    [InlineData("parent")]
    [InlineData("field")]
    [InlineData("project")]
    [InlineData("reference")]
    public async Task AnyEditInvalidatesThePreparedApproval(string edit)
    {
        var service = new CreationService();
        var vm = await ReadyAsync(service);
        await vm.PrepareAsync();
        switch (edit)
        {
            case "summary": vm.Summary = "Changed"; break;
            case "description": vm.Description = "Changed"; break;
            case "instructions": vm.Instructions = "Changed"; break;
            case "parent": vm.ParentKey = "ABC-100"; break;
            case "field": vm.Fields[0].Value = "new-label"; break;
            case "project": vm.ProjectKey = "XYZ"; break;
            case "reference": vm.ReferenceKey = "ABC-10"; break;
        }
        Assert.False(vm.HasPreview);
        Assert.Equal(1, service.Discards);
        vm.ConfirmCreation += _ => true;
        await vm.ApproveAndCreateAsync();
        Assert.Equal(0, service.Creates);
    }

    [Fact]
    public async Task CreationConversationAndOutcomesArePersistedLocally()
    {
        var history = new MemoryHistory();
        var activity = new MemoryActivity();
        var service = new CreationService();
        var vm = await ReadyAsync(service, history, activity);
        var initial = Assert.Single(history.Conversations);
        Assert.Equal(2, initial.Messages.Count);
        Assert.Equal("Rough notes", initial.Title);

        await vm.PrepareAsync();
        vm.ConfirmCreation += _ => false;
        await vm.ApproveAndCreateAsync();
        Assert.Equal("Declined", Assert.Single(activity.Entries).Outcome);

        vm.ConfirmCreation += _ => true;
        await vm.ApproveAndCreateAsync();
        Assert.Equal(new[] { "Declined", "Completed" }, activity.Entries.Select(entry => entry.Outcome));
        var saved = Assert.Single(history.Conversations);
        Assert.Equal(initial.Id, saved.Id);
        Assert.Equal(3, saved.Messages.Count);
        Assert.Equal("Jira", saved.Messages[^1].Speaker);
    }

    [Fact]
    public async Task FailedCreationConsumesApprovalKeepsContentAndDoesNotShowSuccess()
    {
        var service = new CreationService { FailCreate = true };
        var vm = await ReadyAsync(service);
        await vm.PrepareAsync();
        vm.ConfirmCreation += _ => true;
        await vm.ApproveAndCreateAsync();
        Assert.False(vm.HasPreview);
        Assert.False(vm.HasCreatedIssue);
        Assert.Equal("Migrate alerts", vm.Summary);
        Assert.Contains("outcome is unknown", vm.Status);
        Assert.Contains("check Jira", vm.AttemptNotice);
        await vm.ApproveAndCreateAsync();
        Assert.Equal(1, service.Creates);
    }

    [Fact]
    public void FieldEditorsConvertTextChoicesUsersAndArraysToJiraShapes()
    {
        var option = new JiraCreateFieldViewModel(new("priority", "Priority", "priority", false, false, "[{\"id\":\"2\",\"name\":\"High\"}]"), null);
        option.SelectedOption = option.Options[1];
        Assert.Equal("2", option.GetValue()!["id"]!.GetValue<string>());
        var user = new JiraCreateFieldViewModel(new("assignee", "Assignee", "user", false, false), null) { Value = "account-id" };
        Assert.Equal("account-id", user.GetValue()!["accountId"]!.GetValue<string>());
        var array = new JiraCreateFieldViewModel(new("labels", "Labels", "array", false, false), null) { Value = "[\"migration\"]" };
        Assert.Equal("migration", array.GetValue()!.AsArray()[0]!.GetValue<string>());
        array.Value = "not-json";
        Assert.Throws<IntegrationException>(() => array.GetValue());
    }

    private static JiraCreateViewModel Create(DraftAgent agent, CreationService service,
        IConversationHistoryStore? history = null, IActivityLogStore? activity = null) => new(agent, service, new Settings(), history, activity);
    private static async Task<JiraCreateViewModel> ReadyAsync(CreationService service,
        IConversationHistoryStore? history = null, IActivityLogStore? activity = null)
    {
        var vm = Create(new(), service, history, activity);
        await vm.InitializeAsync();
        await vm.LoadTypesAsync();
        vm.SelectedType = vm.IssueTypes[0];
        await vm.LoadFieldsAsync();
        vm.Instructions = "Rough notes";
        await vm.RefineAsync();
        return vm;
    }

    private sealed class DraftAgent : IJiraDraftAgent
    {
        public string? Instructions { get; private set; }
        public JiraDraftContent? Current { get; private set; }
        public Task<JiraDraftContent> RefineAsync(string instructions, JiraDraftContent? currentDraft, CancellationToken cancellationToken = default)
        {
            Instructions = instructions;
            Current = currentDraft;
            return Task.FromResult(new JiraDraftContent("Migrate alerts", "Acceptance criteria: prefer Teams, assess on-call if unsupported."));
        }
    }

    private sealed class CreationService : IJiraIssueCreationService
    {
        public int Creates { get; private set; }
        public int Discards { get; private set; }
        public bool FailCreate { get; set; }
        public Task<IReadOnlyList<JiraIssueType>> GetIssueTypesAsync(string projectKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JiraIssueType>>([new("1", "Task")]);
        public Task<JiraCreationTemplate> LoadTemplateAsync(string projectKey, string issueType, string? referenceKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JiraCreationTemplate(new(projectKey, issueType, "1", [new("label", "Label", "string", false, false)]), "{}", "No reference"));
        public Task<JiraCreationPreview> PrepareAsync(JiraIssueDraft draft, string additionalFieldsJson, CancellationToken cancellationToken = default) => Task.FromResult(new JiraCreationPreview(Guid.NewGuid(), "https://site.atlassian.net", draft.Summary, "{}"));
        public Task<JiraCreatedIssue> CreateApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
        {
            Creates++;
            if (FailCreate) throw new IntegrationException("The creation outcome is unknown. Check Jira.");
            return Task.FromResult(new JiraCreatedIssue("ABC-2", new Uri("https://site.atlassian.net/browse/ABC-2")));
        }
        public void DiscardPreview(Guid previewId) => Discards++;
    }

    private sealed class MemoryHistory : IConversationHistoryStore
    {
        public List<AgentConversation> Conversations { get; } = [];
        public Task SaveAsync(AgentConversation conversation, CancellationToken cancellationToken = default)
        {
            Conversations.RemoveAll(item => item.Id == conversation.Id);
            Conversations.Add(conversation);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AgentConversation>> ListConversationsAsync(IntegrationKind? agent = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentConversation>>(Conversations);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) { Conversations.Clear(); return Task.CompletedTask; }
    }

    private sealed class MemoryActivity : IActivityLogStore
    {
        public List<(string Action, string Outcome)> Entries { get; } = [];
        public Task RecordAsync(IntegrationKind agent, string action, string outcome, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, outcome));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AgentActivity>> ListActivitiesAsync(IntegrationKind? agent = null, string? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentActivity>>([]);
        public Task ClearAsync(CancellationToken cancellationToken = default) { Entries.Clear(); return Task.CompletedTask; }
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSettings(kind, DefaultScope: "ABC"));
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

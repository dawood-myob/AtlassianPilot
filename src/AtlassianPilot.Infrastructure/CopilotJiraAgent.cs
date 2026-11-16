using System.ComponentModel;
using AtlassianPilot.Core;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AtlassianPilot.Infrastructure;

public sealed class CopilotJiraAgent(ICopilotRuntime runtime, IJiraIssueSearchService jira, IJiraIssueDetailsService details, IJiraCreateMetadataService metadata, ISettingsStore settings)
    : IJiraAgent
{
    private const string SystemMessage = "You are AtlassianPilot's Jira Agent. Use only the provided read-only Jira tools. For issue details, use get_jira_issue_details with an issue key. For searches, use search_jira_issues with valid JQL. For questions about fields available when creating an issue, use get_jira_create_fields with the project key and issue type. To propose an issue, use draft_jira_issue only when the user has supplied the project key, issue type and summary. Ask for missing information without guessing. Preserve the user's requirements in the description, including acceptance criteria. The draft is only a local preview; it cannot create an issue or validate Jira fields. Never claim an issue was created. Never invent issue facts or claim a tool was used unless it returned results. Do not attempt to modify Jira.";
    private readonly SemaphoreSlim gate = new(1, 1);
    private CopilotSession? session;
    private string? sessionModel;
    private IDisposable? toolEventSubscription;
    private volatile SearchTurn? activeTurn;
    private bool disposed;

    private sealed class SearchTurn(CancellationToken token, IProgress<JiraAgentProgress>? progress)
    {
        public CancellationToken Token { get; } = token;
        public IProgress<JiraAgentProgress>? Progress { get; } = progress;
        public JiraIssueSearchResult? Result { get; set; }
        public JiraIssueDetails? Details { get; set; }
        public JiraIssueDraft? Draft { get; set; }
        public string? Jql { get; set; }
        public string? Error { get; set; }
    }

    public async Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().Length > 4000)
            throw new IntegrationException("Enter a Jira request of 1–4000 characters.");

        await gate.WaitAsync(cancellationToken);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var turn = new SearchTurn(turnCancellation.Token, progress);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureSessionAsync(cancellationToken);
            progress?.Report(new("Copilot is considering your Jira request..."));
            activeTurn = turn;
            AssistantMessageEvent? response;
            try
            {
                response = await session!.SendAndWaitAsync(new MessageOptions { Prompt = prompt.Trim() },
                    TimeSpan.FromMinutes(2), cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                activeTurn = null;
                turnCancellation.Cancel();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await session!.AbortAsync(cleanup.Token);
                await DisposeSessionAsync();
                throw;
            }
            if (response is null)
                throw new IntegrationException("Copilot returned no response. Check its connection and try again.");
            if (turn.Result is null && turn.Details is null && turn.Draft is null && turn.Error is not null)
                throw new IntegrationException(turn.Error);
            var content = turn.Draft is not null ? "Draft prepared for review. No Jira issue has been created." :
                turn.Details is not null ? $"Jira returned details for {turn.Details.Key}." : turn.Result is not null
                    ? $"Jira returned {turn.Result.Issues.Count} issue(s) on this page."
                    : $"No Jira issue was created or modified.\n\n{response.Data?.Content ?? "Try rephrasing your request."}";
            return new(content, turn.Result, turn.Jql, turn.Details, turn.Draft);
        }
        finally
        {
            activeTurn = null;
            turnCancellation.Cancel();
            gate.Release();
        }
    }

    public async Task NewConversationAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisposeSessionAsync();
        }
        finally { gate.Release(); }
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await runtime.StartAsync(cancellationToken);
        var status = await runtime.GetStatusAsync(cancellationToken);
        if (!status.Authenticated)
        {
            await DisposeSessionAsync();
            throw new IntegrationException("Sign in to GitHub Copilot in AI Configuration before starting the Jira Agent.");
        }
        var preferences = await settings.LoadAsync(IntegrationKind.Copilot, cancellationToken);
        var model = string.IsNullOrWhiteSpace(preferences.Model) ? null : preferences.Model;
        if (session is not null && model == sessionModel) return;
        if (session is not null) await DisposeSessionAsync();
        var models = await runtime.GetModelsAsync(cancellationToken);
        if (model is not null && !models.Any(candidate => candidate.Id == model))
            throw new IntegrationException($"The saved Copilot model '{model}' is not currently available. Open Settings > AI model, select Test connection to load the model list, then choose one and save (or clear the preference to use the default).");

        var tool = CopilotTool.DefineTool(
            (Func<string, ToolInvocation, int, string?, Task<JiraIssueSearchResult>>)SearchJiraIssuesAsync,
            new CopilotToolOptions { SkipPermission = true, Idempotent = true },
            new AIFunctionFactoryOptions
            {
                Name = "search_jira_issues",
                Description = "Search the connected Jira account using read-only JQL. Returns real issue keys, summaries, statuses, assignees, priorities, issue types, and links, plus a continuation token when more results exist."
            });
        var detailsTool = CopilotTool.DefineTool(
            (Func<string, ToolInvocation, Task<string>>)GetJiraIssueDetailsAsync,
            new CopilotToolOptions { SkipPermission = true, Idempotent = true },
            new AIFunctionFactoryOptions
            {
                Name = "get_jira_issue_details",
                Description = "Retrieve and display Jira fields locally for one issue key, read-only. The full fields are not returned to the model. Nested collections may be paginated; changelog is not included."
            });
        var draftTool = CopilotTool.DefineTool(
            (Func<string, string, string, string, string?, ToolInvocation, Task<string>>)DraftJiraIssueAsync,
            new CopilotToolOptions { SkipPermission = true, Idempotent = true },
            new AIFunctionFactoryOptions
            {
                Name = "draft_jira_issue",
                Description = "Prepare a local issue draft for review. Does not access or modify Jira and does not create an issue."
            });
        var metadataTool = CopilotTool.DefineTool(
            (Func<string, string, ToolInvocation, Task<JiraCreateMetadata>>)GetCreateMetadataAsync,
            new CopilotToolOptions { SkipPermission = true, Idempotent = true },
            new AIFunctionFactoryOptions
            {
                Name = "get_jira_create_fields",
                Description = "Read Jira create-screen fields for a project and issue type, including field IDs, types, required flags and defaults. Does not create or validate an issue."
            });
        session = await runtime.CreateAgentSessionAsync([tool, detailsTool, draftTool, metadataTool], model, SystemMessage, cancellationToken);
        sessionModel = model;
        toolEventSubscription = session.On<ToolExecutionStartEvent>(evt =>
            activeTurn?.Progress?.Report(new($"Copilot is running {evt.Data.ToolName}...")));
    }

    private async Task<JiraIssueSearchResult> SearchJiraIssuesAsync(
        [Description("A valid read-only Jira Query Language expression, for example assignee = currentUser() AND status = 'In Progress' ORDER BY updated DESC.")]
        string jql,
        ToolInvocation invocation,
        [Description("Maximum number of issues to return on this page, between 1 and 100.")]
        int maxResults = 50,
        [Description("Opaque continuation token from a previous Jira search page, or null for the first page.")]
        string? nextPageToken = null)
    {
        var turn = activeTurn ?? throw new IntegrationException("This Jira request is no longer active.");
        if (session is null || invocation.SessionId != session.SessionId)
            throw new IntegrationException("This Jira request belongs to an inactive session.");
        turn.Token.ThrowIfCancellationRequested();
        turn.Progress?.Report(new("Searching Jira with a read-only JQL request..."));
        try
        {
            var result = await jira.SearchAsync(jql, maxResults, nextPageToken, turn.Token);
            turn.Result = result;
            turn.Details = null;
            turn.Draft = null;
            turn.Jql = jql;
            turn.Error = null;
            turn.Progress?.Report(new($"Jira returned {result.Issues.Count} actual issue(s).", result, jql));
            return result;
        }
        catch (IntegrationException exception)
        {
            turn.Result = null;
            turn.Details = null;
            turn.Jql = null;
            turn.Error = exception.Message;
            throw;
        }
    }

    private async Task<string> GetJiraIssueDetailsAsync(
        [Description("The Jira issue key, for example ABC-123.")] string issueKey, ToolInvocation invocation)
    {
        var turn = activeTurn ?? throw new IntegrationException("This Jira request is no longer active.");
        if (session is null || invocation.SessionId != session.SessionId)
            throw new IntegrationException("This Jira request belongs to an inactive session.");
        turn.Token.ThrowIfCancellationRequested();
        turn.Progress?.Report(new("Retrieving issue details from Jira..."));
        try
        {
            var result = await details.GetAsync(issueKey, turn.Token);
            turn.Details = result;
            turn.Result = null;
            turn.Draft = null;
            turn.Jql = null;
            turn.Error = null;
            return $"Jira issue {result.Key} was retrieved and will be displayed locally. Nested collections may be paginated; changelog is not included.";
        }
        catch (IntegrationException exception)
        {
            turn.Result = null;
            turn.Details = null;
            turn.Jql = null;
            turn.Error = exception.Message;
            throw;
        }
    }

    private async Task<JiraCreateMetadata> GetCreateMetadataAsync(
        [Description("Jira project key, for example WOR.")] string projectKey,
        [Description("Exact issue type name, for example Task.")] string issueType, ToolInvocation invocation)
    {
        var turn = activeTurn ?? throw new IntegrationException("This Jira request is no longer active.");
        if (session is null || invocation.SessionId != session.SessionId)
            throw new IntegrationException("This Jira request belongs to an inactive session.");
        turn.Token.ThrowIfCancellationRequested();
        turn.Progress?.Report(new("Reading Jira create-screen fields..."));
        try { return await metadata.GetAsync(projectKey, issueType, turn.Token); }
        catch (IntegrationException exception) { turn.Error = exception.Message; throw; }
    }

    private Task<string> DraftJiraIssueAsync(
        [Description("Jira project key explicitly supplied by the user.")] string projectKey,
        [Description("Issue type explicitly supplied by the user, such as Story, Task or Bug.")] string issueType,
        [Description("Issue summary based on the user's request.")] string summary,
        [Description("Issue description and acceptance criteria, preserving the user's requirements.")] string description,
        [Description("Parent issue or epic key, or null if not supplied.")] string? parentKey,
        ToolInvocation invocation)
    {
        var turn = activeTurn ?? throw new IntegrationException("This Jira request is no longer active.");
        if (session is null || invocation.SessionId != session.SessionId)
            throw new IntegrationException("This Jira request belongs to an inactive session.");
        turn.Token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(issueType) || string.IsNullOrWhiteSpace(summary) ||
            projectKey.Length > 255 || issueType.Length > 255 || summary.Length > 255 || description is null || description.Length > 16000 || parentKey?.Length > 255)
            throw new IntegrationException("The draft needs a project key, issue type and summary within the allowed length.");
        turn.Draft = new(projectKey.Trim(), issueType.Trim(), summary.Trim(), description.Trim(), string.IsNullOrWhiteSpace(parentKey) ? null : parentKey.Trim());
        turn.Result = null;
        turn.Details = null;
        turn.Jql = null;
        turn.Error = null;
        return Task.FromResult("Draft prepared locally for review only. No Jira issue was created or validated.");
    }

    private async Task DisposeSessionAsync()
    {
        toolEventSubscription?.Dispose();
        toolEventSubscription = null;
        if (session is not null)
        {
            await session.DisposeAsync();
            session = null;
            sessionModel = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            await DisposeSessionAsync();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}

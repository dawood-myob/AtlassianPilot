using System.Collections;
using AtlassianPilot.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;

namespace AtlassianPilot.Infrastructure;

public enum LoginStepKind { DeviceCode, Completed, PlaintextConsent, AccountSelection, Error, Unsupported }
public sealed record RuntimeLoginStep(LoginStepKind Kind, DeviceLoginPrompt? Prompt = null);
public sealed record RuntimeLoginFlow(string Id, RuntimeLoginStep Step);
public sealed record RuntimeAuthStatus(bool Authenticated, string? Login);

/// <summary>Isolates the official SDK transport for deterministic authentication tests.</summary>
public interface ICopilotRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<RuntimeAuthStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotModel>> GetModelsAsync(CancellationToken cancellationToken);
    Task<CopilotSession> CreateAgentSessionAsync(IReadOnlyList<AIFunction> tools, string? model, string systemMessage,
        CancellationToken cancellationToken);
    Task<RuntimeLoginFlow> BeginLoginAsync(CancellationToken cancellationToken);
    Task<RuntimeLoginStep> AdvanceLoginAsync(string flowId, CancellationToken cancellationToken);
    Task CancelLoginAsync(string flowId, CancellationToken cancellationToken);
    Task LogoutAsync(CancellationToken cancellationToken);
}

public sealed class CopilotRuntime : ICopilotRuntime
{
    private readonly CopilotClient client;
    private CopilotSession? authenticationSession;
    private bool started;

    public CopilotRuntime(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        client = new CopilotClient(new CopilotClientOptions
        {
            BaseDirectory = dataDirectory,
            WorkingDirectory = dataDirectory,
            LogLevel = CopilotLogLevel.None,
            UseLoggedInUser = true,
            Environment = CreateRuntimeEnvironment(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(entry => new KeyValuePair<string, string>((string)entry.Key, (string)entry.Value!)))
        });
    }

    public static Dictionary<string, string> CreateRuntimeEnvironment(IEnumerable<KeyValuePair<string, string>> inherited)
    {
        string[] allowed = ["SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PATH",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "SSL_CERT_FILE", "NODE_EXTRA_CA_CERTS"];
        var names = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in inherited)
            if (names.Contains(name)) result[name] = value;
        result["COPILOT_TELEMETRY_DISABLED"] = "true";
        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started) return;
        await client.StartAsync(cancellationToken);
        started = true;
    }

    public async Task<RuntimeAuthStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await client.GetAuthStatusAsync(cancellationToken);
        return new(status.IsAuthenticated, status.Login);
    }

    public async Task<IReadOnlyList<CopilotModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        var models = await client.ListModelsAsync(cancellationToken);
        return models.Where(model => model.Policy?.State != "disabled" && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new CopilotModel(model.Id, model.Name)).OrderBy(model => model.Name).ToArray();
    }

    #pragma warning disable GHCP001 // Permission requests are explicitly refused.
        public async Task<CopilotSession> CreateAgentSessionAsync(IReadOnlyList<AIFunction> tools, string? model,
            string systemMessage, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        if (tools.Count == 0) throw new ArgumentException("An agent session must have a registered tool.", nameof(tools));
        if (string.IsNullOrWhiteSpace(systemMessage)) throw new ArgumentException("An agent system message is required.", nameof(systemMessage));
        return await client.CreateSessionAsync(new SessionConfig
        {
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Tools = tools.Cast<AIFunctionDeclaration>().ToArray(),
            AvailableTools = tools.Select(tool => $"custom:{tool.Name}").ToArray(),
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = systemMessage },
            EnableSessionTelemetry = false,
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable())
        }, cancellationToken);
    }
#pragma warning restore GHCP001

#pragma warning disable GHCP001 // SDK account device-login is experimental; refuse unsupported steps and plaintext persistence.
    public async Task<RuntimeLoginFlow> BeginLoginAsync(CancellationToken cancellationToken)
    {
        var session = await GetAuthenticationSessionAsync(cancellationToken);
        var flow = await session.Rpc.Accounts.Login.BeginAsync(new AuthLoginBeginRequest
        {
            Kind = LoginProviderKind.GitHubDotCom,
            Method = AuthLoginMethod.DeviceCode
        }, cancellationToken);
        return new(flow.FlowId, ConvertStep(flow.Step));
    }

    public async Task<RuntimeLoginStep> AdvanceLoginAsync(string flowId, CancellationToken cancellationToken) =>
        ConvertStep(await (await GetAuthenticationSessionAsync(cancellationToken)).Rpc.Accounts.Login.AdvanceAsync(flowId, cancellationToken: cancellationToken));

    public async Task CancelLoginAsync(string flowId, CancellationToken cancellationToken) =>
        await (await GetAuthenticationSessionAsync(cancellationToken)).Rpc.Accounts.Login.CancelAsync(flowId, cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var result = await (await GetAuthenticationSessionAsync(cancellationToken)).Rpc.Accounts.SetAsync(new AuthWriteLogout(), cancellationToken);
        if (!result.Ok) throw new IntegrationException("Copilot could not sign out the active account. Check the runtime and retry.");
    }

    private static RuntimeLoginStep ConvertStep(AuthLoginStep step) => step switch
    {
        AuthLoginStepDeviceCode device => new(LoginStepKind.DeviceCode,
            new(device.UserCode, new Uri(device.VerificationUri), checked((int)device.ExpiresIn))),
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.Completed => new(LoginStepKind.Completed),
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.NeedsPlaintextConsent => new(LoginStepKind.PlaintextConsent),
        AuthLoginStepCompleted completed when completed.Result.Status == AuthLoginResultStatus.NeedsAccountSelection => new(LoginStepKind.AccountSelection),
        AuthLoginStepError => new(LoginStepKind.Error),
        _ => new(LoginStepKind.Unsupported)
    };

    private async Task<CopilotSession> GetAuthenticationSessionAsync(CancellationToken cancellationToken)
    {
        authenticationSession ??= await client.CreateSessionAsync(new SessionConfig
        {
            AvailableTools = [],
            EnableSessionTelemetry = false,
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable())
        }, cancellationToken);
        return authenticationSession;
    }

    public async ValueTask DisposeAsync()
    {
        if (authenticationSession is not null) await authenticationSession.DisposeAsync();
        await client.DisposeAsync();
    }
}

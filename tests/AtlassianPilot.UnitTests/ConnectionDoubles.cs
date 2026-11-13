using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

internal sealed class FakeAtlassianConnection : IAtlassianConnectionTester
{
    public int Calls { get; private set; }
    public ConnectionResult Result { get; set; } = new(true, "Verified test account API.");
    public Task<ConnectionResult> TestAsync(IntegrationKind kind, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCopilotConnection : ICopilotConnection
{
    public Task<CopilotConnectionResult> TestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CopilotConnectionResult(new(false, "Test account not signed in.", ConnectionFailure.Authentication), []));
    public Task<CopilotConnectionResult> SignInAsync(IProgress<DeviceLoginPrompt> progress, CancellationToken cancellationToken = default) =>
        TestAsync(cancellationToken);
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeCopilotRuntime : ICopilotRuntime
{
    public RuntimeAuthStatus Auth { get; set; } = new(true, "test-user");
    public IReadOnlyList<CopilotModel> Models { get; set; } = [new("test-model", "Test model")];
    public RuntimeLoginStep InitialStep { get; set; } = new(LoginStepKind.DeviceCode, new("TEST-CODE", new Uri("https://github.com/login/device"), 300));
    public RuntimeLoginStep FinalStep { get; set; } = new(LoginStepKind.Completed);
    public bool BlockAdvance { get; set; }
    public int ModelCalls { get; private set; }
    public string? CancelledFlow { get; private set; }
    public bool LoggedOut { get; private set; }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<RuntimeAuthStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Auth);
    public Task<IReadOnlyList<CopilotModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        ModelCalls++;
        return Task.FromResult(Models);
    }
    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction>? AgentTools { get; private set; }
    public string? AgentModel { get; private set; }
    public string? AgentSystemMessage { get; private set; }
    public Task<GitHub.Copilot.CopilotSession> CreateAgentSessionAsync(IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools, string? model, string systemMessage, CancellationToken cancellationToken)
    {
        AgentTools = tools;
        AgentModel = model;
        AgentSystemMessage = systemMessage;
        throw new IntegrationException("Test session intentionally unavailable.");
    }
    public Task<RuntimeLoginFlow> BeginLoginAsync(CancellationToken cancellationToken) => Task.FromResult(new RuntimeLoginFlow("owned-flow", InitialStep));
    public async Task<RuntimeLoginStep> AdvanceLoginAsync(string flowId, CancellationToken cancellationToken)
    {
        if (BlockAdvance) await Task.Delay(Timeout.Infinite, cancellationToken);
        return FinalStep;
    }
    public Task CancelLoginAsync(string flowId, CancellationToken cancellationToken)
    {
        CancelledFlow = flowId;
        return Task.CompletedTask;
    }
    public Task LogoutAsync(CancellationToken cancellationToken)
    {
        LoggedOut = true;
        Auth = new(false, null);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

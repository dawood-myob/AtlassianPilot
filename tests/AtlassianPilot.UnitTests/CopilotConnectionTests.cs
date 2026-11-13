using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class CopilotConnectionTests
{
    [Fact]
    public async Task UnauthenticatedRuntimeDoesNotListModelsOrClaimSuccess()
    {
        var runtime = new FakeCopilotRuntime { Auth = new(false, null) };
        var result = await new CopilotConnection(runtime).TestAsync();
        Assert.False(result.Connection.Success);
        Assert.Equal(ConnectionFailure.Authentication, result.Connection.Failure);
        Assert.Equal(0, runtime.ModelCalls);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task ReturnsOnlyRuntimeModelsAndVerifiedIdentity()
    {
        var runtime = new FakeCopilotRuntime();
        var result = await new CopilotConnection(runtime).TestAsync();
        Assert.True(result.Connection.Success);
        Assert.Equal("test-user", result.Login);
        Assert.Equal("test-model", Assert.Single(result.Models).Id);
        Assert.Contains("not tested", result.Connection.Message);
    }

    [Fact]
    public async Task NoEnabledModelsIsAnExplicitPermissionFailure()
    {
        var runtime = new FakeCopilotRuntime { Models = [] };
        var result = await new CopilotConnection(runtime).TestAsync();
        Assert.False(result.Connection.Success);
        Assert.Equal(ConnectionFailure.Permission, result.Connection.Failure);
    }

    [Fact]
    public async Task DeviceAuthorizationShowsCodeAndCompletesWithActualStatus()
    {
        var runtime = new FakeCopilotRuntime();
        DeviceLoginPrompt? shown = null;
        var result = await new CopilotConnection(runtime).SignInAsync(new ImmediateProgress<DeviceLoginPrompt>(prompt => shown = prompt));
        Assert.True(result.Connection.Success);
        Assert.Equal("TEST-CODE", shown!.UserCode);
        Assert.Equal("github.com", shown.VerificationUri.Host);
        Assert.Null(runtime.CancelledFlow);
    }

    [Theory]
    [InlineData(LoginStepKind.PlaintextConsent)]
    [InlineData(LoginStepKind.AccountSelection)]
    [InlineData(LoginStepKind.Error)]
    [InlineData(LoginStepKind.Unsupported)]
    public async Task UnsafeOrUnsupportedLoginIsCancelledAndNeverClaimsSuccess(LoginStepKind final)
    {
        var runtime = new FakeCopilotRuntime { FinalStep = new(final) };
        await Assert.ThrowsAsync<IntegrationException>(() => new CopilotConnection(runtime).SignInAsync(new ImmediateProgress<DeviceLoginPrompt>(_ => { })));
        Assert.Equal("owned-flow", runtime.CancelledFlow);
        Assert.Equal(0, runtime.ModelCalls);
    }

    [Fact]
    public async Task NonGitHubDeviceUrlIsRejectedBeforeShowingCode()
    {
        var runtime = new FakeCopilotRuntime
        {
            InitialStep = new(LoginStepKind.DeviceCode, new("TEST-CODE", new Uri("https://untrusted.example/login"), 300))
        };
        var shown = false;
        await Assert.ThrowsAsync<IntegrationException>(() => new CopilotConnection(runtime).SignInAsync(new ImmediateProgress<DeviceLoginPrompt>(_ => shown = true)));
        Assert.False(shown);
        Assert.Equal("owned-flow", runtime.CancelledFlow);
    }

    [Fact]
    public async Task CancellingAuthorizationCancelsTheOwnedRuntimeFlow()
    {
        var runtime = new FakeCopilotRuntime { BlockAdvance = true };
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress<DeviceLoginPrompt>(_ => cancel.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CopilotConnection(runtime).SignInAsync(progress, cancel.Token));
        Assert.Equal("owned-flow", runtime.CancelledFlow);
        Assert.Equal(0, runtime.ModelCalls);
    }

    [Fact]
    public async Task SignOutUsesRuntimeAndDoesNotLeaveVerifiedAuthentication()
    {
        var runtime = new FakeCopilotRuntime();
        var connection = new CopilotConnection(runtime);
        await connection.SignOutAsync();
        Assert.True(runtime.LoggedOut);
        Assert.False((await connection.TestAsync()).Connection.Success);
    }
}

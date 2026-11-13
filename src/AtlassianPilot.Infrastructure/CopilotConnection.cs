using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class CopilotConnection(ICopilotRuntime runtime) : ICopilotConnection
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CopilotConnectionResult> TestAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await CheckAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<CopilotConnectionResult> SignInAsync(IProgress<DeviceLoginPrompt> progress, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        string? flowId = null;
        var complete = false;
        try
        {
            await runtime.StartAsync(cancellationToken);
            var flow = await runtime.BeginLoginAsync(cancellationToken);
            flowId = flow.Id;
            var step = flow.Step;
            if (step.Kind == LoginStepKind.DeviceCode)
            {
                var prompt = step.Prompt ?? throw new IntegrationException("GitHub did not provide a device code.");
                if (prompt.VerificationUri.Scheme != "https" || prompt.VerificationUri.Host != "github.com" ||
                    prompt.VerificationUri.UserInfo.Length != 0 || prompt.ExpiresInSeconds <= 0)
                    throw new IntegrationException("GitHub returned an unsupported sign-in URL or expiry.");
                progress.Report(prompt);
                using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                expiry.CancelAfter(TimeSpan.FromSeconds(Math.Min(prompt.ExpiresInSeconds, 900)));
                try { step = await runtime.AdvanceLoginAsync(flow.Id, expiry.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IntegrationException("The sign-in code expired. Start sign-in again.");
                }
            }
            if (step.Kind == LoginStepKind.PlaintextConsent)
                throw new IntegrationException("GitHub could not securely store the credential. Plaintext storage was refused. Check Windows Credential Manager access with IT.");
            if (step.Kind != LoginStepKind.Completed)
                throw new IntegrationException("GitHub sign-in did not complete. This release supports a single github.com account using device authorization. Check Copilot access and try again.");
            complete = true;
            return await CheckAsync(cancellationToken);
        }
        finally
        {
            try
            {
                if (flowId is not null && !complete)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await runtime.CancelLoginAsync(flowId, cleanup.Token);
                }
            }
            finally { gate.Release(); }
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await runtime.StartAsync(cancellationToken);
            await runtime.LogoutAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<CopilotConnectionResult> CheckAsync(CancellationToken cancellationToken)
    {
        await runtime.StartAsync(cancellationToken);
        var status = await runtime.GetStatusAsync(cancellationToken);
        if (!status.Authenticated)
            return new(new(false, "Not signed in. Use Sign in with GitHub to authorize this app's Copilot runtime.", ConnectionFailure.Authentication), []);
        var models = await runtime.GetModelsAsync(cancellationToken);
        if (models.Count == 0)
            return new(new(false, "Signed in, but no enabled models were returned. Check your Copilot subscription and organization policy.", ConnectionFailure.Permission), [], status.Login);
        return new(new(true, "Copilot authentication and model catalog verified. Model inference and Atlassian agent tools are not tested by this check."), models, status.Login);
    }
}

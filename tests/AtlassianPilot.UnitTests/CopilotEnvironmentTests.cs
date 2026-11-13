using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class CopilotEnvironmentTests
{
    [Fact]
    public void RuntimeReceivesSystemAndNetworkSettingsButNotInheritedSecretsOrTlsBypass()
    {
        var inherited = new Dictionary<string, string>
        {
            ["SystemRoot"] = "windows-root",
            ["Path"] = "system-path",
            ["https_proxy"] = "proxy-setting",
            ["NO_PROXY"] = "local-setting",
            ["NODE_EXTRA_CA_CERTS"] = "trusted-certificate-path",
            ["COPILOT_GITHUB_TOKEN"] = "secret-value",
            ["COPILOT_SDK_AUTH_TOKEN"] = "secret-value",
            ["GH_TOKEN"] = "secret-value",
            ["COPILOT_DISABLE_KEYTAR"] = "1",
            ["NODE_TLS_REJECT_UNAUTHORIZED"] = "0",
            ["COPILOT_RUNTIME_PROCESS_FILE_LOGGING"] = "1",
            ["COPILOT_TELEMETRY_DISABLED"] = "false"
        };

        var result = CopilotRuntime.CreateRuntimeEnvironment(inherited);

        Assert.Equal("windows-root", result["SYSTEMROOT"]);
        Assert.Equal("system-path", result["PATH"]);
        Assert.Equal("proxy-setting", result["HTTPS_PROXY"]);
        Assert.Equal("local-setting", result["NO_PROXY"]);
        Assert.Equal("trusted-certificate-path", result["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal("true", result["COPILOT_TELEMETRY_DISABLED"]);
        Assert.DoesNotContain("COPILOT_GITHUB_TOKEN", result.Keys);
        Assert.DoesNotContain("COPILOT_SDK_AUTH_TOKEN", result.Keys);
        Assert.DoesNotContain("GH_TOKEN", result.Keys);
        Assert.DoesNotContain("COPILOT_DISABLE_KEYTAR", result.Keys);
        Assert.DoesNotContain("NODE_TLS_REJECT_UNAUTHORIZED", result.Keys);
        Assert.DoesNotContain("COPILOT_RUNTIME_PROCESS_FILE_LOGGING", result.Keys);
    }
}

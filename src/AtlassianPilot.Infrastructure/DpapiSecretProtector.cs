using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AtlassianPilot.Credentials.v1");

    public byte[] Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try { return ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(byte[] encryptedSecret)
    {
        var bytes = ProtectedData.Unprotect(encryptedSecret, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

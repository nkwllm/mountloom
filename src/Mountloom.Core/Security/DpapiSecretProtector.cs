using System.Security.Cryptography;
using System.Text;

namespace Mountloom.Core.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    // Changing this legacy identifier would make existing configuration and cache credentials unreadable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mountly.Config.Credentials.v1");

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mountloom credentials require Windows CurrentUser DPAPI.");
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mountloom credentials require Windows CurrentUser DPAPI.");
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}

namespace Mountloom.Core.Security;

/// <summary>Protects bytes for local persistence. Implementations must fail closed.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

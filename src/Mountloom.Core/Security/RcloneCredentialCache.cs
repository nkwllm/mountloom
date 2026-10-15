using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mountloom.Core.Security;

/// <summary>Preserves rclone's random obscured values so unchanged credentials keep the same VFS cache identity.</summary>
public sealed class RcloneCredentialCache(string directory, ISecretProtector? protector = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), ".mountly-credentials.dpapi");
    private readonly ISecretProtector _protector = protector ?? new DpapiSecretProtector();

    public async Task<string> GetOrCreateAsync(string credential, Func<Task<string>> create,
        CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> values = [];
            if (File.Exists(_path))
            {
                try
                {
                    var clear = _protector.Unprotect(await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false));
                    try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(clear) ?? throw new JsonException(); }
                    finally { CryptographicOperations.ZeroMemory(clear); }
                }
                catch (Exception error) when (error is CryptographicException or JsonException)
                {
                    throw new InvalidOperationException("The protected rclone credential cache cannot be read. Restore it before mounting; regenerating it could disconnect pending uploads from their cache.", error);
                }
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
            if (values.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing)) return existing;
            var obscured = await create().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(obscured)) throw new InvalidOperationException("An empty obscured credential cannot be cached.");
            values.Add(key, obscured);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
            byte[] encrypted;
            try { encrypted = _protector.Protect(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return obscured;
        }
        finally { gate.Release(); }
    }
}

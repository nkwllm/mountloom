using System.Security.Cryptography;
using System.Text.Json;
using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

/// <summary>Prevents a configuration change from stranding dirty files in a different rclone cache identity.</summary>
public sealed class VfsCacheSafetyGuard
{
    public const string ManifestFileName = ".mountly-vfs-identity.json";

    public async Task ValidateAndRecordAsync(string cacheDirectory, MountConnection connection,
        RcloneLaunchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        // Serialize the read/check/replace transaction across app instances as well as tasks.
        await using var lease = await AcquireLeaseAsync(Path.Combine(directory, ".mountly-vfs-identity.lock"), cancellationToken);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var fingerprint = Fingerprint(connection, configuration);
        var previous = await ReadFingerprintAsync(manifestPath, cancellationToken);
        var dirty = await HasDirtyMetadataAsync(Path.Combine(directory, "vfsMeta"), cancellationToken);
        if (dirty && previous != fingerprint)
        {
            throw new InvalidOperationException(previous is null
                ? "This cache contains pending writes without a matching Mountloom cache identity. Keep the cache intact and recover it with the previous application version and original connection settings before continuing."
                : "This cache contains pending writes for different connection or cache settings. Restore the original settings and finish uploading before changing them. Keep all cache files intact.");
        }
        if (previous == fingerprint) return;
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Only a digest is persisted; credentials and remote targets stay out of this file.
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new Manifest(1, fingerprint)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static string Fingerprint(MountConnection connection, RcloneLaunchConfiguration configuration)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RemoteTarget = RcloneArguments.GetRemoteTarget(connection),
            connection.CacheMode,
            connection.ReadOnly,
            Environment = configuration.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Key = pair.Key.ToUpperInvariant(), pair.Value }).ToArray()
        });
        try { return Convert.ToHexString(SHA256.HashData(serialized)); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    private static async Task<string?> ReadFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, cancellationToken));
            return manifest is { Version: 1, Fingerprint.Length: 64 }
                && manifest.Fingerprint.All(char.IsAsciiHexDigit) ? manifest.Fingerprint : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<bool> HasDirtyMetadataAsync(string metadataDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(metadataDirectory)) return false;
        var dirty = false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(metadataDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var json = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken);
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("Dirty", out var value)
                    || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("The metadata does not contain a valid Dirty flag.");
                dirty |= value.GetBoolean();
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // rclone may delete a cached file whose metadata cannot be parsed. Never let
            // a launch silently discard potentially dirty data, even with the same identity.
            throw new InvalidOperationException("VFS cache metadata could not be verified. Keep all cache files intact and repair or recover the cache before mounting.", error);
        }
        return dirty;
    }

    private static async Task<FileStream> AcquireLeaseAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(25, cancellationToken); }
        }
    }

    private sealed record Manifest(int Version, string Fingerprint);
}

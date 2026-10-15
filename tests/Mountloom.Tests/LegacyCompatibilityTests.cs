using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mountloom.Core.Configuration;
using Mountloom.Core.Models;
using Mountloom.Core.Security;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

/// <summary>These historical identifiers must not follow future product-name changes.</summary>
public sealed class LegacyCompatibilityTests : IDisposable
{
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("Mountly.Config.Credentials.v1");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MountloomCompatibilityTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultLocationsKeepExistingUserDataInPlace()
    {
        // Constructors only: never load, create, or modify the user's real directories.
        var store = new ConfigStore();
        var settings = new AppSettings();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mountly", "config.json"), store.ConfigPath);
        Assert.Equal(store.ConfigPath + ".bak", store.BackupPath);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mountly", "Cache"), settings.CacheRoot);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mountly", "Logs"), settings.LogRoot);
        var connection = new MountConnection { Id = "legacy-connection" };
        Assert.Equal(Path.Combine(settings.CacheRoot, connection.Id), RcloneArguments.GetCacheDirectory(connection, settings));
    }

    [Fact]
    public async Task OldProtectedConfigurationLoadsAndSavesWithTheOriginalDpapiIdentity()
    {
        Directory.CreateDirectory(_directory);
        var credentials = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Secret = "synthetic-legacy-password", S3AccessKeyId = "", SftpKeyPassphrase = ""
        });
        var protectedCredentials = ProtectedData.Protect(credentials, LegacyEntropy, DataProtectionScope.CurrentUser);
        var original = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            App = new { CacheRoot = Path.Combine(_directory, "cache"), LogRoot = Path.Combine(_directory, "logs") },
            Connections = new[]
            {
                new
                {
                    Settings = new { Id = "legacy-connection", Name = "Existing connection", Host = "https://example.test/dav" },
                    ProtectedCredentials = Convert.ToBase64String(protectedCredentials)
                }
            }
        });
        var store = new ConfigStore(_directory);
        await File.WriteAllTextAsync(store.ConfigPath, original);

        var loaded = await store.LoadAsync();
        Assert.Equal("synthetic-legacy-password", Assert.Single(loaded.Connections).Secret);
        Assert.Equal("legacy-connection", loaded.Connections[0].Id);
        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));
        loaded.Connections[0].Name = "Edited in Mountloom";
        await store.SaveAsync(loaded);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.ConfigPath));
        var stored = document.RootElement.GetProperty("Connections")[0].GetProperty("ProtectedCredentials").GetString()!;
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(stored), LegacyEntropy, DataProtectionScope.CurrentUser);
        using var decoded = JsonDocument.Parse(clear);
        Assert.Equal("synthetic-legacy-password", decoded.RootElement.GetProperty("Secret").GetString());
        Assert.Equal(original, await File.ReadAllTextAsync(store.BackupPath));
    }

    [Fact]
    public async Task OldCredentialRepresentationAndDirtyVfsIdentityRemainUsable()
    {
        Directory.CreateDirectory(_directory);
        const string password = "synthetic-legacy-password";
        const string obscured = "original-obscured-value";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        var oldCache = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [digest] = obscured });
        var cachePath = Path.Combine(_directory, ".mountly-credentials.dpapi");
        var encrypted = ProtectedData.Protect(oldCache, LegacyEntropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(cachePath, encrypted);
        var connection = new MountConnection
        {
            Id = "legacy", Host = "https://example.test/dav", Username = "user", Secret = password,
            RemotePath = "folder", CacheDirectory = _directory
        };
        var writer = new RcloneConfigWriter((_, _, _) => throw new InvalidOperationException("Existing obscured credentials must not be regenerated."));
        var launch = await writer.CreateAsync(connection, "unused-rclone.exe", credentialCacheDirectory: _directory);
        Assert.Equal("mountly_legacy", launch.RemoteName);
        Assert.Equal(obscured, launch.Environment["RCLONE_CONFIG_MOUNTLY_LEGACY_PASS"]);
        Assert.Equal(encrypted, await File.ReadAllBytesAsync(cachePath));

        // Recorded schema-1 digest for this legacy remote, full cache mode and the five WebDAV environment fields.
        const string manifest = "{\"Version\":1,\"Fingerprint\":\"A01122AD9395972EE463C85D3D853E56E019989094ADD757D5702B6F4D81B70A\"}";
        var manifestPath = Path.Combine(_directory, ".mountly-vfs-identity.json");
        await File.WriteAllTextAsync(manifestPath, manifest);
        var metadataPath = Path.Combine(_directory, "vfsMeta", "legacy-remote", "pending.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllTextAsync(metadataPath, "{\"Dirty\":true}");

        await new VfsCacheSafetyGuard().ValidateAndRecordAsync(_directory, connection, launch);

        Assert.Equal(manifest, await File.ReadAllTextAsync(manifestPath));
        Assert.Equal("{\"Dirty\":true}", await File.ReadAllTextAsync(metadataPath));
        Assert.True(File.Exists(Path.Combine(_directory, ".mountly-vfs-identity.lock")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

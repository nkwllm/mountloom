using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mountloom.Core.Configuration;
using Mountloom.Core.Models;
using Mountloom.Core.Security;
using Xunit;

namespace Mountloom.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mountloom.ConfigStoreTests", Guid.NewGuid().ToString("N"));
    private readonly TestProtector _protector = new();

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SecretsRoundTripWithoutPlaintextInPrimaryOrBackup()
    {
        var store = CreateStore();
        var config = ExampleConfig();
        await store.SaveAsync(config);
        config.Connections[0].Name = "Updated name";
        await store.SaveAsync(config);

        foreach (var path in new[] { store.ConfigPath, store.BackupPath })
        {
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("secret-for-test", json);
            Assert.DoesNotContain("access-key-for-test", json);
            Assert.DoesNotContain("key-passphrase-for-test", json);
            var document = JsonNode.Parse(json)!;
            Assert.Equal(ConfigStore.CurrentSchemaVersion, document["SchemaVersion"]!.GetValue<int>());
            var settings = document["Connections"]![0]!["Settings"]!.AsObject();
            Assert.False(settings.ContainsKey("Secret"));
            Assert.False(settings.ContainsKey("S3AccessKeyId"));
            Assert.False(settings.ContainsKey("SftpKeyPassphrase"));
            Assert.Equal(string.Empty, settings["Username"]!.GetValue<string>());
        }

        var loaded = await CreateStore().LoadAsync();
        Assert.Equal("secret-for-test", loaded.Connections[0].Secret);
        Assert.Equal("access-key-for-test", loaded.Connections[0].S3AccessKeyId);
        Assert.Equal("key-passphrase-for-test", loaded.Connections[0].SftpKeyPassphrase);
        Assert.Equal("Updated name", loaded.Connections[0].Name);
        Assert.True(loaded.App.StartWithWindows);
        Assert.False(loaded.App.MinimizeToTray);
        Assert.Equal(1234, loaded.App.MaxLogLines);
    }

    [Fact]
    public async Task LegacyMigrationEncryptsBothCopiesAndOnlyDeletesGeneratedConfigs()
    {
        var store = CreateStore();
        var config = ExampleConfig();
        var plaintext = JsonSerializer.Serialize(config);
        await File.WriteAllTextAsync(store.ConfigPath, plaintext);
        await File.WriteAllTextAsync(store.BackupPath, plaintext);
        var rcloneDirectory = Path.Combine(_directory, "rclone");
        Directory.CreateDirectory(rcloneDirectory);
        var generated = Path.Combine(rcloneDirectory, config.Connections[0].Id + ".conf");
        await File.WriteAllTextAsync(generated, $"[mountly_{config.Connections[0].Id}]\nsecret_access_key = old-secret");
        var unrelated = Path.Combine(rcloneDirectory, "custom.conf");
        await File.WriteAllTextAsync(unrelated, "[custom]\npassword = keep");
        var pendingCache = Path.Combine(_directory, "Cache", "pending-upload");
        Directory.CreateDirectory(Path.GetDirectoryName(pendingCache)!);
        await File.WriteAllTextAsync(pendingCache, "Pending user data");

        var loaded = await store.LoadAsync();

        Assert.Equal("secret-for-test", loaded.Connections[0].Secret);
        Assert.Contains("migrated", store.LastLoadWarning!);
        Assert.DoesNotContain("secret-for-test", await File.ReadAllTextAsync(store.ConfigPath));
        Assert.DoesNotContain("secret-for-test", await File.ReadAllTextAsync(store.BackupPath));
        Assert.False(File.Exists(generated));
        var archived = Assert.Single(Directory.GetFiles(Path.Combine(rcloneDirectory, "recovery"), "*.conf.dpapi"));
        Assert.Equal($"[mountly_{config.Connections[0].Id}]\nsecret_access_key = old-secret",
            Encoding.UTF8.GetString(_protector.Unprotect(await File.ReadAllBytesAsync(archived))));
        Assert.DoesNotContain("old-secret", await File.ReadAllTextAsync(archived));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("Pending user data", await File.ReadAllTextAsync(pendingCache));
    }

    [Fact]
    public async Task LegacyMigrationRetainsThePreviousBackupInsteadOfReplacingItWithTheCurrentVersion()
    {
        var store = CreateStore();
        var previous = ExampleConfig();
        previous.Connections[0].Name = "Previous backup version";
        var current = ExampleConfig();
        current.Connections[0].Name = "Current primary version";
        await File.WriteAllTextAsync(store.ConfigPath, JsonSerializer.Serialize(current));
        await File.WriteAllTextAsync(store.BackupPath, JsonSerializer.Serialize(previous));

        Assert.Equal("Current primary version", (await store.LoadAsync()).Connections[0].Name);
        Assert.DoesNotContain("secret-for-test", await File.ReadAllTextAsync(store.BackupPath));
        await File.WriteAllTextAsync(store.ConfigPath, "simulate later corruption");

        Assert.Equal("Previous backup version", (await CreateStore().LoadAsync()).Connections[0].Name);
    }

    [Theory]
    [InlineData("ignored-bucket", "/actual-bucket/folder/", "actual-bucket", "folder")]
    [InlineData("actual-bucket", "actual-bucket/folder", "actual-bucket", "folder")]
    [InlineData("", "actual-bucket", "actual-bucket", "")]
    public async Task LegacyS3MigrationPreservesThePreviouslyMountedTarget(string oldHost, string oldPath, string expectedBucket, string expectedPrefix)
    {
        var store = CreateStore();
        var legacy = ExampleConfig();
        legacy.Connections[0].Host = oldHost;
        legacy.Connections[0].RemotePath = oldPath;
        legacy.Connections[0].AutoMount = true;
        await File.WriteAllTextAsync(store.ConfigPath, JsonSerializer.Serialize(legacy));

        var loaded = await store.LoadAsync();

        Assert.Equal(expectedBucket, loaded.Connections[0].Host);
        Assert.Equal(expectedPrefix, loaded.Connections[0].RemotePath);
        Assert.True(loaded.Connections[0].AutoMount);
    }

    [Fact]
    public async Task AmbiguousLegacyS3RootRequiresReviewBeforeAutomaticMounting()
    {
        var store = CreateStore();
        var legacy = ExampleConfig();
        legacy.Connections[0].Host = "previously-ignored-bucket";
        legacy.Connections[0].RemotePath = "/";
        legacy.Connections[0].AutoMount = true;
        await File.WriteAllTextAsync(store.ConfigPath, JsonSerializer.Serialize(legacy));

        var loaded = await store.LoadAsync();

        Assert.Equal("previously-ignored-bucket", loaded.Connections[0].Host);
        Assert.False(loaded.Connections[0].AutoMount);
        Assert.Contains("Review the migrated S3 bucket", store.LastLoadWarning!);
        Assert.False((await CreateStore().LoadAsync()).Connections[0].AutoMount);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversBackupAndPreservesDamagedBytesUnderProtection()
    {
        var store = CreateStore();
        var config = ExampleConfig();
        config.Connections[0].Name = "Previous good value";
        await store.SaveAsync(config);
        config.Connections[0].Name = "Latest value";
        await store.SaveAsync(config);
        const string damaged = "{ damaged plaintext-secret";
        await File.WriteAllTextAsync(store.ConfigPath, damaged);

        var recoveredStore = CreateStore();
        var recovered = await recoveredStore.LoadAsync();

        Assert.Equal("Previous good value", recovered.Connections[0].Name);
        Assert.True(recoveredStore.CanSave);
        Assert.Contains("recovered", recoveredStore.LastLoadWarning!);
        var preserved = Assert.Single(Directory.GetFiles(_directory, "config.corrupt.*.protected"));
        Assert.Equal(damaged, Encoding.UTF8.GetString(_protector.Unprotect(await File.ReadAllBytesAsync(preserved))));
        Assert.DoesNotContain("plaintext-secret", await File.ReadAllTextAsync(preserved));
        Assert.Equal("Previous good value", (await CreateStore().LoadAsync()).Connections[0].Name);
    }

    [Fact]
    public async Task UnrecoverableLoadBlocksAllSavingAndPreservesBothFiles()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(store.ConfigPath, "broken primary");
        await File.WriteAllTextAsync(store.BackupPath, "broken backup");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new MountloomConfig()));

        Assert.False(store.CanSave);
        Assert.Equal("broken primary", await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal("broken backup", await File.ReadAllTextAsync(store.BackupPath));
    }

    [Fact]
    public async Task FutureSchemaNeverFallsBackToOlderBackup()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        await store.SaveAsync(ExampleConfig());
        const string newer = "{\"SchemaVersion\":999,\"FutureLayout\":{}}";
        await File.WriteAllTextAsync(store.ConfigPath, newer);
        var backupBefore = await File.ReadAllTextAsync(store.BackupPath);
        var reader = CreateStore();

        await Assert.ThrowsAsync<UnsupportedConfigVersionException>(() => reader.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SaveAsync(new MountloomConfig()));

        Assert.Equal(newer, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal(backupBefore, await File.ReadAllTextAsync(store.BackupPath));
    }

    [Fact]
    public async Task ExistingConfigurationMustBeLoadedBeforeSaving()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        var before = await File.ReadAllTextAsync(store.ConfigPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStore().SaveAsync(new MountloomConfig()));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task ProtectionFailureLeavesPreviousConfigurationIntact()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        var before = await File.ReadAllTextAsync(store.ConfigPath);
        _protector.FailProtection = true;

        await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync(ExampleConfig()));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CanceledSaveKeepsPreviouslySavedData()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        var before = await File.ReadAllTextAsync(store.ConfigPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new MountloomConfig(), cancellation.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task FailedAtomicReplaceDoesNotTruncatePreviousConfiguration()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        var before = await File.ReadAllTextAsync(store.ConfigPath);
        using (var locked = new FileStream(store.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(ExampleConfig()));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CredentialsThatCannotBeDecryptedNeverBecomeAnEmptySavedConfiguration()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        await store.SaveAsync(ExampleConfig());
        var before = await File.ReadAllTextAsync(store.ConfigPath);
        _protector.FailUnprotect = true;
        var reader = CreateStore();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SaveAsync(new MountloomConfig()));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task ConcurrentSavesAlwaysLeaveTwoCompleteDocuments()
    {
        var store = CreateStore();
        await store.SaveAsync(ExampleConfig());
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
        {
            var config = ExampleConfig();
            config.Connections[0].Name = $"Save {index}";
            return store.SaveAsync(config);
        }));

        var latest = await CreateStore().LoadAsync();
        Assert.StartsWith("Save ", latest.Connections[0].Name);
        await File.WriteAllTextAsync(store.ConfigPath, "broken");
        var previous = await CreateStore().LoadAsync();
        Assert.StartsWith("Save ", previous.Connections[0].Name);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task InvalidDuplicateIdsAreRejectedWithoutOverwritingGoodConfig()
    {
        var store = CreateStore();
        var config = ExampleConfig();
        await store.SaveAsync(config);
        var before = await File.ReadAllTextAsync(store.ConfigPath);
        config.Connections.Add(config.Connections[0].Clone());

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(config));

        Assert.Equal(before, await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task InvalidLegacyIdentityDoesNotGenerateANewIdAndOverwriteTheFile()
    {
        var store = CreateStore();
        const string invalid = "{\"App\":{},\"Connections\":[{\"Name\":\"Missing ID\"}]}";
        await File.WriteAllTextAsync(store.ConfigPath, invalid);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.False(store.CanSave);
        Assert.Equal(invalid, await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task IncompleteConnectionDraftCanBeSaved()
    {
        var store = CreateStore();
        var config = new MountloomConfig { Connections = [new MountConnection { Host = "", DriveLetter = "", RemotePath = "" }] };

        await store.SaveAsync(config);

        Assert.Single((await store.LoadAsync()).Connections);
    }

    [Fact]
    public void CurrentUserDpapiRoundTripProtectsActualBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiSecretProtector();
        var input = Encoding.UTF8.GetBytes("local temporary unit test secret");

        var protectedBytes = protector.Protect(input);

        Assert.False(protectedBytes.SequenceEqual(input));
        Assert.Equal(input, protector.Unprotect(protectedBytes));
        protectedBytes[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedBytes));
    }

    private ConfigStore CreateStore() => new(_directory, _protector);

    private static MountloomConfig ExampleConfig() => new()
    {
        App = new AppSettings { StartWithWindows = true, MinimizeToTray = false, MaxLogLines = 1234 },
        Connections =
        [
            new MountConnection
            {
                Name = "Original name", Protocol = MountProtocol.S3,
                Secret = "secret-for-test", S3AccessKeyId = "access-key-for-test",
                Username = "access-key-for-test", SftpKeyPassphrase = "key-passphrase-for-test"
            }
        ]
    };

    public void Dispose()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Mountloom.ConfigStoreTests"));
        var actualParent = Path.GetDirectoryName(Path.GetFullPath(_directory));
        if (!string.Equals(expectedParent, actualParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // A deterministic test double, never used by the application; real DPAPI is tested above.
    private sealed class TestProtector : ISecretProtector
    {
        public bool FailProtection { get; set; }
        public bool FailUnprotect { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            if (FailProtection) throw new CryptographicException("Injected protection failure.");
            return plaintext.Select(value => (byte)(value ^ 0xA5)).ToArray();
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (FailUnprotect) throw new CryptographicException("Injected decryption failure.");
            return ciphertext.Select(value => (byte)(value ^ 0xA5)).ToArray();
        }
    }
}

using Mountloom.Core.Models;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

public sealed class VfsCacheSafetyGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MountloomCacheGuardTests-" + Guid.NewGuid().ToString("N"));
    private readonly VfsCacheSafetyGuard _guard = new();
    private readonly MountConnection _connection = new() { Host = "https://example.test/dav", RemotePath = "/selected" };
    private static RcloneLaunchConfiguration Configuration(string password = "obscured-sensitive-value") =>
        new("test", new Dictionary<string, string> { ["RCLONE_CONFIG_TEST_TYPE"] = "webdav", ["RCLONE_CONFIG_TEST_PASS"] = password });

    [Fact]
    public async Task MatchingIdentityAllowsDirtyRecoveryWithoutChangingMetadata()
    {
        await Check();
        var path = await Metadata("{\"Dirty\":true}");
        await Check();
        Assert.Equal("{\"Dirty\":true}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ManifestContainsOnlyVersionAndDigest()
    {
        await Check();
        var manifest = await File.ReadAllTextAsync(Path.Combine(_directory, VfsCacheSafetyGuard.ManifestFileName));
        Assert.Contains("Fingerprint", manifest);
        Assert.DoesNotContain("sensitive", manifest);
        Assert.DoesNotContain("example.test", manifest);
        Assert.DoesNotContain("selected", manifest);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("path")]
    [InlineData("cache-mode")]
    [InlineData("read-only")]
    public async Task ChangedIdentityRejectsDirtyCacheWithoutChangingManifest(string change)
    {
        await Check();
        await Metadata("{\"Dirty\":true}");
        var manifestPath = Path.Combine(_directory, VfsCacheSafetyGuard.ManifestFileName);
        var original = await File.ReadAllTextAsync(manifestPath);
        var configuration = Configuration();
        switch (change)
        {
            case "password": configuration = Configuration("different-secret"); break;
            case "path": _connection.RemotePath = "/other"; break;
            case "cache-mode": _connection.CacheMode = CacheMode.Off; break;
            case "read-only": _connection.ReadOnly = true; break;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _guard.ValidateAndRecordAsync(_directory, _connection, configuration));
        Assert.Contains("pending writes", error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task MissingManifestRejectsDirtyLegacyMetadata()
    {
        await Metadata("{\"Dirty\":true}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(Check);
        Assert.Contains("previous application version", error.Message);
        Assert.False(File.Exists(Path.Combine(_directory, VfsCacheSafetyGuard.ManifestFileName)));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"Dirty\":\"false\"}")]
    public async Task UnverifiableMetadataBlocksEvenMatchingIdentity(string metadata)
    {
        await Check();
        var path = await Metadata(metadata);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(Check);
        Assert.Contains("could not be verified", error.Message);
        Assert.Equal(metadata, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CleanMetadataAllowsIdentityChange()
    {
        await Check();
        await Metadata("{\"Dirty\":false}");
        var path = Path.Combine(_directory, VfsCacheSafetyGuard.ManifestFileName);
        var original = await File.ReadAllTextAsync(path);
        _connection.RemotePath = "/changed";
        await Check();
        Assert.NotEqual(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EnvironmentOrderDoesNotChangeIdentity()
    {
        await Check();
        await Metadata("{\"Dirty\":true}");
        var reversed = new RcloneLaunchConfiguration("test", Configuration().Environment.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value));
        await _guard.ValidateAndRecordAsync(_directory, _connection, reversed);
    }

    [Fact]
    public async Task CorruptManifestCannotAuthorizeDirtyRecovery()
    {
        await Check();
        await File.WriteAllTextAsync(Path.Combine(_directory, VfsCacheSafetyGuard.ManifestFileName), "broken");
        await Metadata("{\"Dirty\":true}");
        await Assert.ThrowsAsync<InvalidOperationException>(Check);
    }

    private Task Check() => _guard.ValidateAndRecordAsync(_directory, _connection, Configuration());
    private async Task<string> Metadata(string json)
    {
        var path = Path.Combine(_directory, "vfsMeta", "remote{hash}", "nested", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
        return path;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

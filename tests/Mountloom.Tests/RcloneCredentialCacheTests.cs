using System.Text;
using Mountloom.Core.Security;

namespace Mountloom.Tests;

public sealed class RcloneCredentialCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MountloomCredentialTests-" + Guid.NewGuid().ToString("N"));
    private int _calls;
    private Task<string> Generate() => Task.FromResult("synthetic-obscured-" + ++_calls);

    [Fact]
    public async Task FreshInstanceReusesRepresentationAndRetainsPreviousCredentials()
    {
        var first = await new RcloneCredentialCache(_directory).GetOrCreateAsync("first-password", Generate);
        var second = await new RcloneCredentialCache(_directory).GetOrCreateAsync("second-password", Generate);
        Assert.Equal(first, await new RcloneCredentialCache(_directory).GetOrCreateAsync("first-password", Generate));
        Assert.NotEqual(first, second);
        Assert.Equal(2, _calls);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_directory, ".mountly-credentials.dpapi")));
        Assert.DoesNotContain("first-password", text);
        Assert.DoesNotContain(first, text);
    }

    [Fact]
    public async Task ConcurrentPreparationUsesOneRepresentation()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new RcloneCredentialCache(_directory).GetOrCreateAsync("same-password", Generate)));
        Assert.Single(results.Distinct());
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task CorruptionDoesNotSilentlyDisconnectExistingCache()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, ".mountly-credentials.dpapi");
        await File.WriteAllTextAsync(path, "corrupt synthetic data");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RcloneCredentialCache(_directory)
            .GetOrCreateAsync("password", Generate));
        Assert.Equal(0, _calls);
        Assert.Equal("corrupt synthetic data", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

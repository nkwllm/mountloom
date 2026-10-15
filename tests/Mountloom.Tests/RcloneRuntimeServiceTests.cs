using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

public sealed class RcloneRuntimeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MountloomRuntimeTests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _executable = Encoding.UTF8.GetBytes("synthetic rclone runtime; never executed");
    private readonly byte[] _archive;
    private readonly RcloneRuntimePackage _package;
    private string ApplicationDirectory => Path.Combine(_root, "app");
    private string RuntimeDirectory => Path.Combine(_root, "runtime");
    private string InstalledExecutable => Path.Combine(RuntimeDirectory, _package.Version, "rclone.exe");

    public RcloneRuntimeServiceTests()
    {
        Directory.CreateDirectory(ApplicationDirectory);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var file = zip.CreateEntry("package/rclone.exe").Open()) file.Write(_executable);
            using (var writer = new StreamWriter(zip.CreateEntry("package/README.txt").Open()))
                writer.Write("Synthetic upstream license and documentation");
            using (var writer = new StreamWriter(zip.CreateEntry("package/../../outside.txt").Open()))
                writer.Write("This entry must never be extracted.");
        }
        _archive = buffer.ToArray();
        _package = new RcloneRuntimePackage("1.75.2", new Uri("https://runtime.invalid/rclone.zip"),
            Hash(_archive), Hash(_executable), "package");
    }

    [Fact]
    public async Task ValidBundleWinsWithoutDownloadingOrCreatingManagedDirectory()
    {
        var bundled = Path.Combine(ApplicationDirectory, "rclone.exe");
        await File.WriteAllBytesAsync(bundled, _executable);
        using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected download"));
        using var http = new HttpClient(handler);
        var progress = new List<string>();
        var service = Create(http);
        service.ProgressChanged += progress.Add;

        Assert.Equal(bundled, await service.EnsureAsync());
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(RuntimeDirectory));
        Assert.Contains(progress, text => text.Contains("应用内置"));
    }

    [Fact]
    public async Task ValidManagedRuntimeIsReusedWithoutNetwork()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledExecutable)!);
        await File.WriteAllBytesAsync(InstalledExecutable, _executable);
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);

        Assert.Equal(InstalledExecutable, await Create(http).EnsureAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingRuntimeIsDownloadedValidatedAndInstalledWithoutLegacyConfiguration()
    {
        // A stale settings document is intentionally irrelevant to the runtime service.
        await File.WriteAllTextAsync(Path.Combine(ApplicationDirectory, "config.json"),
            "{\"App\":{\"RclonePath\":\"Z:\\\\missing\\\\rclone.exe\"}}");
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        var probedPaths = new List<string>();
        var service = Create(http, probe: (path, _) => { probedPaths.Add(path); return Task.FromResult(true); });

        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
        Assert.Equal(_executable, await File.ReadAllBytesAsync(InstalledExecutable));
        Assert.Contains("license", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(InstalledExecutable)!, "README.txt")));
        Assert.Single(probedPaths);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(_package.DownloadUri, handler.LastUri);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.Empty(Directory.GetFiles(_root, "outside.txt", SearchOption.AllDirectories));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task InvalidBundleIsNeverExecutedAndFallsBackToManagedRuntime()
    {
        var bundled = Path.Combine(ApplicationDirectory, "rclone.exe");
        await File.WriteAllTextAsync(bundled, "untrusted binary");
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        var probedPaths = new List<string>();
        var service = Create(http, probe: (path, _) => { probedPaths.Add(path); return Task.FromResult(true); });

        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
        Assert.DoesNotContain(bundled, probedPaths);
        Assert.Equal("untrusted binary", await File.ReadAllTextAsync(bundled));
    }

    [Fact]
    public async Task HashMismatchRejectsArchiveAndLeavesNoInstalledExecutable()
    {
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        var service = Create(http, _package with { ArchiveSha256 = new string('0', 64) });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureAsync());
        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task ExecutableHashMismatchRejectsArchiveContentBeforeProbe()
    {
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        var service = Create(http, _package with { ExecutableSha256 = new string('0', 64) },
            (_, _) => throw new Xunit.Sdk.XunitException("Untrusted executable must not be started"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.EnsureAsync());
        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task UnusableExecutableIsNotCommitted()
    {
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http, probe: (_, _) => Task.FromResult(false)).EnsureAsync());
        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task CancellationCleansStagingAndAllowsRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestNumber = 0;
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref requestNumber) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return ArchiveResponse();
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var service = Create(http);
        var pending = service.EnsureAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledDownloadBodyCanBeCancelledOrTimeOutAndRetry(bool timeOut)
    {
        var stalled = new StalledDownloadStream(_archive);
        var requestNumber = 0;
        using var handler = new StubHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref requestNumber) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }
                : ArchiveResponse()));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var service = Create(http, downloadTimeout: timeOut ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30));
        var pending = service.EnsureAsync(cancellation.Token);
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (timeOut)
            await Assert.ThrowsAsync<TimeoutException>(() => pending).WaitAsync(TimeSpan.FromSeconds(10));
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellationWhileProbingDoesNotCommitExecutableAndAllowsRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeNumber = 0;
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var service = Create(http, probe: async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref probeNumber) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return true;
        });
        var pending = service.EnsureAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(InstalledExecutable));
        AssertNoStagingFiles();
        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
    }

    [Fact]
    public async Task OfflineFailurePreservesOldFileAndCanRetryWithoutRestartingApplication()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledExecutable)!);
        await File.WriteAllTextAsync(InstalledExecutable, "old incomplete runtime");
        var requestNumber = 0;
        using var handler = new StubHandler((_, _) =>
            Interlocked.Increment(ref requestNumber) == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
                : Task.FromResult(ArchiveResponse()));
        using var http = new HttpClient(handler);
        var service = Create(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureAsync());
        Assert.Equal("old incomplete runtime", await File.ReadAllTextAsync(InstalledExecutable));
        AssertNoStagingFiles();
        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
        Assert.Equal(_executable, await File.ReadAllBytesAsync(InstalledExecutable));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentServicesShareOneInstallationAndWaitingCallerCanCancel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ArchiveResponse();
        });
        using var http = new HttpClient(handler);
        var first = Create(http).EnsureAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = Create(http).EnsureAsync(cancellation.Token);
        var otherWaiters = Enumerable.Range(0, 8).Select(_ => Create(http).EnsureAsync()).ToArray();
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter); }
        finally { release.TrySetResult(); }

        var results = await Task.WhenAll(otherWaiters.Prepend(first));
        Assert.All(results, path => Assert.Equal(InstalledExecutable, path));
        Assert.Equal(1, handler.Calls);
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task ExternalInstallationLockCanBeCancelledAndRetriedAfterRelease()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        var service = Create(http);
        using (var otherInstaller = new FileStream(Path.Combine(RuntimeDirectory, ".install.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource();
            var pending = service.EnsureAsync(cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, handler.Calls);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(InstalledExecutable, await service.EnsureAsync());
        Assert.Equal(1, handler.Calls);
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task AlreadyCancelledCallHasNoFilesystemOrNetworkEffects()
    {
        using var handler = SuccessfulHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(http).EnsureAsync(cancellation.Token));
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(RuntimeDirectory));
    }

    [Fact]
    public void DetectorDoesNotSearchForBareExecutableNames()
    {
        Assert.Equal(string.Empty, EnvironmentDetector.ResolveExecutable("rclone.exe"));
        Assert.Equal(string.Empty, EnvironmentDetector.ResolveExecutable(string.Empty));
    }

    private RcloneRuntimeService Create(HttpClient http, RcloneRuntimePackage? package = null,
        Func<string, CancellationToken, Task<bool>>? probe = null, TimeSpan? downloadTimeout = null) =>
        new(ApplicationDirectory, RuntimeDirectory, http, package ?? _package,
            probe ?? ((_, _) => Task.FromResult(true)), downloadTimeout);

    private StubHandler SuccessfulHandler() => new((_, _) => Task.FromResult(ArchiveResponse()));
    private HttpResponseMessage ArchiveResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(_archive) };
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private void AssertNoStagingFiles()
    {
        Assert.Empty(Directory.GetDirectories(RuntimeDirectory, ".install-*"));
        Assert.Empty(Directory.GetFiles(RuntimeDirectory, "*.zip", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StalledDownloadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastUri = request.RequestUri;
            return respond(request, cancellationToken);
        }
    }
}

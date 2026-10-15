using System.Diagnostics;
using System.Text.Json;
using Mountloom.Core.Models;
using Mountloom.Core.Services;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.Tests;

public sealed class MountManagerTests
{
    [Fact]
    public async Task CancelledUnmountWaiterDoesNotLeaveConnectionMarkedStopping()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var release = NewSignal();
        fixture.Configure = async (_, _, _) =>
        {
            entered.TrySetResult();
            // Deliberately retain the startup gate until this test releases it.
            await release.Task;
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var mounting = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var unmounting = manager.UnmountAsync(fixture.Connection.Id, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unmounting);
        // This would synchronously throw "connection is stopping" before the fix.
        var duplicate = manager.MountAsync(fixture.Connection, fixture.Settings);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mounting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
        Assert.Empty(manager.GetReservedDriveLetters());
        Assert.Empty(fixture.Processes);
    }

    [Fact]
    public async Task DirtyLegacyCachePreventsAnyMountProcessFromStarting()
    {
        using var fixture = new Fixture();
        var metadata = Path.Combine(fixture.Root, fixture.Connection.Id, "vfsMeta", "legacy", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, "{\"Dirty\":true}");
        var manager = fixture.CreateManager();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(fixture.Connection, fixture.Settings));
        Assert.Contains("pending writes", error.Message);
        Assert.Empty(fixture.Processes);
        Assert.Equal("{\"Dirty\":true}", await File.ReadAllTextAsync(metadata));
    }

    [Fact]
    public async Task ConcurrentMountRequestsStartOnlyOneProcess()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var release = NewSignal();
        fixture.Configure = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var first = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        var second = manager.MountAsync(fixture.Connection, fixture.Settings);
        Assert.Single(manager.GetReservedDriveLetters());
        release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Single(fixture.Processes);
        Assert.Equal(MountState.Mounted, manager.GetStatus(fixture.Connection.Id).State);
        await manager.StopAllAsync();
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
        Assert.Equal(0, fixture.Processes[0].KillCount);
    }

    [Fact]
    public async Task UnmountDuringConfigurationCancelsStartupBeforeProcessCreation()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        fixture.Configure = async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var mounting = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        await manager.UnmountAsync(fixture.Connection.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mounting);
        Assert.Empty(fixture.Processes);
        Assert.Empty(manager.GetReservedDriveLetters());
        Assert.Equal(MountState.NotMounted, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
    }

    [Fact]
    public async Task StopAllIncludesStartingTasksAndRejectsNewMountsUntilResumed()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        fixture.Configure = async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var mounting = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        await manager.StopAllAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mounting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(fixture.Connection, fixture.Settings));
        manager.ResumeOperations();
        fixture.Configure = (_, _, _) => Task.FromResult(EmptyConfiguration());
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task DriveReservationPreventsDifferentConnectionsUsingSameDriveDuringStartup()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        fixture.Configure = async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var first = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        var duplicate = fixture.Connection.Clone();
        duplicate.Id = "another";
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(duplicate, fixture.Settings));
        await manager.StopAllAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task SystemOccupiedDriveIsRejectedBeforeStartingAProcess()
    {
        using var fixture = new Fixture();
        fixture.Probe.Drives.Add("X:");
        var manager = fixture.CreateManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(fixture.Connection, fixture.Settings));
        Assert.Empty(fixture.Processes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:/mount")]
    [InlineData("XX:")]
    [InlineData("中:")]
    public void InvalidDriveLettersAreRejected(string drive)
    {
        Assert.False(DriveLetterService.IsValid(drive));
        Assert.Throws<ArgumentException>(() => DriveLetterService.Validate(drive));
    }

    [Fact]
    public async Task ARunningProcessWithoutADriveNeverBecomesMounted()
    {
        using var fixture = new Fixture { CreateDriveOnStart = false, StartupTimeout = TimeSpan.FromMilliseconds(250) };
        var manager = fixture.CreateManager();
        var states = new List<MountState>();
        manager.StatusChanged += (_, status) => states.Add(status.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(fixture.Connection, fixture.Settings));
        Assert.DoesNotContain(MountState.Mounted, states);
        Assert.Equal(MountState.Failed, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal(1, fixture.Controls[0].QuitCount);
        Assert.Equal(0, fixture.Processes[0].KillCount);
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
    }

    [Fact]
    public async Task NormalUnmountWaitsForUploadsThenQuitsUnderTheVolumeLock()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.Controls[0].Stats = IdleStats() with { UploadsQueued = 1 };
        var unmounting = manager.UnmountAsync(fixture.Connection.Id);
        await Task.Delay(20);
        Assert.False(unmounting.IsCompleted);
        Assert.Equal(0, fixture.Controls[0].QuitCount);
        fixture.Controls[0].Stats = IdleStats();
        await unmounting;
        Assert.True(fixture.Controls[0].QuitSawVolumeLock);
        Assert.Equal(0, fixture.Probe.ActiveLocks);
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
        Assert.Equal(0, fixture.Processes[0].KillCount);
        Assert.Equal(MountState.NotMounted, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShellRemovalWaitsForProcessExitAndDriveReleaseAndRunsOnce(bool force)
    {
        using var fixture = new Fixture { KeepDriveOnExit = true, DriveReleaseTimeout = TimeSpan.FromSeconds(2) };
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        var notifiedAfterExit = false;
        var notifiedAfterRelease = false;
        var notifiedWhileReserved = false;
        fixture.ShellNotifier.OnRemoved = drive =>
        {
            notifiedAfterExit = fixture.Processes[0].HasExited;
            notifiedAfterRelease = !fixture.Probe.IsDrivePresent(drive);
            notifiedWhileReserved = manager.GetReservedDriveLetters().Contains(drive);
        };

        var unmounting = manager.UnmountAsync(fixture.Connection.Id, force: force);
        await WaitForAsync(() => fixture.Processes[0].HasExited);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        Assert.False(unmounting.IsCompleted);
        fixture.Probe.Drives.Clear();
        await unmounting;
        await manager.UnmountAsync(fixture.Connection.Id, force: force);
        await manager.StopAllAsync(force: force);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
        Assert.True(notifiedAfterExit);
        Assert.True(notifiedAfterRelease);
        Assert.True(notifiedWhileReserved);
    }

    [Fact]
    public async Task ShellNotificationFailureDoesNotPreventUnmountCleanup()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.ShellNotifier.OnRemoved = _ => throw new InvalidOperationException("Shell unavailable");
        await manager.UnmountAsync(fixture.Connection.Id);
        Assert.Equal(MountState.NotMounted, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Empty(manager.GetReservedDriveLetters());
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
    }

    [Fact]
    public async Task BusyUnmountDoesNotKillAndKeepsTheMountManaged()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.Controls[0].Stats = IdleStats() with { UploadsQueued = 3, OpenFiles = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(fixture.Connection.Id));
        Assert.Equal(0, fixture.Processes[0].KillCount);
        Assert.Equal(0, fixture.Controls[0].QuitCount);
        Assert.Equal(MountState.Mounted, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal(3, manager.GetStatus(fixture.Connection.Id).PendingUploads);
        Assert.Single(manager.GetReservedDriveLetters());
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        await manager.UnmountAsync(fixture.Connection.Id, force: true);
        Assert.Equal(1, fixture.Processes[0].KillCount);
    }

    [Fact]
    public async Task UnknownRcStateDoesNotAuthorizeNormalUnmount()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.Controls[0].Stats = new MountVfsStats(null, null, null, null, null, null, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(fixture.Connection.Id));
        Assert.False(fixture.Processes[0].HasExited);
        Assert.Equal(0, fixture.Controls[0].QuitCount);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        await manager.StopAllAsync(force: true);
    }

    [Fact]
    public async Task RcFailureKeepsProcessAliveAndForceIsExplicit()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.Controls[0].Failure = new IOException("RC unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(fixture.Connection.Id));
        Assert.False(fixture.Processes[0].HasExited);
        Assert.Equal(0, fixture.Processes[0].DisposeCount);
        await manager.UnmountAsync(fixture.Connection.Id, force: true);
        Assert.Equal(1, fixture.Processes[0].KillCount);
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
    }

    [Fact]
    public async Task FailedVolumeLockKeepsTheProcessRunning()
    {
        using var fixture = new Fixture();
        fixture.Probe.LockAllowed = false;
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        await Assert.ThrowsAsync<UnmountSafetyConfirmationRequiredException>(() => manager.UnmountAsync(fixture.Connection.Id));
        Assert.Equal(0, fixture.Controls[0].QuitCount);
        Assert.False(fixture.Processes[0].HasExited);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        await manager.StopAllAsync(force: true);
    }

    [Fact]
    public async Task AcknowledgedClosedFilesAllowsCooperativeQuitWithoutForceKill()
    {
        using var fixture = new Fixture();
        fixture.Probe.LockAllowed = false;
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        await Assert.ThrowsAsync<UnmountSafetyConfirmationRequiredException>(() => manager.UnmountAsync(fixture.Connection.Id));
        await manager.UnmountAsync(fixture.Connection.Id, acknowledgeOpenFilesClosed: true);
        Assert.Equal(1, fixture.Controls[0].QuitCount);
        Assert.False(fixture.Controls[0].QuitSawVolumeLock);
        Assert.Equal(0, fixture.Processes[0].KillCount);
        Assert.Equal(MountState.NotMounted, manager.GetStatus(fixture.Connection.Id).State);
    }

    [Fact]
    public async Task ClosedFilesAcknowledgementDoesNotBypassPendingUploads()
    {
        using var fixture = new Fixture();
        fixture.Probe.LockAllowed = false;
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        fixture.Controls[0].Stats = IdleStats() with { UploadsInProgress = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(fixture.Connection.Id, acknowledgeOpenFilesClosed: true));
        Assert.Equal(0, fixture.Controls[0].QuitCount);
        Assert.Equal(0, fixture.Processes[0].KillCount);
        await manager.StopAllAsync(force: true);
    }

    [Fact]
    public async Task StopAllPreservesConfirmationExceptionsForTheUi()
    {
        using var fixture = new Fixture();
        fixture.Probe.LockAllowed = false;
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        var error = await Assert.ThrowsAsync<AggregateException>(() => manager.StopAllAsync());
        Assert.IsType<UnmountSafetyConfirmationRequiredException>(Assert.Single(error.InnerExceptions));
        await manager.StopAllAsync(acknowledgeOpenFilesClosed: true);
        Assert.Equal(0, fixture.Processes[0].KillCount);
    }

    [Fact]
    public async Task StopAllAttemptsEveryConnectionWhenOneCannotStopSafely()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        var second = fixture.Connection.Clone();
        second.Id = "second";
        second.DriveLetter = "Y:";
        await manager.MountAsync(second, fixture.Settings);
        fixture.Controls[0].Failure = new IOException("unreachable");
        await Assert.ThrowsAsync<AggregateException>(() => manager.StopAllAsync());
        Assert.False(fixture.Processes[0].HasExited);
        Assert.True(fixture.Processes[1].HasExited);
        Assert.Equal(MountState.NotMounted, manager.GetStatus(second.Id).State);
        await manager.StopAllAsync(force: true);
    }

    [Fact]
    public async Task ProcessExitIsDisposedOnceAndCannotOverwriteANewSession()
    {
        using var fixture = new Fixture();
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        var oldProcess = fixture.Processes[0];
        oldProcess.Exit(9);
        await WaitForAsync(() => manager.GetStatus(fixture.Connection.Id).State == MountState.Failed);
        Assert.Equal(1, oldProcess.DisposeCount);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        Assert.Equal(fixture.Processes[1].Id, manager.GetStatus(fixture.Connection.Id).ProcessId);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
        await manager.StopAllAsync();
        Assert.Equal(1, oldProcess.DisposeCount);
        Assert.Equal(1, fixture.Processes[1].DisposeCount);
        Assert.Equal(["X:", "X:"], fixture.ShellNotifier.RemovedDrives);
    }

    [Fact]
    public async Task ProcessExitDuringStartupReportsBoundedRedactedStderr()
    {
        using var fixture = new Fixture();
        fixture.Connection.Secret = "review-secret";
        fixture.OnStart = process =>
        {
            process.Emit("authentication failed review-secret " + new string('x', 4000));
            process.Exit(3);
        };
        var manager = fixture.CreateManager();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(fixture.Connection, fixture.Settings));
        Assert.Contains("authentication failed", exception.Message);
        Assert.DoesNotContain("review-secret", exception.Message);
        Assert.True(exception.Message.Length < 2300);
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
    }

    [Fact]
    public async Task ProcessExitDuringStartupWaitsForDriveReleaseBeforeNotifyingShell()
    {
        using var fixture = new Fixture { KeepDriveOnExit = true, DriveReleaseTimeout = TimeSpan.FromSeconds(2) };
        fixture.OnStart = process => process.Exit(3);
        var manager = fixture.CreateManager();
        var mounting = manager.MountAsync(fixture.Connection, fixture.Settings);
        await WaitForAsync(() => fixture.Processes.Count != 0 && fixture.Processes[0].HasExited);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        Assert.False(mounting.IsCompleted);
        fixture.Probe.Drives.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mounting);
        Assert.Equal(MountState.Failed, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal(["X:"], fixture.ShellNotifier.RemovedDrives);
    }

    [Fact]
    public async Task ConnectionSettingsAndRuntimeStatusesAreIndependentSnapshots()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var release = NewSignal();
        MountConnection? received = null;
        fixture.Configure = async (connection, _, token) =>
        {
            received = connection;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return EmptyConfiguration();
        };
        var manager = fixture.CreateManager();
        var mounting = manager.MountAsync(fixture.Connection, fixture.Settings);
        await entered.Task;
        fixture.Connection.DriveLetter = "Y:";
        fixture.Connection.Host = "https://changed.test";
        fixture.Settings.RclonePath = "changed.exe";
        release.TrySetResult();
        await mounting;
        Assert.Equal("X:", received!.DriveLetter);
        Assert.Equal("https://example.test/dav", received.Host);
        Assert.Equal("rclone.exe", fixture.Processes[0].StartInfo.FileName);
        var status = manager.GetStatus(fixture.Connection.Id);
        status.State = MountState.Failed;
        Assert.Equal(MountState.Mounted, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal("X:", manager.GetStatus(fixture.Connection.Id).MountedDriveLetter);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task DriveRemainingAfterExitIsReportedAsFailure()
    {
        using var fixture = new Fixture { KeepDriveOnExit = true };
        var manager = fixture.CreateManager();
        await manager.MountAsync(fixture.Connection, fixture.Settings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(fixture.Connection.Id));
        Assert.Contains("has not been released", error.Message);
        Assert.Equal(MountState.Failed, manager.GetStatus(fixture.Connection.Id).State);
        Assert.Equal(1, fixture.Processes[0].DisposeCount);
        Assert.Empty(fixture.ShellNotifier.RemovedDrives);
        fixture.Probe.Drives.Clear();
    }

    [Fact]
    public void RcStatsNeverTreatsVfsReferenceCountAsOpenFiles()
    {
        using var json = JsonDocument.Parse("""
            {"fs":"mountly_test:","inUse":1,"diskCache":{"uploadsQueued":0,"uploadsInProgress":0,"erroredFiles":0,"bytesUsed":12,"outOfSpace":false}}
            """);
        var stats = RcloneMountControl.ParseStats(json.RootElement);
        Assert.Null(stats.OpenFiles);
        Assert.Equal(0, stats.UploadsQueued);
        Assert.False(stats.HasPendingWork);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static RcloneLaunchConfiguration EmptyConfiguration() => new("mountly_test", new Dictionary<string, string>());
    private static MountVfsStats IdleStats() => new(null, 0, 0, 0, 0, false, true);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { await Task.Delay(5, timeout.Token); }
    }

    private sealed class Fixture : IDisposable, IMountProcessFactory, IMountControlFactory
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MountloomTests-" + Guid.NewGuid().ToString("N"));
        public MountConnection Connection { get; } = new() { Host = "https://example.test/dav", DriveLetter = "X:" };
        public AppSettings Settings { get; }
        public FakeProbe Probe { get; } = new();
        public FakeShellNotifier ShellNotifier { get; } = new();
        public List<FakeProcess> Processes { get; } = [];
        public List<FakeControl> Controls { get; } = [];
        public bool CreateDriveOnStart { get; init; } = true;
        // Ordinary tests exercise asynchronous disk I/O before the fake process starts.
        // Give those checks headroom on loaded CI workers; only the timeout test needs a short deadline.
        public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public bool KeepDriveOnExit { get; init; }
        public TimeSpan DriveReleaseTimeout { get; init; } = TimeSpan.FromMilliseconds(80);
        public Action<FakeProcess>? OnStart { get; set; }
        public Func<MountConnection, AppSettings, CancellationToken, Task<RcloneLaunchConfiguration>> Configure { get; set; }
            = (_, _, _) => Task.FromResult(EmptyConfiguration());

        public Fixture() => Settings = new AppSettings { CacheRoot = Root, LogRoot = Root, RclonePath = "rclone.exe" };

        public MountManager CreateManager() => new(this, Probe, this, new MountManagerOptions
        {
            StartupTimeout = StartupTimeout,
            StopTimeout = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(5),
            DriveReleaseTimeout = DriveReleaseTimeout,
            HealthInterval = TimeSpan.FromHours(1)
        }, (connection, settings, token) => Configure(connection, settings, token), ShellNotifier);

        public IMountProcess Create(ProcessStartInfo startInfo)
        {
            var mountIndex = startInfo.ArgumentList.IndexOf("mount");
            var drive = startInfo.ArgumentList[mountIndex + 2];
            var process = new FakeProcess(1000 + Processes.Count, startInfo,
                process =>
                {
                    if (CreateDriveOnStart) { Probe.Drives.Add(drive); }
                    OnStart?.Invoke(process);
                }, () => { if (!KeepDriveOnExit) { Probe.Drives.Remove(drive); } });
            Controls[^1].Process = process;
            Processes.Add(process);
            return process;
        }

        public IMountControl Create()
        {
            var control = new FakeControl(Probe);
            Controls.Add(control);
            return control;
        }

        public void Dispose()
        {
            foreach (var process in Processes.Where(process => !process.HasExited)) { process.Exit(0); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, true); }
        }
    }

    private sealed class FakeProcess(int id, ProcessStartInfo startInfo, Action<FakeProcess> onStart, Action onExit) : IMountProcess
    {
        private readonly TaskCompletionSource _exited = NewSignal();
        public ProcessStartInfo StartInfo { get; } = startInfo;
        public int Id => id;
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<ProcessLogLine>? OutputReceived;
        public void Start() => onStart(this);
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exited.Task.WaitAsync(cancellationToken);
        public void Emit(string text) => OutputReceived?.Invoke(this, new ProcessLogLine(text, true));
        public void Kill() { KillCount++; Exit(-1); }
        public void Exit(int code)
        {
            if (HasExited) { return; }
            ExitCode = code;
            HasExited = true;
            onExit();
            _exited.TrySetResult();
        }
        public void Dispose() { DisposeCount++; }
    }

    private sealed class FakeControl(FakeProbe probe) : IMountControl
    {
        public FakeProcess Process { get; set; } = null!;
        public MountVfsStats Stats { get; set; } = IdleStats();
        public Exception? Failure { get; set; }
        public int QuitCount { get; private set; }
        public bool QuitSawVolumeLock { get; private set; }
        public IReadOnlyList<string> Arguments => [];
        public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>();
        public Task<int> GetProcessIdAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Process.Id) : Task.FromException<int>(Failure);
        public Task<MountVfsStats> GetStatsAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Stats) : Task.FromException<MountVfsStats>(Failure);
        public Task QuitAsync(CancellationToken cancellationToken)
        {
            QuitCount++;
            QuitSawVolumeLock = probe.ActiveLocks > 0;
            Process.Exit(0);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class FakeProbe : IMountProbe
    {
        public HashSet<string> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool LockAllowed { get; set; } = true;
        public int ActiveLocks { get; private set; }
        public bool IsDrivePresent(string driveLetter) => Drives.Contains(driveLetter);
        public bool TryAcquireUnmountLock(string driveLetter, out IDisposable? lease, out string reason)
        {
            lease = null;
            reason = "Open files or unsupported lock.";
            if (!LockAllowed) { return false; }
            ActiveLocks++;
            lease = new Lease(() => ActiveLocks--);
            return true;
        }
    }

    private sealed class FakeShellNotifier : IMountShellNotifier
    {
        public List<string> RemovedDrives { get; } = [];
        public Action<string>? OnRemoved { get; set; }
        public void NotifyDriveRemoved(string driveLetter)
        {
            RemovedDrives.Add(driveLetter);
            OnRemoved?.Invoke(driveLetter);
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

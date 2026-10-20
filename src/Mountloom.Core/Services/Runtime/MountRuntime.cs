using System.Diagnostics;

namespace Mountloom.Core.Services.Runtime;

public sealed record MountLogEntry(string ConnectionId, string Message, DateTimeOffset Timestamp);
public sealed record ProcessLogLine(string Text, bool IsError);

public sealed class UnmountSafetyConfirmationRequiredException(string connectionId, string driveLetter, string reason)
    : InvalidOperationException($"Windows could not verify that every file on {driveLetter} is closed. Close all files and applications using this drive, then confirm a cooperative unmount. Pending uploads will still be checked; this is not a force unmount. {reason}")
{
    public string ConnectionId { get; } = connectionId;
    public string DriveLetter { get; } = driveLetter;
}

/// <summary>A process owned exclusively by one mount session.</summary>
public interface IMountProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    event EventHandler<ProcessLogLine>? OutputReceived;
    void Start();
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
    void Kill();
}

public interface IMountProcessFactory
{
    IMountProcess Create(ProcessStartInfo startInfo);
}

public interface IMountProbe
{
    bool IsDrivePresent(string driveLetter);
    // A successful lease prevents new opens until the mount process has stopped.
    bool TryAcquireUnmountLock(string driveLetter, out IDisposable? lease, out string reason);
}

/// <summary>Updates Explorer after an owned mount has released its Windows drive.</summary>
public interface IMountShellNotifier
{
    void NotifyDriveRemoved(string driveLetter);
}

/// <summary>The returned lease restores the previous icon after the mount releases its drive.</summary>
public interface IMountDriveIconService
{
    IDisposable? Apply(string driveLetter);
}

public sealed record MountVfsStats(
    long? OpenFiles,
    long? UploadsQueued,
    long? UploadsInProgress,
    long? ErroredFiles,
    long? BytesUsed,
    bool? OutOfSpace,
    bool HasDiskCache)
{
    public bool HasPendingWork => OpenFiles is > 0 || UploadsQueued is > 0 || UploadsInProgress is > 0 || ErroredFiles is > 0;
}

public interface IMountControl : IDisposable
{
    IReadOnlyList<string> Arguments { get; }
    IReadOnlyDictionary<string, string> Environment { get; }
    Task<int> GetProcessIdAsync(CancellationToken cancellationToken);
    Task<MountVfsStats> GetStatsAsync(CancellationToken cancellationToken);
    Task QuitAsync(CancellationToken cancellationToken);
}

public interface IMountControlFactory
{
    IMountControl Create();
}

public sealed class MountManagerOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DriveReleaseTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(5);
}

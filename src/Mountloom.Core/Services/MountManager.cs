using Mountloom.Core.Models;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.Core.Services;

/// <summary>Owns every mount from reservation through process exit and drive release.</summary>
public sealed class MountManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MountRuntimeStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _driveOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMountProcessFactory _processFactory;
    private readonly IMountProbe _probe;
    private readonly IMountShellNotifier _shellNotifier;
    private readonly IMountDriveIconService _driveIcons;
    private readonly IMountControlFactory _controlFactory;
    private readonly MountManagerOptions _options;
    private readonly Func<MountConnection, AppSettings, CancellationToken, Task<RcloneLaunchConfiguration>> _configure;
    private bool _stopping;
    private int _stopAllCalls;

    public MountManager(IMountProcessFactory? processFactory = null, IMountProbe? probe = null,
        IMountControlFactory? controlFactory = null, MountManagerOptions? options = null,
        Func<MountConnection, AppSettings, CancellationToken, Task<RcloneLaunchConfiguration>>? configure = null,
        IMountShellNotifier? shellNotifier = null, IMountDriveIconService? driveIcons = null)
    {
        _processFactory = processFactory ?? new SystemMountProcessFactory();
        _probe = probe ?? new WindowsMountProbe();
        _shellNotifier = shellNotifier ?? new WindowsMountShellNotifier();
        _driveIcons = driveIcons ?? new WindowsMountDriveIconService();
        _controlFactory = controlFactory ?? new RcloneMountControlFactory();
        _options = options ?? new MountManagerOptions();
        _configure = configure ?? ((connection, settings, token) =>
            new RcloneConfigWriter().CreateAsync(connection, settings.RclonePath, token,
                RcloneArguments.GetCacheDirectory(connection, settings)));
    }

    public event EventHandler<MountRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? LogReceived;
    public event EventHandler<MountLogEntry>? LogEntryReceived;

    public MountRuntimeStatus GetStatus(string connectionId)
    {
        lock (_sync)
        {
            return _statuses.TryGetValue(connectionId, out var status)
                ? status.Clone()
                : new MountRuntimeStatus { ConnectionId = connectionId };
        }
    }

    public IReadOnlyList<string> GetReservedDriveLetters()
    {
        lock (_sync) { return _driveOwners.Keys.ToArray(); }
    }

    public Task MountAsync(MountConnection connection, AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = connection.Clone();
        var appSnapshot = settings.Clone();
        snapshot.DriveLetter = DriveLetterService.Validate(snapshot.DriveLetter);
        if (string.IsNullOrWhiteSpace(snapshot.Id))
        {
            throw new ArgumentException("A mount connection must have an ID.", nameof(connection));
        }

        Session session;
        lock (_sync)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("Mountloom is stopping its mounts. New mounts are disabled until shutdown is cancelled.");
            }
            if (_sessions.TryGetValue(snapshot.Id, out var existing))
            {
                if (existing.StopRequested)
                {
                    throw new InvalidOperationException("This connection is stopping. Wait for unmount to finish before mounting again.");
                }
                return existing.Started.Task.WaitAsync(cancellationToken);
            }
            if (_driveOwners.ContainsKey(snapshot.DriveLetter) || _probe.IsDrivePresent(snapshot.DriveLetter))
            {
                throw new InvalidOperationException($"Drive {snapshot.DriveLetter} is already occupied or reserved by another mount.");
            }
            session = new Session(snapshot, appSnapshot);
            _sessions.Add(snapshot.Id, session);
            _driveOwners.Add(snapshot.DriveLetter, snapshot.Id);
        }

        // Registration precedes all asynchronous work, including credential preparation.
        _ = StartSessionAsync(session, cancellationToken);
        return session.Started.Task;
    }

    public async Task UnmountAsync(string connectionId, bool force = false, CancellationToken cancellationToken = default,
        bool acknowledgeOpenFilesClosed = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session? session;
        lock (_sync)
        {
            _sessions.TryGetValue(connectionId, out session);
            if (session is not null)
            {
                session.StopRequests++;
                session.StopRequested = true;
            }
        }
        if (session is null) { return; }
        session.StartCancellation.Cancel();

        var gateHeld = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            if (!IsCurrent(session)) { return; }
            SetStatus(session, MountState.Unmounting, string.Empty);
            await StopProcessAsync(session, force, cancellationToken, acknowledgeOpenFilesClosed: acknowledgeOpenFilesClosed).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A cancelled waiter does not own the session and must not dispose it.
            if (!gateHeld) { throw; }
            var message = Redact(session, ex.Message);
            if (session.Process is { HasExited: true })
            {
                FinishSession(session, MountState.Failed, message);
            }
            else if (IsCurrent(session))
            {
                SetStatus(session, session.Ready ? MountState.Mounted : MountState.Failed, message);
            }
            throw;
        }
        finally
        {
            lock (_sync)
            {
                session.StopRequests--;
                if (session.StopRequests == 0) { session.StopRequested = false; }
            }
            if (gateHeld) { session.Gate.Release(); }
        }
    }

    public async Task StopAllAsync(bool force = false, CancellationToken cancellationToken = default,
        bool acknowledgeOpenFilesClosed = false)
    {
        Session[] sessions;
        lock (_sync)
        {
            _stopping = true;
            _stopAllCalls++;
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions) { session.StopRequested = true; }
        }
        foreach (var session in sessions) { session.StartCancellation.Cancel(); }
        try
        {
            // Attempt every mount even if another mount is busy or its RC endpoint failed.
            var failures = await Task.WhenAll(sessions.Select(async session =>
            {
                try
                {
                    await UnmountAsync(session.Connection.Id, force, cancellationToken, acknowledgeOpenFilesClosed).ConfigureAwait(false);
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    if (ex is UnmountSafetyConfirmationRequiredException) { return ex; }
                    return new InvalidOperationException($"{session.Connection.Name}: {Redact(session, ex.Message)}", ex);
                }
            })).ConfigureAwait(false);
            var errors = failures.Where(error => error is not null).Cast<Exception>().ToArray();
            if (errors.Length != 0)
            {
                throw new AggregateException("Some mounts could not be stopped safely. They remain managed by Mountloom.", errors);
            }
            // A previous stop may have observed process exit before Windows released
            // its letter. Do not report a successful retry until its icon is restored.
            using var release = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            release.CancelAfter(_options.DriveReleaseTimeout);
            try
            {
                while (GetReservedDriveLetters().Count != 0)
                {
                    await Task.Delay(_options.PollInterval, release.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Windows drive release or drive icon restoration is still pending. Wait for the drive to disappear, then retry exiting Mountloom.");
            }
        }
        finally
        {
            lock (_sync)
            {
                _stopAllCalls--;
                foreach (var session in sessions)
                    if (session.StopRequests == 0) { session.StopRequested = false; }
            }
        }
    }

    public void ResumeOperations()
    {
        lock (_sync)
        {
            if (_stopAllCalls != 0) { throw new InvalidOperationException("Wait for the current shutdown attempt to finish."); }
            _stopping = false;
        }
    }

    private async Task StartSessionAsync(Session session, CancellationToken callerToken)
    {
        await session.Gate.WaitAsync().ConfigureAwait(false);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(callerToken, session.StartCancellation.Token);
        startup.CancelAfter(_options.StartupTimeout);
        try
        {
            if (!IsCurrent(session))
            {
                session.Started.TrySetCanceled();
                return;
            }
            SetStatus(session, MountState.Mounting, string.Empty);
            var token = startup.Token;
            token.ThrowIfCancellationRequested();
            var arguments = RcloneArguments.BuildMount(session.Connection, session.Settings).ToList();
            var configuration = await _configure(session.Connection, session.Settings, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await new VfsCacheSafetyGuard().ValidateAndRecordAsync(
                RcloneArguments.GetCacheDirectory(session.Connection, session.Settings),
                session.Connection, configuration, token).ConfigureAwait(false);
            Directory.CreateDirectory(GetCacheDirectory(session));
            session.Control = _controlFactory.Create();
            arguments.AddRange(session.Control.Arguments);
            var environment = configuration.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in session.Control.Environment) { environment[pair.Key] = pair.Value; }
            session.LogSecrets = environment.Where(pair => pair.Key.Contains("PASS", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("ACCESS_KEY", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value).Where(value => value.Length != 0).ToArray();
            session.Process = _processFactory.Create(ProcessRunner.CreateStartInfo(session.Settings.RclonePath, arguments, environment));
            session.Process.OutputReceived += (_, line) => AppendLog(session, line);
            token.ThrowIfCancellationRequested();
            session.Process.Start();
            session.ProcessStarted = true;
            session.ExitTask = session.Process.WaitForExitAsync();
            _ = ObserveExitAsync(session);
            await WaitUntilReadyAsync(session, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            session.Ready = true;
            session.MountedAt = DateTimeOffset.Now;
            try { session.DriveIcon = _driveIcons.Apply(session.Connection.DriveLetter); }
            catch (Exception iconError)
            {
                AppendLog(session, new ProcessLogLine("Could not set the mounted drive icon: " + Redact(session, iconError.Message), true));
            }
            SetStatus(session, MountState.Mounted, string.Empty);
            session.Started.TrySetResult();
            _ = MonitorHealthAsync(session);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException
                ? callerToken.IsCancellationRequested || session.StartCancellation.IsCancellationRequested
                    ? "Mount startup was cancelled."
                    : $"Mount did not become ready within {_options.StartupTimeout.TotalSeconds:0} seconds."
                : Redact(session, ex.Message);
            if (session.StartupDetail.Length != 0 && ex is OperationCanceledException && !session.StartCancellation.IsCancellationRequested)
            {
                error += " " + session.StartupDetail;
            }

            if (!session.ProcessStarted)
            {
                FinishSession(session, ex is OperationCanceledException ? MountState.NotMounted : MountState.Failed, error);
            }
            else if (session.Process is { HasExited: true })
            {
                try { await session.ExitTask.ConfigureAwait(false); }
                catch (Exception exitError) { session.ExitDetail = Redact(session, exitError.Message); }
                error = ExitMessage(session);
                try { await WaitForDriveReleaseAsync(session, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception releaseError) { error += " " + Redact(session, releaseError.Message); }
                FinishSession(session, MountState.Failed, error);
            }
            else if (!session.StopRequested)
            {
                // A failed/cancelled startup may already be recovering dirty VFS cache.
                // Never kill that process implicitly; a failed safe stop keeps ownership.
                try
                {
                    await StopProcessAsync(session, false, CancellationToken.None,
                        ex is OperationCanceledException && callerToken.IsCancellationRequested ? MountState.NotMounted : MountState.Failed,
                        error).ConfigureAwait(false);
                }
                catch (Exception stopError)
                {
                    error += " " + Redact(session, stopError.Message);
                    SetStatus(session, MountState.Failed, error);
                }
            }

            if (ex is OperationCanceledException && (callerToken.IsCancellationRequested || session.StartCancellation.IsCancellationRequested))
            {
                session.Started.TrySetCanceled();
            }
            else { session.Started.TrySetException(new InvalidOperationException(error)); }
        }
        finally { session.Gate.Release(); }
    }

    private async Task WaitUntilReadyAsync(Session session, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Process!.HasExited)
            {
                await session.ExitTask.ConfigureAwait(false);
                throw new InvalidOperationException(ExitMessage(session));
            }
            try
            {
                if (await session.Control!.GetProcessIdAsync(cancellationToken).ConfigureAwait(false) != session.Process.Id)
                {
                    throw new InvalidOperationException("The control endpoint does not belong to this mount process.");
                }
                var stats = await session.Control.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                UpdateStats(session, stats);
                if (_probe.IsDrivePresent(session.Connection.DriveLetter)) { return; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                session.StartupDetail = Redact(session, ex.Message);
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopProcessAsync(Session session, bool force, CancellationToken cancellationToken,
        MountState terminalState = MountState.NotMounted, string terminalError = "", bool acknowledgeOpenFilesClosed = false)
    {
        if (!session.ProcessStarted)
        {
            FinishSession(session, MountState.NotMounted, string.Empty);
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(_options.StopTimeout);
        IDisposable? volumeLock = null;
        try
        {
            if (!session.Process!.HasExited)
            {
                if (force)
                {
                    session.ExpectedExit = true;
                    AppendLog(session, new ProcessLogLine("Force unmount requested. Cached writes may require recovery on the next mount.", true));
                    try { session.Process.Kill(); }
                    catch when (session.Process.HasExited) { /* Natural exit won the race. */ }
                }
                else
                {
                    volumeLock = await WaitForSafeStopAsync(session, acknowledgeOpenFilesClosed, stop.Token).ConfigureAwait(false);
                    session.ExpectedExit = true;
                    try { await session.Control!.QuitAsync(stop.Token).ConfigureAwait(false); }
                    catch when (session.Process.HasExited) { /* The process may close HTTP before its reply is read. */ }
                }
            }
            await session.ExitTask.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Safe unmount timed out. Close files and wait for uploads, then retry. Force unmount is available only if you accept interrupted writes. " + session.StopDetail);
        }
        finally { volumeLock?.Dispose(); }

        await WaitForDriveReleaseAsync(session, cancellationToken).ConfigureAwait(false);
        FinishSession(session, terminalState, terminalError);
    }

    private async Task<IDisposable?> WaitForSafeStopAsync(Session session, bool acknowledgeOpenFilesClosed,
        CancellationToken cancellationToken)
    {
        while (!session.Process!.HasExited)
        {
            MountVfsStats stats;
            try
            {
                if (await session.Control!.GetProcessIdAsync(cancellationToken).ConfigureAwait(false) != session.Process.Id)
                {
                    throw new InvalidOperationException("Control endpoint process identity changed.");
                }
                stats = await session.Control.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cannot verify pending writes through rclone RC; the process was kept running. " + Redact(session, ex.Message));
            }
            UpdateStats(session, stats);
            EnsureCacheStatsKnown(session, stats);
            if (stats.HasPendingWork)
            {
                session.StopDetail = $"Open files: {stats.OpenFiles?.ToString() ?? "unknown"}; queued uploads: {stats.UploadsQueued}; active uploads: {stats.UploadsInProgress}; cache errors: {stats.ErroredFiles}.";
            }
            else if (!_probe.IsDrivePresent(session.Connection.DriveLetter))
            {
                // No mountpoint can accept new writes; an empty queue can be stopped.
                return null;
            }
            else if (_probe.TryAcquireUnmountLock(session.Connection.DriveLetter, out var lease, out var reason))
            {
                try
                {
                    // Recheck after excluding new opens to close the final-write race.
                    var lockedStats = await session.Control.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                    UpdateStats(session, lockedStats);
                    EnsureCacheStatsKnown(session, lockedStats);
                    if (!lockedStats.HasPendingWork) { return lease; }
                }
                catch { lease?.Dispose(); throw; }
                lease?.Dispose();
            }
            else if (acknowledgeOpenFilesClosed)
            {
                // Some WinFsp/network mounts deny volume locking to ordinary users.
                // Only an explicit acknowledgement permits the cooperative fallback.
                // It never bypasses known pending writes, unknown cache state, or RC errors.
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                var confirmedStats = await session.Control.GetStatsAsync(cancellationToken).ConfigureAwait(false);
                UpdateStats(session, confirmedStats);
                EnsureCacheStatsKnown(session, confirmedStats);
                if (!confirmedStats.HasPendingWork)
                {
                    AppendLog(session, new ProcessLogLine("Cooperative unmount confirmed after closing files. Open-file state could not be independently verified; no force kill is being used.", false));
                    return null;
                }
            }
            else
            {
                throw new UnmountSafetyConfirmationRequiredException(session.Connection.Id, session.Connection.DriveLetter, reason);
            }
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static void EnsureCacheStatsKnown(Session session, MountVfsStats stats)
    {
        if (session.Connection.CacheMode != CacheMode.Off &&
            (!stats.HasDiskCache || stats.UploadsQueued is null || stats.UploadsInProgress is null || stats.ErroredFiles is null))
        {
            throw new InvalidOperationException("rclone does not expose the cache state needed for safe unmount. The process was kept running.");
        }
    }

    private async Task WaitForDriveReleaseAsync(Session session, CancellationToken cancellationToken)
    {
        using var release = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        release.CancelAfter(_options.DriveReleaseTimeout);
        try
        {
            while (_probe.IsDrivePresent(session.Connection.DriveLetter))
            {
                await Task.Delay(_options.PollInterval, release.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"rclone exited, but drive {session.Connection.DriveLetter} has not been released by Windows. Check the drive before reconnecting.");
        }
    }

    private async Task ObserveExitAsync(Session session)
    {
        try { await session.ExitTask.ConfigureAwait(false); }
        catch (Exception ex) { session.ExitDetail = Redact(session, ex.Message); }
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrent(session)) { return; }
            var state = session.ExpectedExit ? MountState.NotMounted : MountState.Failed;
            var error = session.ExpectedExit ? string.Empty : ExitMessage(session);
            try { await WaitForDriveReleaseAsync(session, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { state = MountState.Failed; error = Redact(session, ex.Message); }
            FinishSession(session, state, error);
        }
        finally { session.Gate.Release(); }
    }

    private async Task MonitorHealthAsync(Session session)
    {
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                await Task.Delay(_options.HealthInterval, session.Lifetime.Token).ConfigureAwait(false);
                await session.Gate.WaitAsync(session.Lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(session) || session.Process!.HasExited) { return; }
                    var stats = await session.Control!.GetStatsAsync(session.Lifetime.Token).ConfigureAwait(false);
                    UpdateStats(session, stats);
                    if (!_probe.IsDrivePresent(session.Connection.DriveLetter))
                    {
                        UpdateHealthWarning(session, "The rclone process is running, but the mounted drive is no longer visible. Unmount and reconnect this connection.");
                    }
                }
                catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    UpdateHealthWarning(session, "Mount health could not be verified: " + Redact(session, ex.Message));
                }
                finally { session.Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
    }

    private bool IsCurrent(Session session)
    {
        lock (_sync) { return _sessions.TryGetValue(session.Connection.Id, out var current) && ReferenceEquals(current, session); }
    }

    private void FinishSession(Session session, MountState state, string error)
    {
        // Caller owns the session gate. No exit callback is allowed to dispose Process.
        if (!IsCurrent(session)) { return; }
        NotifyReleasedDrive(session);
        session.Lifetime.Cancel();
        session.Process?.Dispose();
        session.Process = null;
        session.Control?.Dispose();
        session.Control = null;
        SetStatus(session, state, error);
        lock (_sync)
        {
            if (_sessions.TryGetValue(session.Connection.Id, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.Connection.Id);
                // Retain the reservation while a late Windows drive release still
                // owns its icon, so a new mount cannot race the restoration.
                if (session.DriveIcon is null) { _driveOwners.Remove(session.Connection.DriveLetter); }
            }
        }
        if (session.DriveIcon is not null) { _ = RestoreIconAfterDriveReleaseAsync(session); }
    }

    private void NotifyReleasedDrive(Session session)
    {
        try
        {
            // All terminal paths converge here, including a process that exits during
            // startup. Keep the drive reserved until this check and notification finish.
            if (!session.ProcessStarted || session.Process is not { HasExited: true } ||
                session.ShellRemovalNotified || _probe.IsDrivePresent(session.Connection.DriveLetter)) { return; }
            RestoreDriveIcon(session);
            session.ShellRemovalNotified = true;
            _shellNotifier.NotifyDriveRemoved(session.Connection.DriveLetter);
        }
        catch (Exception ex)
        {
            // A shell refresh is best effort and must never prevent process cleanup.
            AppendLog(session, new ProcessLogLine("Could not refresh Explorer after drive removal: " + Redact(session, ex.Message), true));
        }
    }

    private void RestoreDriveIcon(Session session)
    {
        session.DriveIcon?.Dispose();
        session.DriveIcon = null;
    }

    private async Task RestoreIconAfterDriveReleaseAsync(Session session)
    {
        // A driver can release the letter after our bounded unmount wait expires.
        // Keep the icon until that actually happens; never touch the mount itself.
        while (session.DriveIcon is not null)
        {
            await Task.Delay(_options.PollInterval).ConfigureAwait(false);
            try
            {
                if (_probe.IsDrivePresent(session.Connection.DriveLetter)) { continue; }
                RestoreDriveIcon(session);
                if (!session.ShellRemovalNotified)
                {
                    session.ShellRemovalNotified = true;
                    _shellNotifier.NotifyDriveRemoved(session.Connection.DriveLetter);
                }
            }
            catch { /* Keep the original icon snapshot and retry after transient registry/probe errors. */ }
        }
        lock (_sync)
        {
            if (_driveOwners.GetValueOrDefault(session.Connection.DriveLetter) == session.Connection.Id)
            {
                _driveOwners.Remove(session.Connection.DriveLetter);
            }
        }
    }

    private void SetStatus(Session session, MountState state, string error)
    {
        MountRuntimeStatus status;
        lock (_sync)
        {
            status = _statuses.TryGetValue(session.Connection.Id, out var previous) ? previous.Clone() : new() { ConnectionId = session.Connection.Id };
            status.State = state;
            status.ProcessId = session.ProcessStarted && session.Process is not null ? session.Process.Id : null;
            status.LastError = Redact(session, error);
            status.MountedDriveLetter = session.Connection.DriveLetter;
            if (session.MountedAt is not null) { status.LastMountedAt = session.MountedAt; }
            _statuses[session.Connection.Id] = status;
        }
        Publish(StatusChanged, status.Clone());
    }

    private void UpdateStats(Session session, MountVfsStats stats)
    {
        MountRuntimeStatus status;
        lock (_sync)
        {
            status = _statuses.TryGetValue(session.Connection.Id, out var previous) ? previous.Clone() : new() { ConnectionId = session.Connection.Id };
            status.PendingUploads = stats.UploadsQueued;
            status.ActiveUploads = stats.UploadsInProgress;
            status.OpenFiles = stats.OpenFiles;
            status.CacheBytesUsed = stats.BytesUsed;
            status.CacheOutOfSpace = stats.OutOfSpace;
            status.HealthWarning = stats.OutOfSpace == true ? "The VFS cache disk is out of free space. Uploads may be blocked."
                : stats.ErroredFiles > 0 ? $"{stats.ErroredFiles} cached files have errors. Keep the cache until recovery completes." : string.Empty;
            _statuses[session.Connection.Id] = status;
        }
        Publish(StatusChanged, status.Clone());
    }

    private void UpdateHealthWarning(Session session, string warning)
    {
        MountRuntimeStatus status;
        lock (_sync)
        {
            status = _statuses[session.Connection.Id].Clone();
            status.HealthWarning = warning;
            _statuses[session.Connection.Id] = status;
        }
        Publish(StatusChanged, status.Clone());
    }

    private void AppendLog(Session session, ProcessLogLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text)) { return; }
        var text = Redact(session, line.Text);
        if (text.Length > 2048) { text = text[..2048] + " [truncated]"; }
        lock (session.ErrorLines)
        {
            if (line.IsError)
            {
                session.ErrorLines.Enqueue(text);
                while (session.ErrorLines.Count > 32) { session.ErrorLines.Dequeue(); }
            }
        }
        var entry = new MountLogEntry(session.Connection.Id, text, DateTimeOffset.Now);
        Publish(LogEntryReceived, entry);
        Publish(LogReceived, $"[{entry.Timestamp:HH:mm:ss}] {session.Connection.Name}: {text}");
    }

    private string ExitMessage(Session session)
    {
        string tail;
        lock (session.ErrorLines) { tail = string.Join(Environment.NewLine, session.ErrorLines.TakeLast(5)); }
        return Redact(session, $"rclone exited with code {session.Process?.ExitCode}. " +
            (tail.Length != 0 ? tail : session.ExitDetail.Length != 0 ? session.ExitDetail : session.StartupDetail));
    }

    private static string Redact(Session session, string text)
    {
        var result = LogRedactor.Redact(text, session.Connection);
        foreach (var secret in session.LogSecrets) { result = result.Replace(secret, "[redacted]", StringComparison.Ordinal); }
        return result;
    }

    private void Publish<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) { return; }
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, value); }
            catch { /* UI/log observers must not interrupt process ownership or cleanup. */ }
        }
    }

    private static string GetCacheDirectory(Session session) => string.IsNullOrWhiteSpace(session.Connection.CacheDirectory)
        ? Path.Combine(session.Settings.CacheRoot, session.Connection.Id) : session.Connection.CacheDirectory;

    private sealed class Session(MountConnection connection, AppSettings settings)
    {
        public MountConnection Connection { get; } = connection;
        public AppSettings Settings { get; } = settings;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource StartCancellation { get; } = new();
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExitTask { get; set; } = Task.CompletedTask;
        public IMountProcess? Process { get; set; }
        public IMountControl? Control { get; set; }
        public IDisposable? DriveIcon { get; set; }
        public bool ProcessStarted { get; set; }
        public bool Ready { get; set; }
        public DateTimeOffset? MountedAt { get; set; }
        public volatile bool StopRequested;
        public int StopRequests;
        public bool ExpectedExit { get; set; }
        public bool ShellRemovalNotified { get; set; }
        public string StartupDetail { get; set; } = string.Empty;
        public string ExitDetail { get; set; } = string.Empty;
        public string StopDetail { get; set; } = string.Empty;
        public string[] LogSecrets { get; set; } = [];
        public Queue<string> ErrorLines { get; } = new();
    }
}

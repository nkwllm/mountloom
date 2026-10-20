using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mountloom.Core.Models;
using Mountloom.Core.Services;
using Mountloom.Core.Services.Runtime;

if (args.Length != 2 || args[0] != "--rclone")
{
    Console.Error.WriteLine("Run explicitly: Mountloom.Integration --rclone <rclone.exe>. Uses a temporary local WebDAV server and an unused drive.");
    return 2;
}
var rclone = Path.GetFullPath(args[1]);
var root = Path.Combine(Path.GetTempPath(), "MountloomIntegration-" + Guid.NewGuid().ToString("N"));
var remoteDirectory = Path.Combine(root, "remote");
Directory.CreateDirectory(remoteDirectory);
await File.WriteAllTextAsync(Path.Combine(remoteDirectory, "read.txt"), "initial content");
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
const string password = "synthetic-测试-quote\"-slash\\";
var settings = new AppSettings
{
    RclonePath = rclone, CacheRoot = Path.Combine(root, "cache"), LogRoot = Path.Combine(root, "logs")
};
var drive = new DriveLetterService().GetAvailableDriveLetters().LastOrDefault()
    ?? throw new InvalidOperationException("No free drive letter for the integration test.");
var connection = new MountConnection
{
    Name = "Local integration only", Host = $"http://127.0.0.1:{port}/", Username = "mountly-test",
    Secret = password, DriveLetter = drive, AdvancedArguments = "--vfs-write-back 1s --timeout 5s --contimeout 3s --low-level-retries 1",
    VfsCacheMaxSize = "100M", VfsCacheMinFreeSpace = "0"
};
var serverInfo = ProcessRunner.CreateStartInfo(rclone,
    ["--config", "NUL", "serve", "webdav", remoteDirectory, "--addr", $"127.0.0.1:{port}", "--user", "mountly-test"],
    new Dictionary<string, string> { ["RCLONE_PASS"] = password });
Process? server = null;
var manager = new MountManager(options: new MountManagerOptions
{
    StopTimeout = TimeSpan.FromSeconds(8), HealthInterval = TimeSpan.FromMilliseconds(500)
}, driveIcons: new NoOpDriveIcons());
MountState? previousState = null;
manager.StatusChanged += (_, status) =>
{
    if (previousState != status.State) Console.WriteLine($"STATE={status.State}; {status.HealthWarning} {status.LastError}");
    previousState = status.State;
};
manager.LogReceived += (_, line) => Console.WriteLine(line);
var result = 1;
try
{
    server = await StartServerAsync();
    var environment = await new EnvironmentDetector().DetectAsync(rclone);
    if (!environment.RcloneFound || !environment.WinFspFound) throw new InvalidOperationException(environment.Message);
    Console.WriteLine($"ENVIRONMENT={environment.RcloneVersion}; WinFsp {environment.WinFspVersion}");
    Console.WriteLine(await new ConnectionTester().TestAsync(connection, settings));
    await manager.MountAsync(connection, settings);
    if (manager.GetStatus(connection.Id).State != MountState.Mounted) throw new Exception("Mount never became ready.");
    var mountedRoot = drive + "\\";
    if (await File.ReadAllTextAsync(Path.Combine(mountedRoot, "read.txt")) != "initial content") throw new Exception("Read-back mismatch.");
    await File.WriteAllTextAsync(Path.Combine(mountedRoot, "written.txt"), "uploaded through Mountloom");
    for (var attempt = 0; ; attempt++)
    {
        var target = Path.Combine(remoteDirectory, "written.txt");
        if (File.Exists(target) && await File.ReadAllTextAsync(target) == "uploaded through Mountloom") break;
        if (attempt >= 100) throw new TimeoutException("The temporary remote never received the uploaded file.");
        await Task.Delay(200);
    }
    Console.WriteLine("READ_WRITE_UPLOAD=PASS");
    try { await manager.UnmountAsync(connection.Id); }
    catch (UnmountSafetyConfirmationRequiredException)
    {
        // This harness owns every opened file and has disposed all handles before acknowledging.
        await manager.UnmountAsync(connection.Id, acknowledgeOpenFilesClosed: true);
    }
    if (DriveInfo.GetDrives().Any(d => d.Name.Equals(mountedRoot, StringComparison.OrdinalIgnoreCase))) throw new Exception("Drive remains after unmount.");
    Console.WriteLine("NORMAL_UNMOUNT=PASS");
    await manager.MountAsync(connection, settings);
    if (await File.ReadAllTextAsync(Path.Combine(mountedRoot, "written.txt")) != "uploaded through Mountloom") throw new Exception("Remount read-back mismatch.");
    await StopServerAsync();
    await File.WriteAllTextAsync(Path.Combine(mountedRoot, "offline.txt"), "recover this pending upload").WaitAsync(TimeSpan.FromSeconds(30));
    for (var attempt = 0; ; attempt++)
    {
        var state = manager.GetStatus(connection.Id);
        if (state.PendingUploads is > 0 || state.ActiveUploads is > 0) break;
        if (attempt >= 100) throw new TimeoutException("Pending offline write was not reported.");
        await Task.Delay(100);
    }
    var refused = false;
    try { await manager.UnmountAsync(connection.Id, acknowledgeOpenFilesClosed: true); }
    catch (InvalidOperationException) { refused = true; }
    if (!refused || !DriveInfo.GetDrives().Any(d => d.Name.Equals(mountedRoot, StringComparison.OrdinalIgnoreCase)))
        throw new Exception("Normal unmount did not preserve a mount with pending offline writes.");
    Console.WriteLine("OFFLINE_NORMAL_UNMOUNT_REFUSED=PASS");
    await manager.UnmountAsync(connection.Id, force: true);
    server = await StartServerAsync();
    await manager.MountAsync(connection, settings);
    for (var attempt = 0; ; attempt++)
    {
        var target = Path.Combine(remoteDirectory, "offline.txt");
        if (File.Exists(target) && await File.ReadAllTextAsync(target) == "recover this pending upload") break;
        if (attempt >= 200) throw new TimeoutException("Pending cached write was not recovered after remount.");
        await Task.Delay(200);
    }
    Console.WriteLine("FORCED_UNMOUNT_CACHE_RECOVERY=PASS");
    await manager.StopAllAsync(acknowledgeOpenFilesClosed: true);
    if (DriveInfo.GetDrives().Any(d => d.Name.Equals(mountedRoot, StringComparison.OrdinalIgnoreCase))) throw new Exception("Drive remains after StopAll.");
    Console.WriteLine("REMOUNT_STOP_ALL=PASS");
    result = 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
}
finally
{
    try { await manager.StopAllAsync(force: true); }
    catch (Exception error) { Console.Error.WriteLine("Cleanup failed: " + error.Message); result = 1; }
    await StopServerAsync();
    Console.WriteLine("TEST_DIRECTORY=" + root);
    Console.WriteLine("Only synthetic local test data was used. Test files are retained for inspection.");
}
return result;

async Task<Process> StartServerAsync()
{
    var process = new Process { StartInfo = serverInfo };
    process.OutputDataReceived += (_, _) => { };
    process.ErrorDataReceived += (_, _) => { };
    try
    {
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("mountly-test:" + password)));
        for (var attempt = 0; ; attempt++)
        {
            try { using var response = await client.GetAsync(connection.Host); response.EnsureSuccessStatusCode(); return process; }
            catch when (attempt < 40 && !process.HasExited) { await Task.Delay(100); }
        }
    }
    catch
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        finally { process.Dispose(); }
        throw;
    }
}

async Task StopServerAsync()
{
    if (server is null) return;
    if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
    server.Dispose(); server = null;
}

// The integration harness exercises mounting, not the user's Explorer preferences.
internal sealed class NoOpDriveIcons : IMountDriveIconService
{
    public IDisposable? Apply(string driveLetter) => null;
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mountloom.Core.Models;
using Mountloom.Core.Services;

return await new ProtocolSmoke(args.FirstOrDefault()).RunAsync();

internal sealed class ProtocolSmoke(string? rcloneArgument)
{
    private readonly string _rclone = Path.GetFullPath(rcloneArgument ?? ".tools/rclone/rclone-v1.75.2-windows-amd64/rclone.exe");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "MountloomProtocolSmoke-" + Guid.NewGuid().ToString("N"));
    private readonly List<OwnedServer> _servers = [];
    private readonly List<string> _secrets = [];
    private int _passed;
    private int _failed;
    private AppSettings Settings => new() { RclonePath = _rclone };

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_scratch);
        try
        {
            Require(File.Exists(_rclone), "The supplied rclone executable does not exist.");
            var version = await CommandAsync(_rclone, ["--config", "NUL", "version"]);
            Console.WriteLine(version.StdOut.Split('\n')[0].Trim());
            Console.WriteLine("Only owned localhost servers, temporary keys/files, and environment credentials are used; no drive is mounted.");
            await ScenarioAsync("SFTP", SftpAsync);
            await ScenarioAsync("S3", S3Async);
        }
        catch (Exception error) { Fail("setup", error); }
        finally
        {
            foreach (var server in _servers)
            {
                try { await server.DisposeAsync(); }
                catch (Exception error) { Fail("owned server cleanup", error); }
            }
            try
            {
                var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(_scratch);
                Require(target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(target).StartsWith("MountloomProtocolSmoke-", StringComparison.Ordinal), "Unexpected scratch cleanup path.");
                Directory.Delete(target, recursive: true);
                Pass("cleanup: all owned servers exited and the temporary directory was removed");
            }
            catch (Exception error) { Fail("scratch cleanup", error); }
        }
        Console.WriteLine($"Result: {_passed} passed, {_failed} failed.");
        Console.WriteLine("Limit: rclone serve sftp exposes a virtual root; both path forms are exercised, but an external SSH server with a distinct home/root is not simulated.");
        return _failed == 0 ? 0 : 1;
    }

    private async Task SftpAsync()
    {
        var folder = Path.Combine(_scratch, "sftp");
        var data = Path.Combine(folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "projects", "nested"));
        await File.WriteAllTextAsync(Path.Combine(data, "projects", "nested", "sentinel.txt"), "sftp-owned-sentinel");
        await File.WriteAllTextAsync(Path.Combine(data, "outside.txt"), "outside-selected-path");
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");
        var keygen = Path.Combine(sshDirectory, "ssh-keygen.exe");
        var keyscan = Path.Combine(sshDirectory, "ssh-keyscan.exe");
        Require(File.Exists(keygen) && File.Exists(keyscan), "Windows OpenSSH keygen/keyscan are required.");
        var hostKey = Path.Combine(folder, "host_key");
        var clientKey = Path.Combine(folder, "client_key");
        await CommandAsync(keygen, ["-q", "-t", "ed25519", "-N", "", "-f", hostKey]);
        await CommandAsync(keygen, ["-q", "-t", "ed25519", "-N", "", "-f", clientKey]);
        var authorizedKeys = Path.Combine(folder, "authorized_keys");
        File.Copy(clientKey + ".pub", authorizedKeys);
        var user = "mountlysmoke";
        var password = NewSecret();
        var port = AllocatePort();
        var server = await StartServerAsync("sftp", data, port,
            ["--key", hostKey, "--authorized-keys", authorizedKeys],
            new Dictionary<string, string> { ["RCLONE_USER"] = user, ["RCLONE_PASS"] = password });
        var scanned = await CommandAsync(keyscan, ["-T", "5", "-t", "ed25519", "-p", port.ToString(), "127.0.0.1"]);
        var publicKey = (await File.ReadAllTextAsync(hostKey + ".pub")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = $"[127.0.0.1]:{port} {publicKey[0]} {publicKey[1]}";
        Require(scanned.StdOut.Split('\n').Any(line => line.Trim() == expected), "The scanned host key did not match our generated host key.");
        var knownHosts = Path.Combine(folder, "known_hosts");
        await File.WriteAllTextAsync(knownHosts, expected + "\n", new UTF8Encoding(false));
        Pass("SFTP: scanned known_hosts matches the temporary server key");

        var connection = new MountConnection
        {
            Protocol = MountProtocol.Sftp, Host = "127.0.0.1", Port = port, Username = user,
            Secret = password, SftpKnownHostsFile = knownHosts, RemotePath = "/projects/nested",
            CacheDirectory = Path.Combine(folder, "client-cache")
        };
        await new ConnectionTester().TestAsync(connection, Settings);
        Require(RcloneArguments.GetRemoteTarget(connection).EndsWith(":/projects/nested", StringComparison.Ordinal), "SFTP absolute path was changed.");
        await VerifySelectedFileAsync(connection, "sftp-owned-sentinel");
        Pass("SFTP: password authentication and absolute nested path read");
        connection.RemotePath = "projects/nested";
        Require(RcloneArguments.GetRemoteTarget(connection).EndsWith(":projects/nested", StringComparison.Ordinal), "SFTP relative path was changed.");
        await new ConnectionTester().TestAsync(connection, Settings);
        await VerifySelectedFileAsync(connection, "sftp-owned-sentinel");
        Pass("SFTP: relative nested path is preserved and readable");

        var keyConnection = connection.Clone();
        keyConnection.Secret = "";
        keyConnection.SftpAuthentication = SftpAuthenticationMode.KeyFile;
        keyConnection.SftpKeyFile = clientKey;
        await new ConnectionTester().TestAsync(keyConnection, Settings);
        await VerifySelectedFileAsync(keyConnection, "sftp-owned-sentinel");
        Pass("SFTP: explicit temporary private key authentication");

        var wrongPassword = connection.Clone();
        wrongPassword.Secret = NewSecret();
        await ExpectConnectionFailureAsync(wrongPassword, "SFTP: wrong password is rejected", "authenticate", "authentication");
        var wrongHost = connection.Clone();
        var otherPublicKey = (await File.ReadAllTextAsync(clientKey + ".pub")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wrongKnownHosts = Path.Combine(folder, "wrong_known_hosts");
        await File.WriteAllTextAsync(wrongKnownHosts, $"[127.0.0.1]:{port} {otherPublicKey[0]} {otherPublicKey[1]}\n", new UTF8Encoding(false));
        wrongHost.SftpKnownHostsFile = wrongKnownHosts;
        await ExpectConnectionFailureAsync(wrongHost, "SFTP: changed host key is rejected", "knownhosts", "key mismatch", "host key");
        await server.DisposeAsync();
    }

    private async Task S3Async()
    {
        var folder = Path.Combine(_scratch, "s3");
        var data = Path.Combine(folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "smoke-bucket", "prefix", "nested"));
        await File.WriteAllTextAsync(Path.Combine(data, "smoke-bucket", "prefix", "nested", "sentinel.txt"), "s3-owned-sentinel");
        await File.WriteAllTextAsync(Path.Combine(data, "smoke-bucket", "outside.txt"), "outside-selected-prefix");
        var accessKey = "mountly" + Guid.NewGuid().ToString("N");
        var secret = NewSecret();
        var port = AllocatePort();
        var server = await StartServerAsync("s3", data, port, [],
            new Dictionary<string, string> { ["RCLONE_AUTH_KEY"] = $"\"{accessKey},{secret}\"" });
        var connection = new MountConnection
        {
            Protocol = MountProtocol.S3, Host = "smoke-bucket", RemotePath = "/prefix/nested/",
            S3AccessKeyId = accessKey, Secret = secret, S3Region = "us-east-1", S3Endpoint = $"http://127.0.0.1:{port}",
            CacheDirectory = Path.Combine(folder, "client-cache")
        };
        await new ConnectionTester().TestAsync(connection, Settings);
        Require(RcloneArguments.GetRemoteTarget(connection).EndsWith(":smoke-bucket/prefix/nested", StringComparison.Ordinal), "S3 bucket/prefix target is incorrect.");
        await VerifySelectedFileAsync(connection, "s3-owned-sentinel");
        Pass("S3: signed authentication, explicit bucket and normalized nested prefix read");
        var pathOnly = connection.Clone();
        pathOnly.Host = "";
        pathOnly.RemotePath = "/smoke-bucket/prefix/nested/";
        await new ConnectionTester().TestAsync(pathOnly, Settings);
        await VerifySelectedFileAsync(pathOnly, "s3-owned-sentinel");
        Pass("S3: bucket supplied as the first path segment remains compatible");
        var wrongSecret = connection.Clone();
        wrongSecret.Secret = NewSecret();
        await ExpectConnectionFailureAsync(wrongSecret, "S3: wrong secret is rejected", "SignatureDoesNotMatch", "AccessDenied", "403");
        var wrongAccessKey = connection.Clone();
        wrongAccessKey.S3AccessKeyId = "unknown" + Guid.NewGuid().ToString("N");
        await ExpectConnectionFailureAsync(wrongAccessKey, "S3: unknown access key is rejected", "InvalidAccessKeyId", "AccessDenied", "403");
        await server.DisposeAsync();
    }

    private async Task VerifySelectedFileAsync(MountConnection connection, string expected)
    {
        var launch = await new RcloneConfigWriter().CreateAsync(connection, _rclone);
        var target = RcloneArguments.GetRemoteTarget(connection);
        var listed = await CommandAsync(_rclone, ["--config", "NUL", "lsf", target, "--max-depth", "1", "--retries", "1", "--low-level-retries", "1"], launch.Environment);
        Require(listed.StdOut.Trim() == "sentinel.txt", "The selected remote directory exposed unexpected entries: " + listed.StdOut.Trim());
        var content = await CommandAsync(_rclone, ["--config", "NUL", "cat", target.TrimEnd('/') + "/sentinel.txt", "--retries", "1", "--low-level-retries", "1"], launch.Environment);
        Require(content.StdOut == expected, "The selected file content did not match the owned sentinel.");
    }

    private async Task ExpectConnectionFailureAsync(MountConnection connection, string label, params string[] expectedReasons)
    {
        try { await new ConnectionTester().TestAsync(connection, Settings); }
        catch (InvalidOperationException error)
        {
            Require(expectedReasons.Any(reason => error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase)), "Connection failed for an unexpected reason: " + error.Message);
            Require(_secrets.All(secret => !error.Message.Contains(secret, StringComparison.Ordinal)), "Connection failure exposed a credential.");
            Pass(label);
            return;
        }
        throw new InvalidOperationException("An intentionally invalid connection unexpectedly succeeded.");
    }

    private async Task<OwnedServer> StartServerAsync(string protocol, string data, int port,
        IReadOnlyList<string> extra, IReadOnlyDictionary<string, string> environment)
    {
        List<string> arguments = ["--config", "NUL", "--cache-dir", Path.Combine(_scratch, protocol, "server-cache"),
            "serve", protocol, data, "--addr", $"127.0.0.1:{port}"];
        arguments.AddRange(extra);
        var server = new OwnedServer(ProcessRunner.CreateStartInfo(_rclone, arguments, environment));
        _servers.Add(server);
        await server.WaitUntilListeningAsync(port);
        return server;
    }

    private static async Task<ProcessOutput> CommandAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var output = await ProcessRunner.RunCaptureAsync(executable, arguments,
            environment: environment ?? new Dictionary<string, string>(), timeout: TimeSpan.FromSeconds(30));
        Require(output.ExitCode == 0, $"Command failed ({Path.GetFileName(executable)}): {output.StdErr.Trim()}");
        return output;
    }

    private async Task ScenarioAsync(string name, Func<Task> scenario)
    {
        try { await scenario(); }
        catch (Exception error) { Fail(name, error); }
    }

    private string NewSecret()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _secrets.Add(secret);
        return secret;
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Pass(string message) { _passed++; Console.WriteLine("PASS " + message); }
    private void Fail(string label, Exception error)
    {
        _failed++;
        var message = error.Message;
        foreach (var secret in _secrets) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        Console.WriteLine($"FAIL {label}: {message}");
    }
}

internal sealed class OwnedServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly Queue<string> _tail = new();
    private bool _disposed;

    public OwnedServer(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo };
        if (!_process.Start()) throw new InvalidOperationException("The localhost server could not start.");
        _stdout = ReadAsync(_process.StandardOutput);
        _stderr = ReadAsync(_process.StandardError);
    }

    public async Task WaitUntilListeningAsync(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                await Task.WhenAll(_stdout, _stderr);
                throw new InvalidOperationException("The localhost server exited: " + string.Join(" | ", _tail));
            }
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                // Detect a losing port allocation race before accepting another listener.
                await Task.Delay(100, deadline.Token);
                if (!_process.HasExited) return;
            }
            catch (SocketException) { }
            await Task.Delay(50, deadline.Token);
        }
        throw new TimeoutException("The owned localhost server did not listen within 10 seconds.");
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (_tail)
            {
                _tail.Enqueue(line.Length > 2048 ? line[..2048] : line);
                while (_tail.Count > 10) _tail.Dequeue();
            }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
        _process.Dispose();
        _disposed = true;
    }
}

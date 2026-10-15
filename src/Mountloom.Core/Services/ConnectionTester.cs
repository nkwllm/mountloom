using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

public sealed class ConnectionTester
{
    private readonly RcloneConfigWriter _writer = new();

    public async Task<string> TestAsync(MountConnection connection, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var snapshot = connection.Clone();
        ConnectionValidator.Validate(snapshot);
        var launch = await _writer.CreateAsync(snapshot, settings.RclonePath, cancellationToken,
            RcloneArguments.GetCacheDirectory(snapshot, settings));
        var result = await ProcessRunner.RunCaptureAsync(settings.RclonePath,
            ["--config", "NUL", "lsf", RcloneArguments.GetRemoteTarget(snapshot), "--max-depth", "1",
                "--contimeout", "10s", "--timeout", "15s", "--retries", "1", "--low-level-retries", "1"],
            cancellationToken, environment: launch.Environment, timeout: TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            var error = LogRedactor.Redact(result.StdErr, snapshot).Trim();
            if (error.Length > 2000) error = error[^2000..];
            throw new InvalidOperationException($"Connection test failed (exit {result.ExitCode}). {error}");
        }
        return "Connection succeeded. The selected remote directory is accessible.";
    }
}

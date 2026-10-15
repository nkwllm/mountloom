using System.Diagnostics;

namespace Mountloom.Core.Services;

public sealed class DependencyInstaller
{
    public event EventHandler<string>? OutputReceived;

    public async Task InstallRcloneAsync(CancellationToken cancellationToken = default)
    {
        var runtime = new RcloneRuntimeService();
        runtime.ProgressChanged += message => OutputReceived?.Invoke(this, message);
        await runtime.EnsureAsync(cancellationToken);
    }

    public Task InstallWinFspAsync(CancellationToken cancellationToken = default)
    {
        return RunWingetAsync("WinFsp.WinFsp", cancellationToken);
    }

    public async Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunCaptureAsync("winget", "--version", cancellationToken);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    private async Task RunWingetAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!await IsWingetAvailableAsync(cancellationToken))
        {
            throw new InvalidOperationException("winget was not found. Install App Installer from Microsoft Store or install the dependency manually.");
        }

        string[] arguments = ["install", "--id", packageId, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];

        OutputReceived?.Invoke(this, $"Starting winget install for {packageId}...");

        using var process = new Process();
        process.StartInfo = ProcessRunner.CreateStartInfo("winget", arguments);
        process.OutputDataReceived += (_, args) => Emit(args.Data);
        process.ErrorDataReceived += (_, args) => Emit(args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("Dependency installation timed out.");
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"winget install failed for {packageId}. Exit code: {process.ExitCode}.");
        }

        OutputReceived?.Invoke(this, $"{packageId} installation finished.");
    }

    private void Emit(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            OutputReceived?.Invoke(this, line);
        }
    }
}

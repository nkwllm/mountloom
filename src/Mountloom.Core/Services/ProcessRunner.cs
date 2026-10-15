using System.Diagnostics;
using System.Text;

namespace Mountloom.Core.Services;

public static class ProcessRunner
{
    public static Task<ProcessOutput> RunCaptureAsync(string fileName, string arguments,
        CancellationToken cancellationToken = default) =>
        RunCaptureAsync(fileName, CommandLineArguments.Parse(arguments), cancellationToken);

    public static async Task<ProcessOutput> RunCaptureAsync(string fileName, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, environment) };
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        if (standardInput is not null) process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
        if (!process.Start()) throw new InvalidOperationException("The command could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            return new ProcessOutput(await stdout, await stderr, process.ExitCode);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                process.StandardOutput.Close();
                process.StandardError.Close();
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException) { }
            }
            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("The command timed out. Check the connection and try again.");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maxCharacters = 128 * 1024;
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > maxCharacters) text.Remove(0, text.Length - maxCharacters);
        }
        return text.ToString();
    }

    public static ProcessStartInfo CreateStartInfo(string fileName, string arguments) =>
        CreateStartInfo(fileName, CommandLineArguments.Parse(arguments));

    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
        {
            // Isolate managed mounts from inherited rclone options, credentials and RC settings.
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(key);
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        }
        return info;
    }
}

public sealed record ProcessOutput(string StdOut, string StdErr, int ExitCode);

using System.Diagnostics;
using System.Text;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mountloom.ProcessRunnerTests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _ownedProcesses = [];
    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public ProcessRunnerTests() => Directory.CreateDirectory(_directory);

    public static IEnumerable<object[]> CommandLines()
    {
        yield return ["one two", new[] { "one", "two" }];
        yield return ["\"\" plain", new[] { "", "plain" }];
        yield return ["--path \"C:\\folder with spaces\\\\\"", new[] { "--path", "C:\\folder with spaces\\" }];
        yield return ["a\\\"b", new[] { "a\"b" }];
        yield return ["\"a\"\"b\"", new[] { "a\"b" }];
        yield return ["one 'two three'", new[] { "one", "'two", "three'" }];
    }

    [Theory]
    [MemberData(nameof(CommandLines))]
    public void WindowsCommandLineParsingPreservesQuotesBackslashesAndEmptyArguments(string commandLine, string[] expected)
    {
        Assert.Equal(expected, CommandLineArguments.Parse(commandLine));
    }

    [Fact]
    public void UnclosedQuotesAreRejectedInsteadOfChangingArgumentBoundaries()
    {
        Assert.Throws<ArgumentException>(() => CommandLineArguments.Parse("--path \"unterminated"));
    }

    [Fact]
    public void SpecialArgumentsArePassedViaArgumentListWithoutShellEvaluation()
    {
        var arguments = new[] { "", "C:\\trailing slash\\", "literal\"quote", "$([Environment]::Exit(99))", "a&b;c", "中文" };

        var start = ProcessRunner.CreateStartInfo("example.exe", arguments);

        Assert.Equal(arguments, start.ArgumentList);
        Assert.Empty(start.Arguments);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public void ManagedEnvironmentRemovesInheritedRcloneOptionsAndOnlyAddsTheSelectedRemote()
    {
        var key = "RCLONE_MOUNTLY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "test-parent-secret");
        try
        {
            var selected = new Dictionary<string, string> { ["RCLONE_CONFIG_TEST_TYPE"] = "webdav" };

            var start = ProcessRunner.CreateStartInfo("example.exe", Array.Empty<string>(), selected);

            Assert.False(start.Environment.ContainsKey(key));
            Assert.Equal("webdav", start.Environment["RCLONE_CONFIG_TEST_TYPE"]);
            Assert.Single(start.Environment.Keys, name => name.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task StandardInputUnicodeAndNonzeroExitAreCapturedWithoutPuttingTheSecretInArguments()
    {
        const string input = "机密 with quotes \" and trailing slash\\\nsecond line";
        var script = "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                     "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                     "$inputText = [Console]::In.ReadToEnd(); [Console]::Out.Write($inputText); " +
                     "[Console]::Error.Write('separate-error'); exit 7";
        var arguments = EncodedScript(script);

        var output = await ProcessRunner.RunCaptureAsync(PowerShellPath, arguments, standardInput: input, timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(input, output.StdOut);
        Assert.Equal("separate-error", output.StdErr);
        Assert.Equal(7, output.ExitCode);
        Assert.DoesNotContain(input, arguments);
    }

    [Fact]
    public async Task BothOutputStreamsAreDrainedAndBoundedWithoutPipeDeadlock()
    {
        var script = "$chunk = 'x' * 4096; for ($i = 0; $i -lt 64; $i++) { " +
                     "[Console]::Out.Write($chunk); [Console]::Error.Write($chunk) }; " +
                     "[Console]::Out.Write('stdout-tail'); [Console]::Error.Write('stderr-tail')";

        var output = await ProcessRunner.RunCaptureAsync(PowerShellPath, EncodedScript(script), timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, output.ExitCode);
        Assert.True(output.StdOut.Length <= 128 * 1024);
        Assert.True(output.StdErr.Length <= 128 * 1024);
        Assert.EndsWith("stdout-tail", output.StdOut);
        Assert.EndsWith("stderr-tail", output.StdErr);
    }

    [Fact]
    public async Task TimeoutTerminatesTheOwnedProcessBeforeReturning()
    {
        var pidPath = Path.Combine(_directory, "timeout.pid");
        var environment = new Dictionary<string, string> { ["MOUNTLY_TEST_PID"] = pidPath };
        var command = EncodedScript("[IO.File]::WriteAllText($env:MOUNTLY_TEST_PID, [string]$PID); Start-Sleep -Seconds 60");
        var timer = Stopwatch.StartNew();
        var run = ProcessRunner.RunCaptureAsync(PowerShellPath, command, environment: environment, timeout: TimeSpan.FromSeconds(4));
        var process = await TrackPidFileAsync(pidPath);

        await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));

        Assert.True(process.HasExited);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "The configured timeout must bound the operation itself.");
    }

    [Fact]
    public async Task CancellationTerminatesTheOwnedProcessAndItsChild()
    {
        var pidPath = Path.Combine(_directory, "parent.pid");
        var childPidPath = Path.Combine(_directory, "child.pid");
        var childScript = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
        var script = "[IO.File]::WriteAllText($env:MOUNTLY_TEST_PID, [string]$PID); " +
                     "$child = Start-Process -FilePath $env:MOUNTLY_TEST_POWERSHELL -WindowStyle Hidden -PassThru " +
                     "-ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $env:MOUNTLY_TEST_CHILD_SCRIPT); " +
                     "[IO.File]::WriteAllText($env:MOUNTLY_TEST_CHILD_PID, [string]$child.Id); Start-Sleep -Seconds 60";
        var environment = new Dictionary<string, string>
        {
            ["MOUNTLY_TEST_PID"] = pidPath, ["MOUNTLY_TEST_CHILD_PID"] = childPidPath,
            ["MOUNTLY_TEST_POWERSHELL"] = PowerShellPath, ["MOUNTLY_TEST_CHILD_SCRIPT"] = childScript
        };
        using var cancellation = new CancellationTokenSource();
        var run = ProcessRunner.RunCaptureAsync(PowerShellPath, EncodedScript(script), cancellation.Token,
            environment: environment, timeout: TimeSpan.FromSeconds(20));
        var parent = await TrackPidFileAsync(pidPath);
        var child = await TrackPidFileAsync(childPidPath);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(parent.HasExited);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task AlreadyCancelledRunDoesNotLaunchTheExecutable()
    {
        var outputPath = Path.Combine(_directory, "must-not-exist");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var environment = new Dictionary<string, string> { ["MOUNTLY_TEST_PATH"] = outputPath };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunCaptureAsync(PowerShellPath,
            EncodedScript("[IO.File]::WriteAllText($env:MOUNTLY_TEST_PATH, 'unexpected')"), cancellation.Token, environment: environment));

        Assert.False(File.Exists(outputPath));
    }

    private static string[] EncodedScript(string script) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];

    private async Task<Process> TrackPidFileAsync(string path)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                if (int.TryParse(text, out var pid))
                {
                    var process = Process.GetProcessById(pid);
                    _ = process.Handle; // Keep an OS handle so cleanup cannot affect a reused PID.
                    _ownedProcesses.Add(process);
                    return process;
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The isolated test process did not report its PID.");
    }

    public void Dispose()
    {
        foreach (var process in _ownedProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            finally { process.Dispose(); }
        }
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Mountloom.ProcessRunnerTests"));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_directory)), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

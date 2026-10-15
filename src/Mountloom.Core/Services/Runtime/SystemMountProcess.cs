using System.Diagnostics;
using System.Text;

namespace Mountloom.Core.Services.Runtime;

public sealed class SystemMountProcessFactory : IMountProcessFactory
{
    public IMountProcess Create(ProcessStartInfo startInfo) => new SystemMountProcess(startInfo);
}

internal sealed class SystemMountProcess(ProcessStartInfo startInfo) : IMountProcess
{
    private readonly Process _process = new() { StartInfo = startInfo };
    private Task _output = Task.CompletedTask;
    private Task _error = Task.CompletedTask;
    private bool _started;

    public int Id => _process.Id;
    public bool HasExited => _started && _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public event EventHandler<ProcessLogLine>? OutputReceived;

    public void Start()
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException("rclone process did not start.");
        }
        _started = true;
        _output = ReadLinesAsync(_process.StandardOutput, false);
        _error = ReadLinesAsync(_process.StandardError, true);
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_output, _error).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill()
    {
        if (!HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => _process.Dispose();

    private async Task ReadLinesAsync(StreamReader reader, bool isError)
    {
        const int maxLineLength = 16_384;
        var buffer = new char[2048];
        var line = new StringBuilder();
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    Emit(line, truncated, isError);
                    line.Clear();
                    truncated = false;
                }
                else if (line.Length < maxLineLength)
                {
                    line.Append(buffer[i]);
                }
                else
                {
                    truncated = true;
                }
            }
        }
        Emit(line, truncated, isError);
    }

    private void Emit(StringBuilder line, bool truncated, bool isError)
    {
        if (line.Length == 0)
        {
            return;
        }
        var text = line.ToString().TrimEnd('\r') + (truncated ? " [line truncated]" : string.Empty);
        OutputReceived?.Invoke(this, new ProcessLogLine(text, isError));
    }
}

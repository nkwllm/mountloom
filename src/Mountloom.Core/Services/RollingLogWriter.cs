using System.Text;
using System.Threading.Channels;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.Core.Services;

/// <summary>A bounded, asynchronous log sink. Two previous 2 MiB files are retained per connection.</summary>
public sealed class RollingLogWriter : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Channel<MountLogEntry> _queue = Channel.CreateBounded<MountLogEntry>(new BoundedChannelOptions(2000)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _worker;
    public string? LastError { get; private set; }

    public RollingLogWriter(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _worker = Task.Run(WriteLoopAsync);
    }

    public void Write(MountLogEntry entry) => _queue.Writer.TryWrite(entry);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var id = entry.ConnectionId;
                if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) id = "application";
                var path = Path.Combine(_directory, id + ".log");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                {
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", overwrite: true);
                    File.Move(path, path + ".1", overwrite: true);
                }
                var message = entry.Message.Length > 8192 ? entry.Message[..8192] + " [truncated]" : entry.Message;
                await File.AppendAllTextAsync(path, $"[{entry.Timestamp:O}] {message}{Environment.NewLine}", Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { LastError = "Diagnostic logs could not be saved: " + error.Message; }
        }
    }
}

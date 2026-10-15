using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mountloom.Core.Services.Runtime;

public sealed class RcloneMountControlFactory : IMountControlFactory
{
    public IMountControl Create() => new RcloneMountControl();
}

/// <summary>Private, authenticated RC endpoint for a single child process.</summary>
public sealed class RcloneMountControl : IMountControl
{
    private readonly HttpClient _client;
    public IReadOnlyList<string> Arguments { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }

    public RcloneMountControl()
    {
        // rclone must bind the port itself. Unique credentials and PID verification make a
        // port-reuse race fail closed instead of controlling an unrelated RC instance.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var user = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Arguments = ["--rc", "--rc-addr", $"127.0.0.1:{port}", "--rc-no-auth=false", "--rc-web-gui=false", "--rc-serve=false"];
        Environment = new Dictionary<string, string>
        {
            ["RCLONE_RC_USER"] = user,
            ["RCLONE_RC_PASS"] = password
        };
        _client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBufferSize = 256 * 1024
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    public async Task<int> GetProcessIdAsync(CancellationToken cancellationToken)
    {
        using var response = await PostAsync("core/pid", cancellationToken).ConfigureAwait(false);
        if (!response.RootElement.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var value))
        {
            throw new InvalidOperationException("rclone RC returned no process identity.");
        }
        return value;
    }

    public async Task<MountVfsStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        using var response = await PostAsync("vfs/stats", cancellationToken).ConfigureAwait(false);
        return ParseStats(response.RootElement);
    }

    public static MountVfsStats ParseStats(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fs", out _))
        {
            throw new InvalidOperationException("rclone did not report an active VFS.");
        }

        // inUse is the VFS reference count, not the count of open files. Older/current
        // rclone versions do not expose openFiles; retain null rather than inventing zero.
        var open = Number(root, "openFiles");
        var hasCache = root.TryGetProperty("diskCache", out var cache) && cache.ValueKind == JsonValueKind.Object;
        return hasCache
            ? new MountVfsStats(open ?? Number(cache, "openFiles"), Number(cache, "uploadsQueued"),
                Number(cache, "uploadsInProgress"), Number(cache, "erroredFiles"), Number(cache, "bytesUsed"),
                Boolean(cache, "outOfSpace"), true)
            : new MountVfsStats(open, null, null, null, null, null, false);
    }

    public async Task QuitAsync(CancellationToken cancellationToken)
    {
        using var response = await PostAsync("core/quit", cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private async Task<JsonDocument> PostAsync(string method, CancellationToken cancellationToken)
    {
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(method, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"rclone RC {method} failed (HTTP {(int)response.StatusCode}).");
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    private static long? Number(JsonElement root, string key) =>
        root.TryGetProperty(key, out var node) && node.TryGetInt64(out var value) && value >= 0 ? value : null;

    private static bool? Boolean(JsonElement root, string key) => root.TryGetProperty(key, out var node)
        ? node.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;
}

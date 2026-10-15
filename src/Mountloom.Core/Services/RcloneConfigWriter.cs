using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mountloom.Core.Models;
using Mountloom.Core.Security;

namespace Mountloom.Core.Services;

/// <summary>Creates an isolated child environment, with DPAPI-protected stable credential representations.</summary>
public sealed class RcloneConfigWriter
{
    private readonly Func<string, string, CancellationToken, Task<string>> _obscure;

    public RcloneConfigWriter() : this(ObscureAsync) { }
    public RcloneConfigWriter(Func<string, string, CancellationToken, Task<string>> obscure) => _obscure = obscure;

    public async Task<RcloneLaunchConfiguration> CreateAsync(MountConnection connection, string rclonePath,
        CancellationToken cancellationToken = default, string? credentialCacheDirectory = null)
    {
        ConnectionValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();
        var remoteName = GetRemoteName(connection);
        var prefix = $"RCLONE_CONFIG_{remoteName.ToUpperInvariant()}_";
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cache = credentialCacheDirectory is null ? null : new RcloneCredentialCache(credentialCacheDirectory);
        void Set(string key, string value) => environment[prefix + key.ToUpperInvariant()] = value;
        async Task SetPassword(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var obscured = cache is null ? await _obscure(rclonePath, value, cancellationToken)
                : await cache.GetOrCreateAsync(value, () => _obscure(rclonePath, value, cancellationToken), cancellationToken);
            Set(key, obscured);
        }

        switch (connection.Protocol)
        {
            case MountProtocol.WebDav:
                Set("type", "webdav"); Set("url", connection.Host); Set("vendor", "other");
                Set("user", connection.Username);
                await SetPassword("pass", connection.Secret);
                break;
            case MountProtocol.Sftp:
                Set("type", "sftp"); Set("host", connection.Host.Trim('[', ']')); Set("user", connection.Username);
                Set("port", (connection.Port == 0 ? 22 : connection.Port).ToString(CultureInfo.InvariantCulture));
                Set("known_hosts_file", Path.GetFullPath(connection.SftpKnownHostsFile));
                switch (connection.SftpAuthentication)
                {
                    case SftpAuthenticationMode.Password:
                        await SetPassword("pass", connection.Secret);
                        break;
                    case SftpAuthenticationMode.KeyFile:
                        Set("key_file", Path.GetFullPath(connection.SftpKeyFile));
                        await SetPassword("key_file_pass", connection.SftpKeyPassphrase);
                        break;
                    case SftpAuthenticationMode.Agent:
                        Set("key_use_agent", "true");
                        break;
                }
                break;
            case MountProtocol.S3:
                Set("type", "s3"); Set("provider", string.IsNullOrWhiteSpace(connection.S3Endpoint) ? "AWS" : "Other");
                Set("env_auth", "false"); Set("access_key_id", connection.S3AccessKeyId);
                Set("secret_access_key", connection.Secret);
                if (!string.IsNullOrWhiteSpace(connection.S3Region)) Set("region", connection.S3Region);
                if (!string.IsNullOrWhiteSpace(connection.S3Endpoint)) Set("endpoint", connection.S3Endpoint);
                break;
            case MountProtocol.Ftp:
                Set("type", "ftp"); Set("host", connection.Host.Trim('[', ']')); Set("user", connection.Username);
                Set("port", (connection.Port == 0 ? 21 : connection.Port).ToString(CultureInfo.InvariantCulture));
                await SetPassword("pass", connection.Secret);
                break;
            default: throw new InvalidOperationException("Unsupported protocol.");
        }
        return new RcloneLaunchConfiguration(remoteName, environment);
    }

    public static string GetRemoteName(MountConnection connection)
    {
        // Preserve existing GUID-based names so pending VFS uploads retain their cache identity.
        var id = connection.Id;
        if (id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return $"mountly_{id}";
        return "mountly_id_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    }

    private static async Task<string> ObscureAsync(string rclonePath, string value, CancellationToken cancellationToken)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("Passwords cannot contain line breaks or NUL characters.");
        var output = await ProcessRunner.RunCaptureAsync(rclonePath, ["--config", "NUL", "obscure", "-"], cancellationToken,
            standardInput: value + "\n", environment: new Dictionary<string, string>(), timeout: TimeSpan.FromSeconds(10));
        if (output.ExitCode != 0 || string.IsNullOrWhiteSpace(output.StdOut))
            throw new InvalidOperationException("rclone could not prepare the credential. Check the selected rclone executable.");
        return output.StdOut.Trim();
    }
}

public sealed record RcloneLaunchConfiguration(string RemoteName, IReadOnlyDictionary<string, string> Environment);

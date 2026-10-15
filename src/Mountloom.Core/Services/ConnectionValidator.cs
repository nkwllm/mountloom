using System.Text.RegularExpressions;
using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

public static partial class ConnectionValidator
{
    public static void Validate(MountConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!SafeId().IsMatch(connection.Id ?? string.Empty)) throw new InvalidOperationException("The connection ID is invalid.");
        if (!Enum.IsDefined(connection.Protocol) || !Enum.IsDefined(connection.CacheMode))
            throw new InvalidOperationException("Select a supported protocol and cache mode.");
        if (connection.Port is < 0 or > 65535)
            throw new InvalidOperationException("Port must be between 1 and 65535, or 0 for the default.");
        foreach (var text in new[] { connection.Host, connection.Username, connection.RemotePath,
                     connection.S3Endpoint, connection.S3Region, connection.S3AccessKeyId })
            if (text is null || text.Any(char.IsControl)) throw new InvalidOperationException("Connection fields cannot contain control characters.");

        switch (connection.Protocol)
        {
            case MountProtocol.WebDav:
                if (!IsHttpUrl(connection.Host)) throw new InvalidOperationException("Enter a complete WebDAV http:// or https:// URL without embedded credentials.");
                break;
            case MountProtocol.Sftp:
            case MountProtocol.Ftp:
                if (string.IsNullOrWhiteSpace(connection.Host) || Uri.CheckHostName(connection.Host.Trim('[', ']')) == UriHostNameType.Unknown)
                    throw new InvalidOperationException("Enter the server host name. Set the port in its separate field.");
                if (connection.Protocol == MountProtocol.Sftp)
                {
                    if (!Enum.IsDefined(connection.SftpAuthentication)) throw new InvalidOperationException("Select an SFTP authentication method.");
                    if (string.IsNullOrWhiteSpace(connection.SftpKnownHostsFile) || !File.Exists(connection.SftpKnownHostsFile))
                        throw new InvalidOperationException("Select an existing known_hosts file containing the verified server key before connecting.");
                    if (connection.SftpAuthentication == SftpAuthenticationMode.KeyFile && !File.Exists(connection.SftpKeyFile))
                        throw new InvalidOperationException("Select an existing SFTP private key file.");
                }
                break;
            case MountProtocol.S3:
                if (!string.IsNullOrWhiteSpace(connection.S3Endpoint) && !IsHttpUrl(connection.S3Endpoint))
                    throw new InvalidOperationException("S3 endpoint must be an http:// or https:// URL without embedded credentials.");
                var bucket = string.IsNullOrWhiteSpace(connection.Host)
                    ? connection.RemotePath.Trim('/').Split('/')[0] : connection.Host;
                if (!BucketName().IsMatch(bucket)) throw new InvalidOperationException("Enter an S3 bucket name; place the folder path in Prefix.");
                if (string.IsNullOrWhiteSpace(connection.S3AccessKeyId) || string.IsNullOrEmpty(connection.Secret))
                    throw new InvalidOperationException("Enter the S3 access key ID and secret access key.");
                break;
        }
        foreach (var value in new[] { connection.VfsCacheMaxSize, connection.VfsCacheMinFreeSpace })
            if (string.IsNullOrWhiteSpace(value) || !SizeValue().IsMatch(value))
                throw new InvalidOperationException("Cache limits must be sizes such as 10G, 512M, or 0.");
        RcloneArguments.ParseAdvanced(connection.AdvancedArguments);
    }

    private static bool IsHttpUrl(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex SafeId();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$")] private static partial Regex BucketName();
    [GeneratedRegex(@"^\d+(?:\.\d+)?(?:[kKmMgGtTpPeE](?:i?[bB])?|[bB])?$")] private static partial Regex SizeValue();
}

using System.Text;
using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

public static class RcloneArguments
{
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--buffer-size", "--vfs-read-ahead", "--dir-cache-time", "--poll-interval", "--vfs-cache-max-age",
        "--vfs-write-back", "--timeout", "--contimeout", "--transfers", "--checkers", "--bwlimit",
        "--retries", "--low-level-retries", "--tpslimit", "--tpslimit-burst", "--vfs-read-chunk-size",
        "--vfs-read-chunk-size-limit", "--vfs-read-chunk-streams", "--log-level"
    };
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    { "--no-modtime", "--use-server-modtime", "--vfs-fast-fingerprint", "--no-seek" };

    public static IReadOnlyList<string> BuildMount(MountConnection connection, AppSettings settings)
    {
        ConnectionValidator.Validate(connection);
        var arguments = new List<string>
        {
            "--config", "NUL", "mount", GetRemoteTarget(connection), DriveLetterService.Normalize(connection.DriveLetter),
            "--vfs-cache-mode", connection.CacheMode.ToString().ToLowerInvariant(),
            "--cache-dir", GetCacheDirectory(connection, settings),
            "--vfs-cache-max-size", connection.VfsCacheMaxSize,
            "--vfs-cache-min-free-space", connection.VfsCacheMinFreeSpace,
            "--dir-cache-time", "1h", "--poll-interval", "1m", "--buffer-size", "16M",
            "--vfs-read-ahead", "128M", "--network-mode", "--volname", GetVolumeName(connection),
            "--log-level", "INFO"
        };
        if (connection.ReadOnly) arguments.Add("--read-only");
        arguments.AddRange(ParseAdvanced(connection.AdvancedArguments));
        return arguments;
    }

    private static string GetVolumeName(MountConnection connection)
    {
        // In network mode rclone uses this as the UNC share name shown in Explorer.
        // Keep it separate from the stable remote name used for credentials and VFS caches.
        const string invalidShareCharacters = "\\/[]:|¦<>+=;,*?\"";
        var label = new StringBuilder();
        var byteCount = 0;
        foreach (var input in (connection.Name ?? string.Empty).Trim().EnumerateRunes())
        {
            var rune = Rune.IsControl(input) || input == Rune.ReplacementChar
                || (input.IsBmp && invalidShareCharacters.Contains((char)input.Value))
                ? new Rune('_') : input;
            // rclone 1.75.2 truncates Windows volume names at 32 UTF-8 bytes. Stop at
            // a whole Unicode scalar here so Chinese names and emoji cannot be split.
            if (byteCount + rune.Utf8SequenceLength > 32) break;
            label.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }
        var name = label.ToString().Trim().TrimEnd(' ', '.');
        return name.Trim('_', '.', ' ').Length == 0
            ? $"Mountloom {DriveLetterService.Validate(connection.DriveLetter)[0]}" : name;
    }

    public static string GetRemoteTarget(MountConnection connection)
    {
        var path = connection.RemotePath.Trim();
        if (connection.Protocol == MountProtocol.S3)
        {
            path = path.Trim('/');
            if (!string.IsNullOrWhiteSpace(connection.Host))
                path = string.IsNullOrEmpty(path) ? connection.Host : $"{connection.Host}/{path}";
        }
        else if (connection.Protocol == MountProtocol.WebDav) path = path.TrimStart('/');
        return $"{RcloneConfigWriter.GetRemoteName(connection)}:{path}";
    }

    public static string GetCacheDirectory(MountConnection connection, AppSettings settings) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(connection.CacheDirectory)
            ? Path.Combine(settings.CacheRoot, connection.Id) : connection.CacheDirectory);

    public static IReadOnlyList<string> ParseAdvanced(string arguments)
    {
        var tokens = CommandLineArguments.Parse(arguments ?? string.Empty);
        for (var index = 0; index < tokens.Count; index++)
        {
            var split = tokens[index].Split('=', 2);
            var flag = split[0];
            if (BooleanFlags.Contains(flag))
            {
                if (split.Length == 2 && split[1] is not ("true" or "false"))
                    throw new InvalidOperationException($"{flag} expects true or false.");
            }
            else if (ValueFlags.Contains(flag))
            {
                var value = split.Length == 2 ? split[1] : ++index < tokens.Count ? tokens[index] : string.Empty;
                if (string.IsNullOrEmpty(value) || value.StartsWith("--"))
                    throw new InvalidOperationException($"{flag} requires a value.");
            }
            else throw new InvalidOperationException($"The advanced option '{flag}' is not supported. Configure authentication, cache location and mount control in their dedicated fields.");
        }
        return tokens;
    }
}

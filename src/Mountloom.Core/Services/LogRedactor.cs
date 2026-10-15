using System.Text.RegularExpressions;
using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

public static partial class LogRedactor
{
    public static string Redact(string text, MountConnection connection)
    {
        foreach (var value in new[] { connection.Secret, connection.S3AccessKeyId, connection.SftpKeyPassphrase }
                     .Where(v => !string.IsNullOrEmpty(v)).OrderByDescending(v => v.Length))
        {
            text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(value), "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }
        text = CredentialAssignment().Replace(text, "$1[REDACTED]");
        return UrlCredentials().Replace(text, "$1[REDACTED]@");
    }

    [GeneratedRegex(@"(?i)((?:pass(?:word)?|secret(?:_access_key)?|access_key_id|key_file_pass|authorization|token)\s*[:=]\s*)[^\s,;]+")]
    private static partial Regex CredentialAssignment();
    [GeneratedRegex(@"(https?://)[^/@\s]+:[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();
}

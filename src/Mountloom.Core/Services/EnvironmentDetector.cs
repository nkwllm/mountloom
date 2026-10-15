using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mountloom.Core.Models;

namespace Mountloom.Core.Services;

public sealed class EnvironmentDetector
{
    public async Task<EnvironmentStatus> DetectAsync(string configuredRclonePath, CancellationToken cancellationToken = default)
    {
        var status = new EnvironmentStatus();
        var path = ResolveExecutable(configuredRclonePath);
        var failure = string.Empty;
        if (!string.IsNullOrWhiteSpace(path))
        {
            status.RclonePath = path;
            try
            {
                var result = await ProcessRunner.RunCaptureAsync(path, ["version"], cancellationToken,
                    environment: new Dictionary<string, string>(), timeout: TimeSpan.FromSeconds(8));
                var version = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                status.RcloneFound = result.ExitCode == 0 && version.StartsWith("rclone v", StringComparison.OrdinalIgnoreCase);
                status.RcloneVersion = status.RcloneFound ? version : "Executable did not return a valid rclone version.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException)
            { failure = ex.Message; }
        }
        status.WinFspVersion = ReadWinFspVersion();
        status.WinFspFound = !string.IsNullOrEmpty(status.WinFspVersion);
        status.Message = status switch
        {
            { RcloneFound: true, WinFspFound: true } => "rclone and WinFsp are installed. Mount availability is verified when connecting.",
            { RcloneFound: false, WinFspFound: true } => "The managed rclone runtime is not ready. Retry automatic dependency preparation.",
            { RcloneFound: true, WinFspFound: false } => "WinFsp for this process architecture was not detected. Install WinFsp before mounting.",
            _ => "Automatic rclone preparation and a matching WinFsp installation are required."
        };
        if (!string.IsNullOrEmpty(failure)) status.Message += " " + failure;
        return status;
    }

    public static string ResolveExecutable(string configuredPath)
    {
        // The application supplies RcloneRuntimeService's verified result. Explicit paths remain
        // supported for isolated integration runners; PATH and legacy settings are not consulted.
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathFullyQualified(configuredPath)
            && File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
        return string.Empty;
    }

    public static string ReadWinFspVersion()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        { Architecture.X86 => "x86", Architecture.Arm64 => "a64", _ => "x64" };
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\WinFsp");
            foreach (var value in new[] { "SxsDir", "InstallDir" })
            {
                if (key?.GetValue(value) is not string directory) continue;
                var library = Path.Combine(directory, "bin", $"winfsp-{architecture}.dll");
                if (!File.Exists(library)) continue;
                return FileVersionInfo.GetVersionInfo(library).FileVersion ?? "Installed (version unknown)";
            }
        }
        return string.Empty;
    }
}

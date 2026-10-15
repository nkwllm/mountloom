using Microsoft.Win32;

namespace Mountloom.Core.Services;

public sealed class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Reuse the original value so an explicit startup-setting change cannot create a second entry.
    private const string RunValue = "Mountly";
    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string value && string.Equals(value, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, StartupCommand());
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static string StartupCommand()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Mountloom.App.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Login startup requires the published Mountloom.App.exe. Publish the application first.");
        return $"\"{executable}\"";
    }
}

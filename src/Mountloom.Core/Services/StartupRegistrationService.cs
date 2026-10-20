using Microsoft.Win32;

namespace Mountloom.Core.Services;

public sealed class StartupRegistrationService
{
    public const string StartupArgument = "--startup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Reuse the original value so startup-setting changes cannot create a second entry.
    private const string RunValue = "Mountly";
    private readonly string _runKey;
    private readonly string _executable;

    public StartupRegistrationService() : this(RunKey, Path.Combine(AppContext.BaseDirectory, "Mountloom.App.exe")) { }

    internal StartupRegistrationService(string runKey, string executable)
    {
        _runKey = runKey;
        _executable = executable;
    }

    public static bool IsStartupLaunch(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        if (key?.GetValue(RunValue) is not string value) return false;
        var legacyCommand = LegacyStartupCommand();
        return string.Equals(value, legacyCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, legacyCommand + " " + StartupArgument, StringComparison.OrdinalIgnoreCase);
    }

    public void UpgradeLegacyRegistration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        if (key?.GetValue(RunValue) is not string value) return;
        var legacyCommand = LegacyStartupCommand();
        if (string.Equals(value, legacyCommand, StringComparison.OrdinalIgnoreCase))
            key.SetValue(RunValue, legacyCommand + " " + StartupArgument);
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(_runKey);
        if (enabled) key.SetValue(RunValue, LegacyStartupCommand() + " " + StartupArgument);
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private string LegacyStartupCommand()
    {
        if (!File.Exists(_executable)) throw new InvalidOperationException("Login startup requires the published Mountloom.App.exe. Publish the application first.");
        return $"\"{_executable}\"";
    }
}

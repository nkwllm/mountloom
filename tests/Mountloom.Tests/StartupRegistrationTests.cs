using Microsoft.Win32;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _runKey = @"Software\Mountloom.Tests\StartupRegistration\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mountloom StartupRegistration Tests", Guid.NewGuid().ToString("N"));
    private readonly string _executable;
    private readonly StartupRegistrationService _service;

    public StartupRegistrationTests()
    {
        Directory.CreateDirectory(_directory);
        _executable = Path.Combine(_directory, "Mountloom.App.exe");
        File.WriteAllBytes(_executable, []);
        _service = new StartupRegistrationService(_runKey, _executable);
    }

    [Fact]
    public void EnablingQuotesExecutableAndAddsStartupArgument()
    {
        _service.SetEnabled(true);

        Assert.Equal($"\"{_executable}\" --startup", ReadCommand());
        Assert.True(_service.IsEnabled());
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        Assert.Equal(["Mountly"], key!.GetValueNames());
    }

    [Fact]
    public void LegacyRegistrationIsRecognizedWithoutWritingThenUpgraded()
    {
        var legacyCommand = $"\"{_executable}\"";
        WriteCommand(legacyCommand);

        Assert.True(_service.IsEnabled());
        Assert.Equal(legacyCommand, ReadCommand());

        _service.UpgradeLegacyRegistration();

        Assert.Equal(legacyCommand + " --startup", ReadCommand());
        Assert.True(_service.IsEnabled());
        _service.UpgradeLegacyRegistration();
        Assert.Equal(legacyCommand + " --startup", ReadCommand());
    }

    [Fact]
    public void UpgradeDoesNotEnableMissingOrDisabledRegistration()
    {
        _service.UpgradeLegacyRegistration();

        Assert.False(_service.IsEnabled());
        using (var key = Registry.CurrentUser.OpenSubKey(_runKey)) Assert.Null(key);

        _service.SetEnabled(true);
        _service.SetEnabled(false);
        _service.UpgradeLegacyRegistration();

        Assert.False(_service.IsEnabled());
        Assert.Null(ReadCommand());
    }

    [Theory]
    [InlineData("other-executable")]
    [InlineData("other-arguments")]
    [InlineData("unquoted")]
    public void UpgradePreservesUnrecognizedRegistration(string kind)
    {
        var command = kind switch
        {
            "other-executable" => $"\"{Path.Combine(_directory, "old release", "Mountloom.App.exe")}\"",
            "other-arguments" => $"\"{_executable}\" --custom",
            _ => _executable
        };
        WriteCommand(command);

        Assert.False(_service.IsEnabled());
        _service.UpgradeLegacyRegistration();

        Assert.Equal(command, ReadCommand());
    }

    [Fact]
    public void CommandsAreComparedWithoutCaseSensitivity()
    {
        WriteCommand($"\"{_executable.ToUpperInvariant()}\"");
        Assert.True(_service.IsEnabled());

        _service.UpgradeLegacyRegistration();

        Assert.Equal($"\"{_executable}\" --startup", ReadCommand());
        WriteCommand($"\"{_executable.ToUpperInvariant()}\" --STARTUP");
        Assert.True(_service.IsEnabled());
    }

    [Theory]
    [InlineData("--startup", true)]
    [InlineData("--STARTUP", true)]
    [InlineData("--Startup", true)]
    [InlineData("startup", false)]
    [InlineData("--startup=true", false)]
    [InlineData("--startup-extra", false)]
    [InlineData(" --startup", false)]
    [InlineData("--startup ", false)]
    public void StartupFlagRequiresAnExactArgument(string argument, bool expected) =>
        Assert.Equal(expected, StartupRegistrationService.IsStartupLaunch([argument]));

    [Fact]
    public void StartupFlagCanAppearAmongArgumentsButIsNotImpliedByManualLaunch()
    {
        Assert.False(StartupRegistrationService.IsStartupLaunch([]));
        Assert.True(StartupRegistrationService.IsStartupLaunch(["--other", "--startup"]));
    }

    private void WriteCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey);
        key.SetValue("Mountly", command);
    }

    private string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue("Mountly") as string;
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_runKey, throwOnMissingSubKey: false);
        Directory.Delete(_directory, recursive: true);
    }
}

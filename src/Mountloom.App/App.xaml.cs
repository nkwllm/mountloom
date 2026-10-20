using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Security.Principal;
using Mountloom.Core.Services;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startInTray = StartupRegistrationService.IsStartupLaunch(e.Args);
        if (IsSetupRunning())
        {
            if (!startInTray)
                MessageBox.Show("Mountloom 正在安装或更新，请等待安装完成后再启动。", "Mountloom",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        // Share the legacy lock: old and new versions must not own the same configuration/cache concurrently.
        _instanceMutex = new Mutex(false, "Global\\Mountly-" + user);
        try { _ownsMutex = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            if (!startInTray)
                MessageBox.Show("Mountloom 已在运行。请双击任务栏通知区域中的 Mountloom 图标打开窗口。", "Mountloom",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try { new StartupRegistrationService().UpgradeLegacyRegistration(); }
        catch (Exception ex) { TryWriteCrashLog(ex); }
        try { new WindowsMountDriveIconService().RecoverStaleIcons(); }
        catch (Exception ex) { TryWriteCrashLog(ex); }

        var window = new MainWindow();
        MainWindow = window;
        // Never show the window during login startup, including before config is loaded.
        if (!startInTray) window.Show();
        await window.InitializeAsync();
    }

    internal static bool IsSetupRunning(string mutexName = @"Global\Mountly-Setup")
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var setupMutex)) return false;
            using (setupMutex) return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception);
        MessageBox.Show(
            $"Mountloom 遇到未处理的错误。请查看应用诊断日志。\n\n{e.Exception.Message}",
            "Mountloom 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TryWriteCrashLog(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception);
        e.SetObserved();
    }

    private static void TryWriteCrashLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mountly", "Logs");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

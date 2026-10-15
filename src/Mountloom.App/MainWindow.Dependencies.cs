using System.Diagnostics;
using System.Windows;

namespace Mountloom.App;

public partial class MainWindow
{
    private CancellationTokenSource? _installationCancellation;
    private Task<bool>? _installationTask;
    private bool _installingWinFsp;
    private CancellationTokenSource? _dependencyCancellation;
    private Task<bool>? _dependencyPreparationTask;
    private bool _rcloneReady;
    private bool _winFspReady;
    private bool _startupMountsPending;
    private bool _startupInitialized;
    private bool DependenciesBusy => _dependencyPreparationTask is { IsCompleted: false } || _installingWinFsp;
    private bool CanMount => _rcloneReady && _winFspReady && !DependenciesBusy;

    internal async Task InitializeDependenciesAsync()
    {
        if (!_configReady || _shuttingDown) return;
        if (!_startupInitialized)
        {
            _startupInitialized = true;
            _startupMountsPending = true;
        }
        await PrepareDependenciesAsync();
        await RunStartupMountsAsync();
    }

    internal Task<bool> PrepareDependenciesAsync(bool recheck = false)
    {
        if (!_configReady || _shuttingDown) return Task.FromResult(false);
        if (_dependencyPreparationTask is { IsCompleted: false }) return _dependencyPreparationTask;
        if (_installingWinFsp) return Task.FromResult(false);
        if (_rcloneReady && !recheck) return Task.FromResult(true);
        _dependencyCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _dependencyPreparationTask = PrepareDependenciesCoreAsync(_dependencyCancellation);
        UpdateControls();
        return _dependencyPreparationTask;
    }

    private async Task<bool> PrepareDependenciesCoreAsync(CancellationTokenSource cancellation)
    {
        // Assign the shared task before invoking services that may complete synchronously.
        await Task.Yield();
        var token = cancellation.Token;
        _rcloneReady = false;
        _winFspReady = false;
        EnvironmentText.Text = "正在自动准备挂载组件…";
        DependencySummaryText.Text = "正在自动准备组件，准备完成后即可测试与挂载。";
        UpdateControls();
        try
        {
            var path = await _ensureRclone(token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("组件准备未返回可用程序。");
            var status = await _detectEnvironment(path, token);
            token.ThrowIfCancellationRequested();
            if (!status.RcloneFound) throw new InvalidOperationException("自动准备的组件未通过运行检查，请重新检查 / 修复。");
            _config.App.RclonePath = path;
            ScheduleAutoSave();
            var saved = await SaveNowAsync();
            token.ThrowIfCancellationRequested();
            _rcloneReady = true;
            _winFspReady = status.WinFspFound;
            EnvironmentText.Text = $"rclone：{status.RcloneVersion}\nWinFsp：{(status.WinFspFound ? status.WinFspVersion : "尚未安装")}";
            DependencySummaryText.Text = status.WinFspFound
                ? "挂载组件已就绪。" : "连接测试已可用。挂载还需要 WinFsp，请在应用设置中安装。";
            if (!saved) EnvironmentText.Text += "\n组件已可用，但配置尚未保存，请检查输入或保存提示。";
            AppendLog("自动准备组件完成。" + (status.WinFspFound ? string.Empty : " WinFsp 尚未安装。"));
            return true;
        }
        catch (OperationCanceledException)
        {
            _rcloneReady = _winFspReady = false;
            EnvironmentText.Text = "组件准备已取消，可重新检查 / 修复。";
            DependencySummaryText.Text = "组件准备已取消。";
            return false;
        }
        catch (Exception ex)
        {
            _rcloneReady = _winFspReady = false;
            EnvironmentText.Text = $"组件自动准备失败：{ex.Message}\n请检查网络后选择“重新检查 / 修复”。";
            DependencySummaryText.Text = "组件准备失败，请展开应用设置并重新检查 / 修复。";
            ApplicationSettingsPanel.IsExpanded = true;
            AppendLog($"组件自动准备失败：{ex.Message}");
            return false;
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_dependencyCancellation, cancellation)) _dependencyCancellation = null;
            // The task is still completing inside finally; defer enablement until it is completed.
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(UpdateControls));
        }
    }

    private void OnDependencyProgress(string progress)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shuttingDown || _dependencyPreparationTask is not { IsCompleted: false }) return;
            EnvironmentText.Text = progress;
        }));
    }

    internal async Task RunStartupMountsAsync()
    {
        if (!_startupMountsPending || !CanMount || _shuttingDown || _connectionEditor is not null) return;
        _startupMountsPending = false;
        foreach (var row in _rows.Where(row => row.Connection.AutoMount).ToArray())
        {
            if (_shuttingDown) break;
            await MountRowAsync(row, showError: false);
        }
    }

    private async void DetectEnvironment_Click(object sender, RoutedEventArgs e)
    {
        await PrepareDependenciesAsync(recheck: true);
        await RunStartupMountsAsync();
    }

    private void OpenWinFspProject_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/winfsp/winfsp") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开 WinFsp 项目页面", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void InstallWinFsp_Click(object sender, RoutedEventArgs e)
    {
        if (!_configReady || _shuttingDown || DependenciesBusy) return;
        if (MessageBox.Show(this, "挂载需要 WinFsp 文件系统驱动。Mountloom 将自动安装，系统可能要求管理员授权。是否继续？", "安装 WinFsp",
            MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        await InstallWinFspAsync();
    }

    internal Task<bool> InstallWinFspAsync()
    {
        if (!_configReady || _shuttingDown) return Task.FromResult(false);
        if (_installationTask is { IsCompleted: false }) return _installationTask;
        if (DependenciesBusy) return Task.FromResult(false);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _installationCancellation = cancellation;
        _installingWinFsp = true;
        _installationTask = InstallWinFspCoreAsync(cancellation);
        UpdateControls();
        return _installationTask;
    }

    private async Task<bool> InstallWinFspCoreAsync(CancellationTokenSource cancellation)
    {
        // Track the entire installation, recheck and deferred startup sequence for shutdown.
        await Task.Yield();
        EnvironmentText.Text = "正在自动安装 WinFsp…";
        try
        {
            await _installWinFsp(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _installingWinFsp = false;
            if (_shuttingDown || !await PrepareDependenciesAsync(recheck: true)) return false;
            cancellation.Token.ThrowIfCancellationRequested();
            await RunStartupMountsAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            return _winFspReady;
        }
        catch (OperationCanceledException)
        {
            EnvironmentText.Text = "WinFsp 安装已取消，可重新安装。";
            AppendLog("WinFsp 安装已取消。");
            return false;
        }
        catch (Exception ex)
        {
            EnvironmentText.Text = $"WinFsp 安装失败：{ex.Message}";
            AppendLog(EnvironmentText.Text);
            return false;
        }
        finally
        {
            _installingWinFsp = false;
            cancellation.Dispose();
            if (ReferenceEquals(_installationCancellation, cancellation)) _installationCancellation = null;
            UpdateControls();
        }
    }

    internal async Task CancelDependencyWorkAsync()
    {
        _dependencyCancellation?.Cancel();
        _installationCancellation?.Cancel();
        var preparation = _dependencyPreparationTask;
        var installation = _installationTask;
        if (preparation is not null) await preparation;
        if (installation is not null)
        {
            try { await installation; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendLog($"依赖安装已停止：{ex.Message}"); }
        }
    }
}

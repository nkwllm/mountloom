using System.ComponentModel;
using System.Windows;
using Mountloom.Core.Services.Runtime;
using Forms = System.Windows.Forms;

namespace Mountloom.App;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayImage;
    private readonly CancellationTokenSource _windowLifetime = new();
    private bool _allowClose;
    private bool _trayHintShown;

    private void InitializeTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose();
            menu.Items.Clear();
            menu.Items.Add("打开 Mountloom", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
            foreach (var row in _rows)
            {
                var entry = new Forms.ToolStripMenuItem($"{row.Name} · {row.StateText}");
                var mount = new Forms.ToolStripMenuItem("挂载") { Enabled = _configReady && CanMount && !_shuttingDown && !IsBusy(row) && !IsRunning(row) };
                mount.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await MountRowAsync(row)));
                var unmount = new Forms.ToolStripMenuItem("卸载") { Enabled = !_shuttingDown && !IsBusy(row) && IsRunning(row) };
                unmount.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await UnmountRowAsync(row)));
                entry.DropDownItems.Add(mount);
                entry.DropDownItems.Add(unmount);
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = menu.Items.Add("静默退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitFromTrayAsync())));
            exit.Enabled = !_shuttingDown;
            exit.ToolTipText = "正常卸载后退出，不弹出窗口；需要处理时保留运行并通过托盘提示。";
        };
        using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Mountloom.App;component/Assets/Mountloom.ico"))!.Stream)
        using (var icon = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize))
            _trayImage = (System.Drawing.Icon)icon.Clone();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Mountloom", Icon = _trayImage,
            ContextMenuStrip = menu, Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowFromTray();
            _connectionEditor?.Activate();
        }));
    }

    private void ShowFromTray()
    {
        if (_allowClose || Dispatcher.HasShutdownStarted) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown || _trayIcon is null) return;
        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(2500, "Mountloom 仍在运行", "挂载会继续保持。右键托盘图标可卸载或退出。", Forms.ToolTipIcon.Info);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _configReady && MinimizeToTrayBox.IsChecked == true && _trayIcon is not null)
            HideToTray();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_shuttingDown) return;
        if (_configReady && MinimizeToTrayBox.IsChecked == true && _trayIcon is not null) HideToTray();
        else await ExitApplicationAsync();
    }

    private async void Exit_Click(object sender, RoutedEventArgs e) => await ExitApplicationAsync();

    internal Task ExitFromTrayAsync(Func<Task>? stopMounts = null, Action<string>? notify = null) =>
        ExitApplicationAsync(silent: true, stopMounts, notify);

    private async Task ExitApplicationAsync(bool silent = false, Func<Task>? stopMounts = null, Action<string>? notify = null)
    {
        if (_shuttingDown) return;
        if (_connectionEditor is not null)
        {
            if (silent) ReportSilentExitCancelled("连接设置仍在编辑，请先保存或关闭编辑窗口。", notify);
            else _connectionEditor.Activate();
            return;
        }
        if (!silent) ShowFromTray();
        _shuttingDown = true;
        _autoSaveTimer.Stop();
        UpdateControls();
        try
        {
            await CancelPendingWorkAsync();
            if (!await SaveBeforeExitAsync(() => !silent && MessageBox.Show(this,
                    "配置未能保存。是否放弃未保存的更改并继续退出？\n\n选择“否”返回编辑。继续退出仍会先正常卸载磁盘，不会强制中断上传。",
                    "未保存的更改", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes))
            {
                if (silent) ReportSilentExitCancelled("配置未能保存，未保存的更改已保留，请先处理保存问题。", notify);
                return;
            }
            if (silent) await (stopMounts ?? (() => _mountManager.StopAllAsync()))();
            else if (!await StopAllWithConfirmationAsync()) return;
            _windowLifetime.Cancel();
            if (_logWriter is not null) await _logWriter.DisposeAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            if (silent)
            {
                var confirmations = GetSafetyConfirmations(ex);
                var reason = confirmations.Length > 0
                    ? $"{string.Join("、", confirmations.Select(error => error.DriveLetter).Distinct())} 需要确认文件已关闭，请在主窗口完成卸载后重试。"
                    : $"未能完成正常退出：{ex.Message}";
                ReportSilentExitCancelled(reason, notify);
            }
            else
            {
                AppendLog($"退出已取消：{ex.Message}");
                MessageBox.Show(this, ex.Message, "退出已取消", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!_allowClose)
            {
                _mountManager.ResumeOperations();
                _shuttingDown = false;
                UpdateControls();
                if (_configReady && _savedVersion != _editVersion) _autoSaveTimer.Start();
            }
        }
    }

    private void ReportSilentExitCancelled(string reason, Action<string>? notify)
    {
        AppendLog($"静默退出已取消：{reason}");
        if (notify is not null) notify(reason);
        else _trayIcon?.ShowBalloonTip(5000, "Mountloom 仍在运行", $"{reason}\n点击此通知打开窗口。", Forms.ToolTipIcon.Warning);
    }

    internal async Task CancelPendingWorkAsync()
    {
        // A dependency installation can be awaiting its deferred startup mounts.
        // Cancel those operations before waiting for the installation to complete.
        foreach (var operation in _operations.Values.ToArray()) operation.Cancel();
        await CancelDependencyWorkAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (_operations.Count > 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        if (_operations.Count > 0) throw new InvalidOperationException("操作仍在停止中，请稍后再次退出。");
        // Initial config loading can recover or migrate files; let that work finish before closing.
        if (_initializationTask is not null) await _initializationTask;
    }

    internal async Task<bool> SaveBeforeExitAsync(Func<bool> confirmDiscard)
    {
        if (!_configReady || await SaveNowAsync()) return true;
        return confirmDiscard();
    }

    private async Task<bool> StopAllWithConfirmationAsync()
    {
        var acknowledged = false;
        while (true)
        {
            try { await _mountManager.StopAllAsync(acknowledgeOpenFilesClosed: acknowledged); return true; }
            catch (Exception ex) when (!acknowledged && GetSafetyConfirmations(ex).Length > 0)
            {
                var drives = string.Join("、", GetSafetyConfirmations(ex).Select(error => error.DriveLetter).Distinct());
                var confirmed = MessageBox.Show(this,
                    $"{drives} 的上传队列已清空，但系统无法确认是否仍有打开的文件。\n\n请关闭这些盘符上打开的文件和程序，再继续正常卸载并退出。是否继续？",
                    "确认文件已关闭", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (confirmed != MessageBoxResult.Yes) return false;
                acknowledged = true;
            }
            catch (Exception ex)
            {
                var force = MessageBox.Show(this,
                    $"正常卸载未完成：{ex.Message}\n\n强制退出会中断访问，尚未上传的数据可能留在缓存中。是否强制卸载并退出？",
                    "退出前需要处理挂载", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (force != MessageBoxResult.Yes) return false;
                await _mountManager.StopAllAsync(force: true);
                return true;
            }
        }
    }

    private static UnmountSafetyConfirmationRequiredException[] GetSafetyConfirmations(Exception exception) =>
        (exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.AsEnumerable() : [exception])
        .OfType<UnmountSafetyConfirmationRequiredException>().ToArray();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _shuttingDown = true;
        _autoSaveTimer.Stop();
        _logTimer.Stop();
        _windowLifetime.Cancel();
        _rcloneRuntime.ProgressChanged -= OnDependencyProgress;
        _trayIcon?.ContextMenuStrip?.Dispose();
        _trayIcon?.Dispose();
        _trayImage?.Dispose();
        _windowLifetime.Dispose();
    }
}

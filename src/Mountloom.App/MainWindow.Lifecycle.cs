using System.ComponentModel;
using System.Windows;
using Mountloom.Core.Services.Runtime;
using Forms = System.Windows.Forms;

namespace Mountloom.App;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
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
            menu.Items.Add("退出并卸载", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitApplicationAsync())));
        };
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Mountloom", Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu, Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    private void ShowFromTray()
    {
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

    private async Task ExitApplicationAsync()
    {
        if (_connectionEditor is not null) { _connectionEditor.Activate(); return; }
        if (_shuttingDown) return;
        ShowFromTray();
        _shuttingDown = true;
        _autoSaveTimer.Stop();
        UpdateControls();
        try
        {
            await CancelPendingWorkAsync();
            if (!await SaveBeforeExitAsync(() => MessageBox.Show(this,
                    "配置未能保存。是否放弃未保存的更改并继续退出？\n\n选择“否”返回编辑。继续退出仍会先正常卸载磁盘，不会强制中断上传。",
                    "未保存的更改", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes))
                return;
            if (!await StopAllWithConfirmationAsync()) return;
            _windowLifetime.Cancel();
            if (_logWriter is not null) await _logWriter.DisposeAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppendLog($"退出已取消：{ex.Message}");
            MessageBox.Show(this, ex.Message, "退出已取消", MessageBoxButton.OK, MessageBoxImage.Error);
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

    internal async Task CancelPendingWorkAsync()
    {
        // A dependency installation can be awaiting its deferred startup mounts.
        // Cancel those operations before waiting for the installation to complete.
        foreach (var operation in _operations.Values.ToArray()) operation.Cancel();
        await CancelDependencyWorkAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (_operations.Count > 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        if (_operations.Count > 0) throw new InvalidOperationException("操作仍在停止中，请稍后再次退出。");
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
        _windowLifetime.Dispose();
    }
}

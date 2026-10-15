using System.Diagnostics;
using System.IO;
using System.Windows;
using Mountloom.Core.Services;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.App;

public partial class MainWindow
{
    private async Task<bool> RunOperationAsync(ConnectionRow row, string label,
        Func<CancellationToken, Task> action, bool showError = true, bool requireSavedConfig = true)
    {
        if (_shuttingDown || !_configReady || IsBusy(row) || _connectionEditor is not null) return false;
        using var cancellation = new CancellationTokenSource();
        _operations.Add(row.Connection.Id, cancellation);
        row.OperationError = string.Empty;
        UpdateControls();
        try
        {
            if (requireSavedConfig && !await SaveNowAsync()) throw new InvalidOperationException("配置尚未保存，请先修正输入或保存错误。");
            cancellation.Token.ThrowIfCancellationRequested();
            AppendLog($"{label}：{row.Name}", row.Connection.Id);
            await action(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"已取消{label}：{row.Name}", row.Connection.Id);
            return false;
        }
        catch (Exception ex)
        {
            row.OperationError = ex.Message;
            AppendLog($"{label}失败：{ex.Message}", row.Connection.Id);
            if (showError && !_shuttingDown)
                MessageBox.Show(this, ex.Message, $"{label}失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _operations.Remove(row.Connection.Id);
            row.RefreshStatus(_mountManager.GetStatus(row.Connection.Id));
            UpdateControls();
        }
    }

    private async Task<bool> MountRowAsync(ConnectionRow row, bool showError = true)
    {
        if (IsRunning(row) || _shuttingDown || _connectionEditor is not null) return false;
        if (!await PrepareDependenciesAsync() || !CanMount)
        {
            row.OperationError = _rcloneReady ? "挂载需要 WinFsp，请在应用设置中完成安装后重试。" : "组件尚未准备就绪，请使用重新检查 / 修复。";
            row.RefreshConnection();
            UpdateControls();
            return false;
        }
        return await RunOperationAsync(row, "挂载", async token =>
        {
            await _mount(row.Connection.Clone(), _config.App.Clone(), token);
            row.NeedsReconnect = false;
        }, showError);
    }

    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is { } row) await MountRowAsync(row);
    }

    private Task<bool> UnmountRowAsync(ConnectionRow row) => RunOperationAsync(row, "卸载",
        token => UnmountWithConfirmationAsync(row, token), requireSavedConfig: false);

    private async Task<bool> UnmountWithConfirmationAsync(ConnectionRow row, CancellationToken token, bool acknowledged = false)
    {
        try { await _mountManager.UnmountAsync(row.Connection.Id, cancellationToken: token, acknowledgeOpenFilesClosed: acknowledged); }
        catch (UnmountSafetyConfirmationRequiredException ex) when (!acknowledged && !_shuttingDown)
        {
            var confirmed = MessageBox.Show(this,
                $"{ex.DriveLetter} 的上传队列已清空，但系统无法确认是否仍有打开的文件。\n\n请关闭此盘符上打开的文件和程序，再继续正常卸载。是否已关闭并继续？",
                "确认文件已关闭", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (confirmed != MessageBoxResult.Yes) throw new OperationCanceledException("用户保留当前挂载。");
            return await UnmountWithConfirmationAsync(row, token, acknowledged: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !_shuttingDown)
        {
            var force = MessageBox.Show(this,
                $"正常卸载未完成：{ex.Message}\n\n强制卸载会中断访问，尚未上传的数据可能仍在本地缓存中。是否强制卸载？",
                "无法完成正常卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (force != MessageBoxResult.Yes) throw new OperationCanceledException("用户保留当前挂载。");
            await _mountManager.UnmountAsync(row.Connection.Id, force: true, cancellationToken: token);
            return true;
        }
        return false;
    }

    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is { } row) await UnmountRowAsync(row);
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is not { } row) return;
        if (!await PrepareDependenciesAsync() || !CanMount) return;
        await RunOperationAsync(row, "重新挂载", async token =>
        {
            var connection = row.Connection.Clone();
            ConnectionValidator.Validate(connection);
            if (await UnmountWithConfirmationAsync(row, token))
            {
                AppendLog("已强制卸载；为保留未完成上传的排查机会，请确认缓存状态后手动挂载。", row.Connection.Id);
                return;
            }
            token.ThrowIfCancellationRequested();
            await _mount(connection, _config.App.Clone(), token);
            row.NeedsReconnect = false;
        });
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is not { } row) return;
        if (!await PrepareDependenciesAsync()) return;
        await RunOperationAsync(row, "测试连接", async token =>
        {
            var result = await _connectionTester.TestAsync(row.Connection.Clone(), _config.App.Clone(), token);
            AppendLog($"连接测试成功：{result}", row.Connection.Id);
            if (!_shuttingDown) MessageBox.Show(this, "连接测试成功。\n" + result, row.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is { } row && _operations.TryGetValue(row.Connection.Id, out var cancellation))
            cancellation.Cancel();
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is not { } row) return;
        var drive = _mountManager.GetStatus(row.Connection.Id).MountedDriveLetter;
        if (string.IsNullOrWhiteSpace(drive)) return;
        try { Process.Start(new ProcessStartInfo(drive.TrimEnd('\\') + "\\") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开盘符", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

}

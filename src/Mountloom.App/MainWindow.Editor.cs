using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Mountloom.Core.Models;
using Mountloom.Core.Services;

namespace Mountloom.App;

public partial class MainWindow
{
    private ConnectionEditorWindow? _connectionEditor;

    private bool TryCommitEditor(out string error)
    {
        error = string.Empty;
        if (_isLoadingEditor || !_configReady) return true;
        _config.App.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        return true;
    }

    private static string MountFingerprint(MountConnection connection)
    {
        var copy = connection.Clone();
        copy.AutoMount = false;
        return JsonSerializer.Serialize(copy);
    }

    internal ConnectionEditorWindow CreateConnectionEditor(ConnectionRow? row)
    {
        if (!_configReady || _shuttingDown) throw new InvalidOperationException("配置尚未就绪，请稍后再试。");
        if (row is not null && (!_rows.Contains(row) || IsBusy(row)))
            throw new InvalidOperationException("连接正在执行操作，请完成后再修改设置。");
        var used = _rows.Where(item => item != row)
            .Select(item => DriveLetterService.Normalize(item.Connection.DriveLetter))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connection = row?.Connection.Clone();
        if (connection is null)
        {
            var drive = _driveLetterService.GetAvailableDriveLetters(_mountManager.GetReservedDriveLetters())
                .FirstOrDefault(letter => !used.Contains(letter));
            if (drive is null) throw new InvalidOperationException("没有可用盘符，请先释放一个盘符。");
            connection = new MountConnection { Name = $"新连接 {_rows.Count + 1}", DriveLetter = drive };
        }
        IReadOnlyList<string> DriveLetters(string selected)
        {
            var letters = _driveLetterService.GetAvailableDriveLetters(_mountManager.GetReservedDriveLetters())
                .Where(letter => !used.Contains(letter)).ToList();
            if (!string.IsNullOrWhiteSpace(selected) && !letters.Contains(selected)) letters.Add(selected);
            return letters.OrderBy(letter => letter).ToArray();
        }
        return new ConnectionEditorWindow(connection, row is null, _config.App.CacheRoot, DriveLetters,
            draft => SaveConnectionAsync(row, draft));
    }

    private async Task<string?> SaveConnectionAsync(ConnectionRow? row, MountConnection draft)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (!_configReady || _shuttingDown) return "配置当前无法保存，请稍后重试。";
            if (row is not null && (!_rows.Contains(row) || IsBusy(row))) return "连接正在执行操作，请稍后重试。";
            var candidate = draft.Clone();
            candidate.DriveLetter = DriveLetterService.Validate(candidate.DriveLetter);
            if (_rows.Any(item => item != row && string.Equals(item.Connection.DriveLetter,
                    candidate.DriveLetter, StringComparison.OrdinalIgnoreCase)))
                return "该盘符已分配给另一个连接，请选择其他盘符。";
            if ((row is null || !string.Equals(row.Connection.DriveLetter, candidate.DriveLetter, StringComparison.OrdinalIgnoreCase)) &&
                (!_driveLetterService.IsDriveLetterAvailable(candidate.DriveLetter) ||
                 _mountManager.GetReservedDriveLetters().Contains(candidate.DriveLetter, StringComparer.OrdinalIgnoreCase)))
                return "该盘符正在被使用，请选择其他盘符。";
            if (row is not null && candidate.Id != row.Connection.Id) return "连接标识不匹配，请重新打开设置。";
            TryCommitEditor(out _);
            var snapshot = JsonSerializer.Deserialize<MountloomConfig>(JsonSerializer.Serialize(_config))!;
            var index = row is null ? -1 : snapshot.Connections.FindIndex(item => item.Id == row.Connection.Id);
            if (row is not null && index < 0) return "连接已不存在，请重新打开设置。";
            if (index < 0) snapshot.Connections.Add(candidate.Clone());
            else snapshot.Connections[index] = candidate.Clone();
            var version = _editVersion;
            await _saveConfiguration(snapshot);
            // Publish the draft only after the complete configuration has reached disk.
            if (row is null)
            {
                _config.Connections.Add(candidate);
                row = new ConnectionRow(candidate, _mountManager.GetStatus(candidate.Id));
                _rows.Add(row);
                ConnectionsGrid.SelectedItem = row;
            }
            else
            {
                if (IsRunning(row) && MountFingerprint(row.Connection) != MountFingerprint(candidate)) row.NeedsReconnect = true;
                var currentIndex = _config.Connections.FindIndex(item => item.Id == candidate.Id);
                _config.Connections[currentIndex] = candidate;
                row.ApplyConnection(candidate);
            }
            _editVersion++;
            if (_editVersion == version + 1) _savedVersion = _editVersion;
            else { _savedVersion = version; _autoSaveTimer.Start(); }
            SaveStatusText.Text = $"已保存 {DateTimeOffset.Now:HH:mm:ss}";
            RefreshLogFilters();
            UpdateControls();
            return null;
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "连接未保存，请在设置窗口中重试";
            return "保存失败：" + LogRedactor.Redact(ex.Message, draft);
        }
        finally { _saveGate.Release(); }
    }

    private void ShowConnectionEditor(ConnectionRow? row)
    {
        if (_connectionEditor is not null) { _connectionEditor.Activate(); return; }
        try
        {
            var dialog = CreateConnectionEditor(row);
            dialog.Owner = this;
            _connectionEditor = dialog;
            dialog.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "连接设置", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally
        {
            _connectionEditor = null;
            UpdateControls();
            _ = RunStartupMountsAsync();
        }
    }

    private void EditSelectedConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is { } row) ShowConnectionEditor(row);
    }

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: ConnectionRow row })
        {
            ConnectionsGrid.SelectedItem = row;
            e.Handled = true;
            ShowConnectionEditor(row);
        }
    }

    private void ConnectionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ConnectionsGrid, source) is DataGridRow { Item: ConnectionRow row })
        {
            e.Handled = true;
            ShowConnectionEditor(row);
        }
    }

    private void ConnectionsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _editingRow is { } row)
        {
            e.Handled = true;
            ShowConnectionEditor(row);
        }
    }

    private void StartWithWindowsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_configReady || _isLoadingEditor) return;
        var enabled = StartWithWindowsBox.IsChecked == true;
        try
        {
            _startupRegistration.SetEnabled(enabled);
            _config.App.StartWithWindows = enabled;
            ScheduleAutoSave();
        }
        catch (Exception ex)
        {
            _isLoadingEditor = true;
            StartWithWindowsBox.IsChecked = _config.App.StartWithWindows;
            _isLoadingEditor = false;
            MessageBox.Show(this, ex.Message, "无法修改登录启动设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

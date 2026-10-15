using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mountloom.Core.Services.Runtime;
using Mountloom.Core.Services;

namespace Mountloom.App;

public partial class MainWindow
{
    private readonly ConcurrentQueue<MountLogEntry> _pendingLogs = new();
    private readonly List<MountLogEntry> _logs = [];
    private DispatcherTimer _logTimer = null!;
    private bool _refreshingFilters;
    private RollingLogWriter? _logWriter;
    private string? _reportedLogError;
    private sealed record LogFilter(string ConnectionId, string Name);

    private void InitializeLogs()
    {
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _logTimer.Tick += (_, _) => FlushLogs();
        _logTimer.Start();
        RefreshLogFilters();
    }

    private void AppendLog(string line, string connectionId = "") =>
        QueueLog(new MountLogEntry(connectionId, line, DateTimeOffset.Now));

    private void QueueLog(MountLogEntry entry)
    {
        if (entry.Message.Length > 8192) entry = entry with { Message = entry.Message[..8192] + "…" };
        _logWriter?.Write(entry);
        while (_pendingLogs.Count >= 2000) _pendingLogs.TryDequeue(out _);
        _pendingLogs.Enqueue(entry);
    }

    private void FlushLogs()
    {
        var changed = false;
        if (_logWriter?.LastError is { } error && error != _reportedLogError)
        {
            _reportedLogError = error;
            _pendingLogs.Enqueue(new MountLogEntry(string.Empty, error, DateTimeOffset.Now));
        }
        for (var index = 0; index < 300 && _pendingLogs.TryDequeue(out var entry); index++)
        {
            _logs.Add(entry);
            changed = true;
        }
        var max = Math.Clamp(_config.App.MaxLogLines, 100, 10000);
        if (_logs.Count > max) _logs.RemoveRange(0, _logs.Count - max);
        if (changed) RenderLogs();
    }

    private void RefreshLogFilters()
    {
        if (LogFilterBox is null) return;
        var selectedId = (LogFilterBox.SelectedItem as LogFilter)?.ConnectionId ?? "*";
        var filters = new List<LogFilter> { new("*", "全部日志"), new("", "应用日志") };
        filters.AddRange(_rows.Select(row => new LogFilter(row.Connection.Id, row.Name)));
        _refreshingFilters = true;
        LogFilterBox.ItemsSource = filters;
        LogFilterBox.SelectedItem = filters.FirstOrDefault(filter => filter.ConnectionId == selectedId) ?? filters[0];
        _refreshingFilters = false;
    }

    private IEnumerable<string> GetVisibleLogLines()
    {
        var filter = (LogFilterBox.SelectedItem as LogFilter)?.ConnectionId ?? "*";
        foreach (var entry in _logs.Where(entry => filter == "*" || entry.ConnectionId == filter))
        {
            var name = _rows.FirstOrDefault(row => row.Connection.Id == entry.ConnectionId)?.Name ??
                (entry.ConnectionId.Length == 0 ? "应用" : entry.ConnectionId);
            var message = entry.Message.Length > 8192 ? entry.Message[..8192] + "…" : entry.Message;
            yield return $"[{entry.Timestamp:HH:mm:ss}] [{name}] {message}";
        }
    }

    private void RenderLogs()
    {
        var follow = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        var offset = LogBox.VerticalOffset;
        LogBox.Text = string.Join(Environment.NewLine, GetVisibleLogLines());
        if (follow) LogBox.ScrollToEnd(); else LogBox.ScrollToVerticalOffset(offset);
    }

    private void LogFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingFilters && LogBox is not null) RenderLogs();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        while (_pendingLogs.TryDequeue(out _)) { }
        _logs.Clear();
        RenderLogs();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        FlushLogs();
        try { if (LogBox.Text.Length > 0) Clipboard.SetText(LogBox.Text); }
        catch (Exception ex) { AppendLog($"复制日志失败：{ex.Message}"); }
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出当前筛选的日志", Filter = "日志文本 (*.log)|*.log", FileName = $"mountloom-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;
        FlushLogs();
        try { await File.WriteAllLinesAsync(dialog.FileName, GetVisibleLogLines().ToArray()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "日志导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

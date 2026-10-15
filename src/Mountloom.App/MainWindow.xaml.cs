using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mountloom.Core.Configuration;
using Mountloom.Core.Models;
using Mountloom.Core.Services;

namespace Mountloom.App;

public partial class MainWindow : Window
{
    private readonly ConfigStore _configStore;
    private readonly Func<MountloomConfig, Task> _saveConfiguration;
    private readonly bool _enablePersistentLogs;
    private readonly EnvironmentDetector _environmentDetector = new();
    private readonly RcloneRuntimeService _rcloneRuntime = new();
    private readonly Func<CancellationToken, Task<string>> _ensureRclone;
    private readonly Func<string, CancellationToken, Task<EnvironmentStatus>> _detectEnvironment;
    private readonly Func<CancellationToken, Task> _installWinFsp;
    private readonly Func<MountConnection, AppSettings, CancellationToken, Task> _mount;
    private readonly DependencyInstaller _dependencyInstaller = new();
    private readonly DriveLetterService _driveLetterService = new();
    private readonly MountManager _mountManager = new();
    private readonly ConnectionTester _connectionTester = new();
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly ObservableCollection<ConnectionRow> _rows = [];
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _operations = new();
    private MountloomConfig _config = new();
    private ConnectionRow? _editingRow;
    private bool _isLoadingEditor;
    private bool _changingSelection;
    private bool _configReady;
    private bool _shuttingDown;
    private long _editVersion;
    private long _savedVersion;

    public MainWindow() : this(new ConfigStore(), true) { }

    internal MainWindow(ConfigStore configStore, bool enableTray, Func<MountloomConfig, Task>? saveConfiguration = null,
        bool enablePersistentLogs = true, Func<CancellationToken, Task<string>>? ensureRclone = null,
        Func<string, CancellationToken, Task<EnvironmentStatus>>? detectEnvironment = null,
        Func<CancellationToken, Task>? installWinFsp = null,
        Func<MountConnection, AppSettings, CancellationToken, Task>? mount = null)
    {
        _configStore = configStore;
        _saveConfiguration = saveConfiguration ?? (config => configStore.SaveAsync(config));
        _enablePersistentLogs = enablePersistentLogs;
        _ensureRclone = ensureRclone ?? (token => _rcloneRuntime.EnsureAsync(token));
        _detectEnvironment = detectEnvironment ?? ((path, token) => _environmentDetector.DetectAsync(path, token));
        _installWinFsp = installWinFsp ?? (token => _dependencyInstaller.InstallWinFspAsync(token));
        _mount = mount ?? ((connection, settings, token) => _mountManager.MountAsync(connection, settings, token));
        InitializeComponent();
        ConnectionsGrid.ItemsSource = _rows;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _autoSaveTimer.Tick += async (_, _) => { _autoSaveTimer.Stop(); await SaveNowAsync(); };
        MinimizeToTrayBox.Checked += (_, _) => ScheduleAutoSave();
        MinimizeToTrayBox.Unchecked += (_, _) => ScheduleAutoSave();
        InitializeLogs();
        if (enableTray) InitializeTray();
        _mountManager.StatusChanged += MountManager_StatusChanged;
        _mountManager.LogEntryReceived += (_, entry) => QueueLog(entry);
        _dependencyInstaller.OutputReceived += (_, line) => AppendLog(line);
        _rcloneRuntime.ProgressChanged += OnDependencyProgress;
        UpdateControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { await LoadConfigAsync(); }
        catch (Exception ex)
        {
            _configReady = false;
            SaveStatusText.Text = "配置加载失败，已停止保存";
            AppendLog($"配置加载失败：{ex.Message}");
            MessageBox.Show(this, $"无法安全加载配置，已停止保存。请检查或恢复配置后重新启动。\n\n{ex.Message}",
                "配置加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateControls();
            return;
        }
        if (_shuttingDown) return;
        await InitializeDependenciesAsync();
    }

    internal async Task LoadConfigAsync()
    {
        _configReady = false;
        _isLoadingEditor = true;
        try
        {
            _config = await _configStore.LoadAsync();
            if (_enablePersistentLogs)
            {
                if (_logWriter is not null) await _logWriter.DisposeAsync();
                _logWriter = new RollingLogWriter(_config.App.LogRoot);
            }
            MinimizeToTrayBox.IsChecked = _config.App.MinimizeToTray;
            try { StartWithWindowsBox.IsChecked = _startupRegistration.IsEnabled(); }
            catch (Exception ex) { StartWithWindowsBox.IsChecked = false; AppendLog($"登录启动状态不可用：{ex.Message}"); }
            _config.App.StartWithWindows = StartWithWindowsBox.IsChecked == true;
            _editingRow = null;
            _rows.Clear();
            foreach (var connection in _config.Connections)
                _rows.Add(new ConnectionRow(connection, _mountManager.GetStatus(connection.Id)));
            _changingSelection = true;
            ConnectionsGrid.SelectedIndex = _rows.Count == 0 ? -1 : 0;
            _changingSelection = false;
            _editingRow = ConnectionsGrid.SelectedItem as ConnectionRow;
            RefreshLogFilters();
            _configReady = true;
            _editVersion = _savedVersion = 0;
            SaveStatusText.Text = "配置已加载";
            if (!string.IsNullOrEmpty(_configStore.LastLoadWarning)) AppendLog(_configStore.LastLoadWarning);
        }
        finally
        {
            _isLoadingEditor = false;
            _changingSelection = false;
            UpdateControls();
        }
    }

    private void ScheduleAutoSave()
    {
        if (_isLoadingEditor || !_configReady || _shuttingDown) return;
        _editVersion++;
        SaveStatusText.Text = "有未保存更改…";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    internal async Task<bool> SaveNowAsync()
    {
        if (!_configReady) return false;
        _autoSaveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            do
            {
                if (!TryCommitEditor(out var error)) { SaveStatusText.Text = error; return false; }
                var version = _editVersion;
                var snapshot = JsonSerializer.Deserialize<MountloomConfig>(JsonSerializer.Serialize(_config))!;
                SaveStatusText.Text = "正在保存…";
                await _saveConfiguration(snapshot);
                _savedVersion = version;
            } while (_savedVersion != _editVersion);
            SaveStatusText.Text = $"已保存 {DateTimeOffset.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = "保存失败，请检查日志";
            AppendLog($"配置保存失败：{ex.Message}");
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private void ConnectionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || _isLoadingEditor) return;
        _editingRow = ConnectionsGrid.SelectedItem as ConnectionRow;
        UpdateControls();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        ShowConnectionEditor(null);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingRow is not { } row || IsRunning(row) || IsBusy(row)) return;
        if (MessageBox.Show(this, $"删除连接“{row.Name}”？", "删除连接", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _editingRow = null;
        _changingSelection = true;
        _config.Connections.Remove(row.Connection);
        _rows.Remove(row);
        ConnectionsGrid.SelectedIndex = _rows.Count == 0 ? -1 : 0;
        _changingSelection = false;
        _editingRow = ConnectionsGrid.SelectedItem as ConnectionRow;
        RefreshLogFilters();
        ScheduleAutoSave();
        await SaveNowAsync();
        UpdateControls();
    }

    private static bool IsRunning(ConnectionRow row) => row.ProcessId is not null ||
        row.State is MountState.Mounting or MountState.Mounted or MountState.Unmounting or MountState.Reconnecting;
    private bool IsBusy(ConnectionRow row) => _operations.ContainsKey(row.Connection.Id) ||
        row.State is MountState.Mounting or MountState.Unmounting or MountState.Reconnecting;

    private void UpdateControls()
    {
        if (MountButton is null) return;
        var row = _editingRow;
        var usable = _configReady && !_shuttingDown;
        var busy = row is not null && IsBusy(row);
        var running = row is not null && IsRunning(row);
        AddButton.IsEnabled = usable;
        DeleteButton.IsEnabled = usable && row is not null && !busy && !running;
        MountButton.IsEnabled = usable && CanMount && row is not null && !busy && !running;
        UnmountButton.IsEnabled = usable && row is not null && !busy && running;
        ReconnectButton.IsEnabled = usable && CanMount && row is not null && !busy && running;
        TestConnectionButton.IsEnabled = usable && _rcloneReady && !DependenciesBusy && row is not null && !busy;
        CancelOperationButton.IsEnabled = row is not null && _operations.ContainsKey(row.Connection.Id);
        OpenDriveButton.IsEnabled = row?.State == MountState.Mounted;
        EditConnectionButton.IsEnabled = usable && row is not null && !busy;
        ApplicationSettingsPanel.IsEnabled = usable;
        DetectButton.IsEnabled = usable && !DependenciesBusy && _operations.Count == 0;
        InstallWinFspButton.IsEnabled = usable && !DependenciesBusy && !_winFspReady;
        InstallWinFspButton.Visibility = _winFspReady ? Visibility.Collapsed : Visibility.Visible;
        ConnectionStatusText.Text = row is null ? "添加连接以开始使用。" : row.StatusDetail;
    }

    private void MountManager_StatusChanged(object? sender, MountRuntimeStatus status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rows.FirstOrDefault(item => item.Connection.Id == status.ConnectionId)?.RefreshStatus(status);
            UpdateControls();
        }));
    }
}

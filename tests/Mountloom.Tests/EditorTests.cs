using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mountloom.App;
using Mountloom.Core.Configuration;
using Mountloom.Core.Models;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.Tests;

public sealed class EditorTests
{
    [Fact]
    public Task SavingAConnectionDialogPreservesItsOwnerAfterSelectionChanges() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var expected = files.CreateConfig();
        await files.Store.SaveAsync(expected);
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            var alpha = window.CreateConnectionEditor((ConnectionRow)grid.SelectedItem);
            Assert.Equal("Alpha", Find<TextBox>(alpha, "NameBox").Text);
            Assert.Equal("alpha-secret", Find<PasswordBox>(alpha, "SecretBox").Password);
            Find<TextBox>(alpha, "NameBox").Text = "Alpha edited";
            Find<TextBox>(alpha, "HostBox").Text = "https://alpha.example/changed";
            grid.SelectedIndex = 1;
            Assert.True(await alpha.SaveAsync());
            alpha.Close();
            var beta = window.CreateConnectionEditor((ConnectionRow)grid.SelectedItem);
            Assert.Equal("Beta", Find<TextBox>(beta, "NameBox").Text);
            Assert.Equal("beta-secret", Find<PasswordBox>(beta, "SecretBox").Password);
            Find<TextBox>(beta, "NameBox").Text = "Beta edited";
            Assert.True(await beta.SaveAsync());
            beta.Close();
            grid.SelectedIndex = 0;
            var reopened = window.CreateConnectionEditor((ConnectionRow)grid.SelectedItem);
            Assert.Equal("Alpha edited", Find<TextBox>(reopened, "NameBox").Text);
            Assert.Equal("https://alpha.example/changed", Find<TextBox>(reopened, "HostBox").Text);
            await CaptureUiIfRequested(reopened, "connection-settings.png");
            Find<StackPanel>(reopened, "EditorPanel").Children.OfType<Expander>().Single().IsExpanded = true;
            await CaptureUiIfRequested(reopened, "connection-settings-advanced.png", scrollToEnd: true);
            reopened.Close();
            var saved = await files.Store.LoadAsync();
            Assert.Equal("Alpha edited", saved.Connections[0].Name);
            Assert.Equal("Beta edited", saved.Connections[1].Name);
            Assert.Equal("beta-secret", saved.Connections[1].Secret);
            Assert.Equal("Y:", saved.Connections[1].DriveLetter);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task EditsDuringAnInFlightSaveProduceANewSnapshot() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<MountloomConfig>();
        async Task Save(MountloomConfig snapshot)
        {
            snapshots.Add(snapshot);
            if (snapshots.Count == 1) { started.SetResult(); await release.Task; }
        }
        var window = new MainWindow(files.Store, enableTray: false, Save, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked = false;
            var saving = window.SaveNowAsync();
            await started.Task;
            Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked = true;
            Assert.False(snapshots[0].App.MinimizeToTray);
            release.SetResult();
            Assert.True(await saving);
            Assert.Equal(2, snapshots.Count);
            Assert.True(snapshots[1].App.MinimizeToTray);
            Assert.False(snapshots[0].App.MinimizeToTray);
        }
        finally { release.TrySetResult(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task EmptyConfigurationDoesNotInventOrOverwriteAConnection() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(new MountloomConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            Assert.Empty(Find<DataGrid>(window, "ConnectionsGrid").Items.Cast<object>());
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            Assert.True(await window.SaveNowAsync());
            Assert.Empty((await files.Store.LoadAsync()).Connections);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task CorruptConfigurationCannotBeOverwrittenByTheEditor() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await File.WriteAllTextAsync(files.Store.ConfigPath, "{broken");
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => window.LoadConfigAsync());
            Assert.False(await window.SaveNowAsync());
            Assert.True(await window.SaveBeforeExitAsync(() => throw new InvalidOperationException("Unloaded configuration must not be saved or prompt for draft disposal.")));
            Assert.Equal("{broken", await File.ReadAllTextAsync(files.Store.ConfigPath));
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task AddingAConnectionDoesNotCopyPreviousCredentials() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var dialog = window.CreateConnectionEditor(null);
            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            Assert.Equal(2, grid.Items.Count);
            Assert.Empty(Find<PasswordBox>(dialog, "SecretBox").Password);
            Assert.Empty(Find<TextBox>(dialog, "HostBox").Text);
            await CaptureUiIfRequested(dialog, "new-connection.png");
            Find<TextBox>(dialog, "HostBox").Text = "https://new.example/dav";
            Assert.True(await dialog.SaveAsync());
            dialog.Close();
            Assert.Equal(3, grid.Items.Count);
            var saved = await files.Store.LoadAsync();
            Assert.Equal("Alpha", saved.Connections[0].Name);
            Assert.Equal("alpha-secret", saved.Connections[0].Secret);
            Assert.Equal(3, saved.Connections.Select(connection => connection.DriveLetter).Distinct().Count());
        }
        finally { CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingAConnectionDraftDoesNotChangeSavedConnections(bool createNew) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            var selected = (ConnectionRow)grid.SelectedItem;
            var dialog = window.CreateConnectionEditor(createNew ? null : selected);
            Find<TextBox>(dialog, "NameBox").Text = "Cancelled draft";
            Find<TextBox>(dialog, "HostBox").Text = "https://cancelled.example/dav";
            Find<PasswordBox>(dialog, "SecretBox").Password = "cancelled-secret";
            // Background configuration writes must never include an open editor's draft.
            Assert.True(await window.SaveNowAsync());
            dialog.Close();
            Assert.True(await window.SaveNowAsync());
            var saved = await files.Store.LoadAsync();
            Assert.Equal(2, saved.Connections.Count);
            Assert.Equal("Alpha", saved.Connections[0].Name);
            Assert.Equal("https://alpha.example/dav", saved.Connections[0].Host);
            Assert.Equal("alpha-secret", saved.Connections[0].Secret);
            Assert.Equal(2, grid.Items.Count);
            Assert.Same(selected, grid.SelectedItem);
            Assert.Equal("Alpha", selected.Name);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailedDialogSaveKeepsTheDraftAndDoesNotApplyItUntilRetrySucceeds(bool createNew) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var fail = true;
        Task Save(MountloomConfig config) => fail
            ? Task.FromException(new IOException("Simulated full disk."))
            : files.Store.SaveAsync(config);
        var window = new MainWindow(files.Store, enableTray: false, Save, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            var selected = (ConnectionRow)grid.SelectedItem;
            var dialog = window.CreateConnectionEditor(createNew ? null : selected);
            Find<TextBox>(dialog, "NameBox").Text = "Pending save";
            Find<TextBox>(dialog, "HostBox").Text = "https://pending.example/dav";
            Find<PasswordBox>(dialog, "SecretBox").Password = "pending-secret";
            Assert.False(await dialog.SaveAsync());
            Assert.Equal(2, grid.Items.Count);
            Assert.Equal("Alpha", selected.Name);
            Assert.Equal("Alpha", (await files.Store.LoadAsync()).Connections[0].Name);
            Assert.Equal("Pending save", Find<TextBox>(dialog, "NameBox").Text);
            Assert.Equal("pending-secret", Find<PasswordBox>(dialog, "SecretBox").Password);
            fail = false;
            Assert.True(await dialog.SaveAsync());
            dialog.Close();
            var saved = await files.Store.LoadAsync();
            Assert.Equal(createNew ? 3 : 2, saved.Connections.Count);
            var connection = Assert.Single(saved.Connections, item => item.Name == "Pending save");
            Assert.Equal("pending-secret", connection.Secret);
            Assert.Equal(createNew ? 3 : 2, grid.Items.Count);
            if (createNew) Assert.Equal("alpha-secret", saved.Connections[0].Secret);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task DialogRejectsAnotherConnectionsDriveWithoutChangingEitherConnection() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            var selected = (ConnectionRow)grid.SelectedItem;
            var dialog = window.CreateConnectionEditor(selected);
            Find<ComboBox>(dialog, "DriveLetterBox").Text = "Y:";
            Assert.False(await dialog.SaveAsync());
            Assert.Contains("另一个连接", Find<TextBlock>(dialog, "EditorErrorText").Text);
            Assert.Equal("X:", selected.Connection.DriveLetter);
            Assert.Equal(new[] { "X:", "Y:" }, (await files.Store.LoadAsync()).Connections.Select(item => item.DriveLetter));
            dialog.Close();
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task CollapsingLogsReleasesSpaceAndKeepsMessagesReceivedWhileHidden() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var expander = Find<Expander>(window, "LogsExpander");
            Assert.False(expander.IsExpanded);
            var content = (FrameworkElement)window.Content;
            var size = new Size(1180, 820);
            void Layout()
            {
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
            }
            Layout();
            var collapsedHeight = expander.ActualHeight;
            await CaptureUiIfRequested(window, "main-logs-collapsed.png");
            typeof(MainWindow).GetMethod("AppendLog", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, ["Message received while collapsed", string.Empty]);
            typeof(MainWindow).GetMethod("FlushLogs", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            expander.IsExpanded = true;
            Layout();
            Assert.True(expander.ActualHeight > collapsedHeight + 100);
            Assert.Contains("Message received while collapsed", Find<TextBox>(window, "LogBox").Text);
            await CaptureUiIfRequested(window, "main-logs-expanded.png");
            Find<Expander>(window, "ApplicationSettingsPanel").IsExpanded = true;
            await CaptureUiIfRequested(window, "main-settings-and-logs-expanded.png");
            Find<Expander>(window, "ApplicationSettingsPanel").IsExpanded = false;
            expander.IsExpanded = false;
            Layout();
            Assert.Equal(collapsedHeight, expander.ActualHeight);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(980, 660)]
    [InlineData(780, 500)]
    public Task ExpandedSettingsAndLogsRemainReachableInSmallWindows(double width, double height) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            Find<Expander>(window, "LogsExpander").IsExpanded = true;
            Find<Expander>(window, "ApplicationSettingsPanel").IsExpanded = true;
            var content = (FrameworkElement)window.Content;
            var size = new Size(width, height);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            var scroll = Find<ScrollViewer>(window, "ConnectionsScrollViewer");
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight + 50);
            Assert.True(Find<DataGrid>(window, "ConnectionsGrid").ActualHeight >= 100);
            await CaptureUiIfRequested(window, $"main-both-expanded-{width}-top.png", renderSize: size);
            scroll.ScrollToEnd();
            content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            var environment = Find<TextBlock>(window, "EnvironmentText");
            var bounds = environment.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), environment.RenderSize));
            Assert.True(bounds.Top >= 0, "Environment information must be reachable by scrolling.");
            Assert.True(bounds.Bottom <= scroll.ActualHeight, "The bottom of the environment information must fit in the viewport.");
            await CaptureUiIfRequested(window, $"main-both-expanded-{width}-bottom.png", renderSize: size);
        }
        finally { CloseWithoutShowing(window); }
    });

    private static T Find<T>(FrameworkElement window, string name) => (T)window.FindName(name);

    [Fact]
    public Task SilentTrayExitSavesAndWaitsForUnmountWithoutShowingTheWindow() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var unmounting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var closed = false;
        var shown = false;
        var activated = false;
        var notifications = new List<string>();
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        window.Closed += (_, _) => closed = true;
        window.IsVisibleChanged += (_, _) => shown |= window.IsVisible;
        window.Activated += (_, _) => activated = true;
        async Task StopMounts()
        {
            calls++;
            Assert.False((await files.Store.LoadAsync()).App.MinimizeToTray);
            unmounting.SetResult();
            await release.Task;
        }
        try
        {
            await window.LoadConfigAsync();
            Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked = false;
            var exiting = window.ExitFromTrayAsync(StopMounts, notifications.Add);
            await unmounting.Task;
            Assert.False(closed);
            Assert.False(exiting.IsCompleted);
            Assert.False(shown);
            Assert.False(activated);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            await window.ExitFromTrayAsync(StopMounts, notifications.Add);
            Assert.Equal(1, calls);
            release.SetResult();
            await exiting;
            Assert.True(closed);
            Assert.False(shown);
            Assert.False(activated);
            Assert.Empty(notifications);
        }
        finally { release.TrySetResult(); if (!closed) CloseWithoutShowing(window); }
    });

    [Fact]
    public Task SilentTrayExitDuringStartupCompletesInitializationWithoutShowingOrMounting() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var preparations = 0;
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => { preparations++; return Task.FromResult(@"C:\managed\rclone.exe"); });
        var closed = false;
        var shown = false;
        var notifications = new List<string>();
        window.Closed += (_, _) => closed = true;
        window.IsVisibleChanged += (_, _) => shown |= window.IsVisible;
        try
        {
            var initialization = window.InitializeAsync();
            await window.ExitFromTrayAsync(() => Task.CompletedTask, notifications.Add);
            Assert.True(initialization.IsCompletedSuccessfully);
            Assert.True(closed);
            Assert.False(shown);
            Assert.Equal(0, preparations);
            Assert.Empty(notifications);
        }
        finally { if (!closed) CloseWithoutShowing(window); }
    });

    [Fact]
    public Task SilentTrayExitPreservesUnsavedChangesAndNotifiesWithoutUnmounting() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var failSave = true;
        Task Save(MountloomConfig config) => failSave
            ? Task.FromException(new IOException("Simulated full disk."))
            : files.Store.SaveAsync(config);
        var window = new MainWindow(files.Store, enableTray: false, Save, enablePersistentLogs: false);
        var notifications = new List<string>();
        var unmounts = 0;
        var closed = false;
        var shown = false;
        window.Closed += (_, _) => closed = true;
        window.IsVisibleChanged += (_, _) => shown |= window.IsVisible;
        try
        {
            await window.LoadConfigAsync();
            Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked = false;
            await window.ExitFromTrayAsync(() => { unmounts++; return Task.CompletedTask; }, notifications.Add);
            Assert.Contains("配置未能保存", Assert.Single(notifications));
            Assert.Equal(0, unmounts);
            Assert.False(closed);
            Assert.False(shown);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.False(Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked);
            Assert.True((await files.Store.LoadAsync()).App.MinimizeToTray);
            failSave = false;
            await window.ExitFromTrayAsync(() => { unmounts++; return Task.CompletedTask; }, notifications.Add);
            Assert.True(closed);
            Assert.Equal(1, unmounts);
            Assert.Single(notifications);
            Assert.False(shown);
            Assert.False((await files.Store.LoadAsync()).App.MinimizeToTray);
        }
        finally { if (!closed) CloseWithoutShowing(window); }
    });

    [Fact]
    public Task SilentTrayExitKeepsAnOpenConnectionDraftWithoutActivatingEitherWindow() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        ConnectionEditorWindow? editor = null;
        var editorField = typeof(MainWindow).GetField("_connectionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var notifications = new List<string>();
        var closed = false;
        var activated = false;
        var unmounts = 0;
        window.Closed += (_, _) => closed = true;
        window.Activated += (_, _) => activated = true;
        try
        {
            await window.LoadConfigAsync();
            editor = window.CreateConnectionEditor((ConnectionRow)Find<DataGrid>(window, "ConnectionsGrid").SelectedItem);
            editor.Activated += (_, _) => activated = true;
            Find<TextBox>(editor, "NameBox").Text = "Unsaved draft";
            editorField.SetValue(window, editor);
            await window.ExitFromTrayAsync(() => { unmounts++; return Task.CompletedTask; }, notifications.Add);
            Assert.Contains("仍在编辑", Assert.Single(notifications));
            Assert.False(closed);
            Assert.False(activated);
            Assert.False(window.IsVisible);
            Assert.False(editor.IsVisible);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(editor).Handle);
            Assert.Equal(0, unmounts);
            Assert.Equal("Unsaved draft", Find<TextBox>(editor, "NameBox").Text);
            Assert.Equal("Alpha", (await files.Store.LoadAsync()).Connections[0].Name);
        }
        finally { editorField.SetValue(window, null); editor?.Close(); CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SilentTrayExitCancelsOnUnmountFailureAndCanBeRetriedWithoutShowingTheWindow(bool needsConfirmation) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        var closed = false;
        var shown = false;
        var attempts = 0;
        var notifications = new List<string>();
        window.Closed += (_, _) => closed = true;
        window.IsVisibleChanged += (_, _) => shown |= window.IsVisible;
        Task StopMounts()
        {
            if (++attempts > 1) return Task.CompletedTask;
            Exception error = needsConfirmation
                ? new AggregateException(new UnmountSafetyConfirmationRequiredException("alpha", "X:", "Open files cannot be checked."))
                : new IOException("Uploads are still pending.");
            return Task.FromException(error);
        }
        try
        {
            await window.LoadConfigAsync();
            await window.ExitFromTrayAsync(StopMounts, notifications.Add);
            Assert.False(closed);
            Assert.False(shown);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.Equal(1, attempts);
            Assert.Contains(needsConfirmation ? "X: 需要确认文件已关闭" : "Uploads are still pending", Assert.Single(notifications));
            await window.ExitFromTrayAsync(StopMounts, notifications.Add);
            Assert.True(closed);
            Assert.False(shown);
            Assert.Equal(2, attempts);
            Assert.Single(notifications);
        }
        finally { if (!closed) CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExitAfterASaveFailureHonorsTheDiscardDecisionAndPreservesTheLastSavedFile(bool discard) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var attempts = 0;
        Task FailSave(MountloomConfig _) { attempts++; return Task.FromException(new IOException("Simulated full disk.")); }
        var window = new MainWindow(files.Store, enableTray: false, FailSave, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked = false;
            var prompts = 0;
            var mayContinueWithNormalUnmount = await window.SaveBeforeExitAsync(() => { prompts++; return discard; });
            Assert.Equal(discard, mayContinueWithNormalUnmount);
            Assert.Equal(1, attempts);
            Assert.Equal(1, prompts);
            Assert.Equal("Alpha", (await files.Store.LoadAsync()).Connections[0].Name);
            Assert.True((await files.Store.LoadAsync()).App.MinimizeToTray);
            Assert.False(Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task InvalidUncommittedFieldsDoNotPreventUnmountingAnExistingSession() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, enableTray: false, enablePersistentLogs: false);
        try
        {
            await window.LoadConfigAsync();
            var row = (ConnectionRow)Find<DataGrid>(window, "ConnectionsGrid").SelectedItem;
            var dialog = window.CreateConnectionEditor(row);
            Find<ComboBox>(dialog, "ProtocolBox").SelectedItem = MountProtocol.Sftp;
            Find<TextBox>(dialog, "PortBox").Text = "unfinished";
            Assert.False(dialog.TryReadConnection(out _, out _));
            Assert.True(await window.SaveNowAsync());
            // A vanished process still has to be removable while the editor contains a draft.
            row.RefreshStatus(new MountRuntimeStatus { State = MountState.Mounted, ProcessId = 12345,
                ConnectionId = row.Connection.Id, MountedDriveLetter = "X:" });
            var stop = (Task<bool>)typeof(MainWindow).GetMethod("UnmountRowAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [row])!;
            Assert.True(await stop);
            Assert.Equal(MountState.NotMounted, row.State);
            Assert.Equal("unfinished", Find<TextBox>(dialog, "PortBox").Text);
            Assert.Equal(MountProtocol.WebDav, (await files.Store.LoadAsync()).Connections[0].Protocol);
            dialog.Close();
        }
        finally { CloseWithoutShowing(window); }
    });

    private static void CloseWithoutShowing(MainWindow window)
    {
        typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close();
    }

    [Fact]
    public Task AutomaticPreparationIsSharedAndReplacesAnOldManualPathWithoutLosingEdits() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.App.RclonePath = @"C:\obsolete\custom-rclone.exe";
        await files.Store.SaveAsync(config);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string> Ensure(CancellationToken token)
        {
            calls++;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return @"C:\managed\rclone.exe";
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false, ensureRclone: Ensure,
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)));
        try
        {
            await window.LoadConfigAsync();
            var first = window.PrepareDependenciesAsync();
            var second = window.PrepareDependenciesAsync(recheck: true);
            Assert.Same(first, second);
            await started.Task;
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            Assert.False(Find<Button>(window, "TestConnectionButton").IsEnabled);
            var dialog = window.CreateConnectionEditor((ConnectionRow)Find<DataGrid>(window, "ConnectionsGrid").SelectedItem);
            Find<TextBox>(dialog, "NameBox").Text = "Edited while preparing";
            release.SetResult();
            Assert.True(await first);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var saved = await files.Store.LoadAsync();
            Assert.Equal(@"C:\managed\rclone.exe", saved.App.RclonePath);
            Assert.Equal("Alpha", saved.Connections[0].Name);
            Assert.Equal("Edited while preparing", Find<TextBox>(dialog, "NameBox").Text);
            Assert.True(await dialog.SaveAsync());
            dialog.Close();
            saved = await files.Store.LoadAsync();
            Assert.Equal("Edited while preparing", saved.Connections[0].Name);
            Assert.Equal(@"C:\managed\rclone.exe", saved.App.RclonePath);
            Assert.Equal(1, calls);
            Assert.True(Find<Button>(window, "MountButton").IsEnabled);
            Assert.Null(window.FindName("RclonePathBox"));
            Assert.Null(window.FindName("InstallRcloneButton"));
            await CaptureUiIfRequested(window, "dependencies-ready.png");
        }
        finally { release.TrySetResult(); await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task MissingWinFspAllowsConnectionTestsButKeepsMountingDisabled() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => Task.FromResult(@"C:\managed\rclone.exe"),
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, false)));
        try
        {
            await window.LoadConfigAsync();
            Assert.True(await window.PrepareDependenciesAsync());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Find<Button>(window, "TestConnectionButton").IsEnabled);
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            Assert.True(Find<Button>(window, "InstallWinFspButton").IsEnabled);
            Assert.Contains("WinFsp", Find<TextBlock>(window, "DependencySummaryText").Text);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task FailedAutomaticPreparationCanRetryWithoutReportingFalseReadiness() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var attempts = 0;
        Task<string> Ensure(CancellationToken _) => ++attempts == 1
            ? Task.FromException<string>(new IOException("Synthetic unavailable network"))
            : Task.FromResult(@"C:\managed\rclone.exe");
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false, ensureRclone: Ensure,
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)));
        try
        {
            await window.LoadConfigAsync();
            Assert.False(await window.PrepareDependenciesAsync());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            Assert.False(Find<Button>(window, "TestConnectionButton").IsEnabled);
            Assert.True(Find<Button>(window, "DetectButton").IsEnabled);
            Assert.Contains("失败", Find<TextBlock>(window, "EnvironmentText").Text);
            await CaptureUiIfRequested(window, "dependencies-failed.png");
            Assert.True(await window.PrepareDependenciesAsync(recheck: true));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Find<Button>(window, "MountButton").IsEnabled);
            Assert.Equal(2, attempts);
        }
        finally { CloseWithoutShowing(window); }
    });

    [Fact]
    public Task DependencyPreparationCancellationCompletesBeforeShutdownAndCanBeRetried() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var stopped = false;
        async Task<string> Ensure(CancellationToken token)
        {
            if (++attempts > 1) return @"C:\managed\rclone.exe";
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return string.Empty;
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false, ensureRclone: Ensure,
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)));
        try
        {
            await window.LoadConfigAsync();
            var preparation = window.PrepareDependenciesAsync();
            await started.Task;
            await window.CancelDependencyWorkAsync();
            Assert.True(stopped);
            Assert.False(await preparation);
            Assert.True(await window.PrepareDependenciesAsync(recheck: true));
            Assert.Equal(2, attempts);
        }
        finally { await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task ACorruptConfigurationNeverStartsOrPersistsAutomaticPreparation() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await File.WriteAllTextAsync(files.Store.ConfigPath, "{broken");
        var calls = 0;
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => { calls++; return Task.FromResult(@"C:\managed\rclone.exe"); });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => window.LoadConfigAsync());
            Assert.False(await window.PrepareDependenciesAsync());
            Assert.Equal(0, calls);
            Assert.Equal("{broken", await File.ReadAllTextAsync(files.Store.ConfigPath));
        }
        finally { CloseWithoutShowing(window); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task HiddenStartupInitializesAndMountsOnceWithoutLoadingTheWindow(bool minimizeToTray) => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.App.MinimizeToTray = minimizeToTray;
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var preparations = 0;
        var mounts = new List<string>();
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => { preparations++; return Task.FromResult(@"C:\managed\rclone.exe"); },
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)),
            mount: (connection, _, _) => { mounts.Add(connection.Id); return Task.CompletedTask; });
        var becameVisible = false;
        window.IsVisibleChanged += (_, _) => becameVisible |= window.IsVisible;
        try
        {
            var startup = window.InitializeAsync();
            Assert.Same(startup, window.InitializeAsync());
            await startup;
            Assert.False(becameVisible);
            Assert.False(window.IsVisible);
            Assert.False(window.IsLoaded);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.Equal(minimizeToTray, Find<CheckBox>(window, "MinimizeToTrayBox").IsChecked);
            Assert.Equal([config.Connections[0].Id], mounts);

            var grid = Find<DataGrid>(window, "ConnectionsGrid");
            var row = Assert.IsType<ConnectionRow>(grid.Items[0]);
            await window.InitializeAsync();
            Assert.Same(row, grid.Items[0]);
            Assert.Equal(1, preparations);
            Assert.Single(mounts);
        }
        finally { await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task StartupMountsWaitForPreparationAndRunOnlyOnce() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mounts = new List<string>();
        async Task<string> Ensure(CancellationToken token)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return @"C:\managed\rclone.exe";
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false, ensureRclone: Ensure,
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)),
            mount: (connection, settings, _) =>
            {
                Assert.Equal(@"C:\managed\rclone.exe", settings.RclonePath);
                mounts.Add(connection.Id);
                return Task.CompletedTask;
            });
        try
        {
            await window.LoadConfigAsync();
            var startup = window.InitializeDependenciesAsync();
            await started.Task;
            Assert.Empty(mounts);
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            release.SetResult();
            await startup;
            Assert.Equal([config.Connections[0].Id], mounts);
            await window.InitializeDependenciesAsync();
            await window.RunStartupMountsAsync();
            Assert.Single(mounts);
        }
        finally { release.TrySetResult(); await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task FailedPreparationDefersStartupMountsUntilSuccessfulRepair() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var attempts = 0;
        var mounts = 0;
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => ++attempts == 1 ? Task.FromException<string>(new IOException("Network unavailable"))
                : Task.FromResult(@"C:\managed\rclone.exe"),
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)),
            mount: (_, _, _) => { mounts++; return Task.CompletedTask; });
        try
        {
            await window.LoadConfigAsync();
            await window.InitializeDependenciesAsync();
            Assert.Equal(0, mounts);
            Assert.True(await window.PrepareDependenciesAsync(recheck: true));
            await window.RunStartupMountsAsync();
            Assert.Equal(1, mounts);
            await window.RunStartupMountsAsync();
            Assert.Equal(1, mounts);
        }
        finally { await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task WinFspInstallationRechecksBeforeEnablingMountsAndResumesPendingStartup() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installed = false;
        var checks = 0;
        var mounts = 0;
        async Task Install(CancellationToken token)
        {
            installing.SetResult();
            await release.Task.WaitAsync(token);
            installed = true;
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => Task.FromResult(@"C:\managed\rclone.exe"),
            detectEnvironment: (path, _) => { checks++; return Task.FromResult(ReadyEnvironment(path, installed)); },
            installWinFsp: Install,
            mount: (_, _, _) => { Assert.True(installed); Assert.Equal(2, checks); mounts++; return Task.CompletedTask; });
        try
        {
            await window.LoadConfigAsync();
            await window.InitializeDependenciesAsync();
            Assert.Equal(0, mounts);
            var installation = window.InstallWinFspAsync();
            Assert.Same(installation, window.InstallWinFspAsync());
            await installing.Task;
            Assert.False(await window.PrepareDependenciesAsync(recheck: true));
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
            Assert.False(Find<Button>(window, "TestConnectionButton").IsEnabled);
            release.SetResult();
            Assert.True(await installation);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, checks);
            Assert.Equal(1, mounts);
            Assert.True(Find<Button>(window, "MountButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, Find<Button>(window, "InstallWinFspButton").Visibility);
            await window.RunStartupMountsAsync();
            Assert.Equal(1, mounts);
        }
        finally { release.TrySetResult(); await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task CancellingWinFspInstallationWaitsForInstallerAndPreventsFollowUpPreparation() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        await files.Store.SaveAsync(files.CreateConfig());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCalls = 0;
        var attempts = 0;
        async Task Install(CancellationToken _)
        {
            if (++attempts > 1) return;
            started.SetResult();
            // Model a child installer that needs time to stop after cancellation.
            await release.Task;
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => { preparationCalls++; return Task.FromResult(@"C:\managed\rclone.exe"); },
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, true)), installWinFsp: Install);
        try
        {
            await window.LoadConfigAsync();
            var installation = window.InstallWinFspAsync();
            await started.Task;
            var stopping = window.CancelDependencyWorkAsync();
            Assert.False(stopping.IsCompleted);
            release.SetResult();
            await stopping;
            Assert.False(await installation);
            Assert.Equal(0, preparationCalls);
            Assert.True(Find<Button>(window, "InstallWinFspButton").IsEnabled);
            Assert.True(await window.InstallWinFspAsync());
            Assert.Equal(1, preparationCalls);
        }
        finally { release.TrySetResult(); await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task CancellingDuringPostInstallRecheckWaitsForWholeWorkflow() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var stopped = false;
        var mounts = 0;
        async Task<EnvironmentStatus> Detect(string path, CancellationToken token)
        {
            if (++checks == 1) return ReadyEnvironment(path, false);
            checking.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return ReadyEnvironment(path, true);
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => Task.FromResult(@"C:\managed\rclone.exe"), detectEnvironment: Detect,
            installWinFsp: _ => Task.CompletedTask,
            mount: (_, _, _) => { mounts++; return Task.CompletedTask; });
        try
        {
            await window.LoadConfigAsync();
            await window.InitializeDependenciesAsync();
            var installation = window.InstallWinFspAsync();
            await checking.Task;
            await window.CancelDependencyWorkAsync();
            Assert.True(stopped);
            Assert.True(installation.IsCompleted);
            Assert.False(await installation);
            Assert.Equal(0, mounts);
            Assert.False(Find<Button>(window, "MountButton").IsEnabled);
        }
        finally { await window.CancelDependencyWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public Task ShutdownCancelsPostInstallStartupMountBeforeWaitingForInstallation() => OnSta(async () =>
    {
        using var files = new TemporaryConfig();
        var config = files.CreateConfig();
        config.Connections[0].AutoMount = true;
        await files.Store.SaveAsync(config);
        var mounting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installed = false;
        var mountCancelled = false;
        async Task Mount(MountConnection connection, AppSettings settings, CancellationToken token)
        {
            mounting.SetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { mountCancelled = true; throw; }
        }
        var window = new MainWindow(files.Store, false, enablePersistentLogs: false,
            ensureRclone: _ => Task.FromResult(@"C:\managed\rclone.exe"),
            detectEnvironment: (path, _) => Task.FromResult(ReadyEnvironment(path, installed)),
            installWinFsp: _ => { installed = true; return Task.CompletedTask; }, mount: Mount);
        try
        {
            await window.LoadConfigAsync();
            await window.InitializeDependenciesAsync();
            var installation = window.InstallWinFspAsync();
            await mounting.Task;
            Assert.False(installation.IsCompleted);
            typeof(MainWindow).GetField("_shuttingDown", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            await window.CancelPendingWorkAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(mountCancelled);
            Assert.False(await installation);
            Assert.False(Find<Button>(window, "CancelOperationButton").IsEnabled);
        }
        finally { release.TrySetResult(); await window.CancelPendingWorkAsync(); CloseWithoutShowing(window); }
    });

    [Fact]
    public void SetupGuardOnlyBlocksWhileTheInstallerMutexExists()
    {
        var name = @"Local\Mountloom-Setup-Test-" + Guid.NewGuid().ToString("N");
        Assert.False(Mountloom.App.App.IsSetupRunning(name));
        using (var setup = new Mutex(false, name)) Assert.True(Mountloom.App.App.IsSetupRunning(name));
        Assert.False(Mountloom.App.App.IsSetupRunning(name));
    }

    private static async Task CaptureUiIfRequested(Window window, string fileName, bool scrollToEnd = false, Size? renderSize = null)
    {
        var directory = Environment.GetEnvironmentVariable("MOUNTLY_UI_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        if (window is MainWindow && fileName.StartsWith("dependencies-", StringComparison.Ordinal))
            Find<Expander>(window, "ApplicationSettingsPanel").IsExpanded = true;
        var content = (FrameworkElement)window.Content;
        var size = renderSize ?? (window is ConnectionEditorWindow ? new Size(650, 700) : new Size(980, 660));
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        content.UpdateLayout();
        if (scrollToEnd)
        {
            ((Grid)content).Children.OfType<ScrollViewer>().Single().ScrollToEnd();
            content.UpdateLayout();
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private static EnvironmentStatus ReadyEnvironment(string path, bool winFsp) => new()
    {
        RcloneFound = true, RclonePath = path, RcloneVersion = "rclone v-test",
        WinFspFound = winFsp, WinFspVersion = winFsp ? "test" : string.Empty
    };

    private static Task OnSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class TemporaryConfig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MountloomEditorTests-" + Guid.NewGuid().ToString("N"));
        public ConfigStore Store { get; }
        public TemporaryConfig() { Directory.CreateDirectory(_directory); Store = new ConfigStore(_directory); }
        public MountloomConfig CreateConfig() => new()
        {
            App = new AppSettings { CacheRoot = Path.Combine(_directory, "cache"), LogRoot = Path.Combine(_directory, "logs") },
            Connections =
            [
                new MountConnection { Name = "Alpha", Host = "https://alpha.example/dav", DriveLetter = "X:", Secret = "alpha-secret" },
                new MountConnection { Name = "Beta", Host = "https://beta.example/dav", DriveLetter = "Y:", Secret = "beta-secret" }
            ]
        };
        public void Dispose() { Directory.Delete(_directory, recursive: true); }
    }
}

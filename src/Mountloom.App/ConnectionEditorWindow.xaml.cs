using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Mountloom.Core.Models;
using Mountloom.Core.Services;
using RcloneCacheMode = Mountloom.Core.Models.CacheMode;

namespace Mountloom.App;

public partial class ConnectionEditorWindow : Window
{
    private readonly MountConnection _initialConnection;
    private readonly string _cacheRoot;
    private readonly Func<string, IReadOnlyList<string>> _getDriveLetters;
    private readonly Func<MountConnection, Task<string?>> _saveConnection;
    private bool _saving;

    internal ConnectionEditorWindow(MountConnection connection, bool isNew, string cacheRoot,
        Func<string, IReadOnlyList<string>> getDriveLetters,
        Func<MountConnection, Task<string?>> saveConnection)
    {
        _initialConnection = connection.Clone();
        _cacheRoot = cacheRoot;
        _getDriveLetters = getDriveLetters;
        _saveConnection = saveConnection;
        InitializeComponent();
        Title = EditorTitleText.Text = isNew ? "新建连接" : "连接设置";
        ProtocolBox.ItemsSource = Enum.GetValues<MountProtocol>();
        CacheModeBox.ItemsSource = Enum.GetValues<RcloneCacheMode>();
        SftpAuthenticationBox.ItemsSource = Enum.GetValues<SftpAuthenticationMode>();
        LoadDraft();
    }

    internal bool TryReadConnection(out MountConnection connection, out string error)
    {
        connection = _initialConnection.Clone();
        error = string.Empty;
        var protocol = ProtocolBox.SelectedItem is MountProtocol selected ? selected : MountProtocol.WebDav;
        var port = 0;
        if (protocol is MountProtocol.Sftp or MountProtocol.Ftp && !string.IsNullOrWhiteSpace(PortBox.Text) &&
            (!int.TryParse(PortBox.Text, out port) || port < 0 || port > 65535))
        {
            error = "端口须为 1–65535，留空或 0 使用默认值。";
            return false;
        }
        var driveLetter = DriveLetterService.Normalize(DriveLetterBox.Text);
        if (!DriveLetterService.IsValid(driveLetter))
        {
            error = "请选择有效的 Windows 盘符，例如 Z:。";
            return false;
        }

        connection.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名连接" : NameBox.Text.Trim();
        connection.Protocol = protocol;
        connection.DriveLetter = driveLetter;
        connection.Host = HostBox.Text.Trim();
        connection.RemotePath = RemotePathBox.Text.Trim();
        if (protocol == MountProtocol.S3) connection.S3AccessKeyId = UsernameBox.Text.Trim();
        else connection.Username = UsernameBox.Text.Trim();
        connection.Secret = SecretBox.Password;
        connection.Port = port;
        connection.SftpAuthentication = SftpAuthenticationBox.SelectedItem is SftpAuthenticationMode auth
            ? auth : SftpAuthenticationMode.Password;
        connection.SftpKeyFile = SftpKeyFileBox.Text.Trim();
        connection.SftpKeyPassphrase = SftpKeyPassphraseBox.Password;
        connection.SftpKnownHostsFile = SftpKnownHostsFileBox.Text.Trim();
        connection.S3Region = S3RegionBox.Text.Trim();
        connection.S3Endpoint = S3EndpointBox.Text.Trim();
        connection.CacheMode = CacheModeBox.SelectedItem is RcloneCacheMode cacheMode ? cacheMode : RcloneCacheMode.Full;
        connection.CacheDirectory = CustomCacheDirectoryBox.IsChecked == true ? CacheDirectoryBox.Text.Trim() : string.Empty;
        connection.VfsCacheMaxSize = CacheSizeBox.Text.Trim();
        connection.VfsCacheMinFreeSpace = CacheFreeSpaceBox.Text.Trim();
        connection.AdvancedArguments = AdvancedArgumentsBox.Text.Trim();
        connection.AutoMount = AutoMountBox.IsChecked == true;
        connection.ReadOnly = ReadOnlyBox.IsChecked == true;

        foreach (var size in new[] { connection.VfsCacheMaxSize, connection.VfsCacheMinFreeSpace })
        {
            if (!Regex.IsMatch(size, @"^\d+(?:\.\d+)?(?:[kKmMgGtTpPeE](?:i?[bB])?|[bB])?$"))
            {
                error = "缓存容量和最小剩余空间须填写有效大小，例如 10G、512M 或 0。";
                return false;
            }
        }
        if (CustomCacheDirectoryBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(connection.CacheDirectory) || connection.CacheDirectory.Any(char.IsControl))
            {
                error = "请填写有效的自定义缓存目录，或取消勾选以使用默认目录。";
                return false;
            }
            try { _ = Path.GetFullPath(connection.CacheDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "自定义缓存目录无效，请检查目录路径。";
                return false;
            }
        }
        try { RcloneArguments.ParseAdvanced(connection.AdvancedArguments); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            error = $"附加参数无效：{ex.Message}";
            return false;
        }
        return true;
    }

    internal async Task<bool> SaveAsync()
    {
        if (_saving) return false;
        if (!TryReadConnection(out var connection, out var error))
        {
            ShowError(error);
            return false;
        }
        _saving = true;
        EditorPanel.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        SaveButton.Content = "正在保存…";
        ShowError(null);
        try
        {
            var saveError = await _saveConnection(connection);
            if (saveError is null) return true;
            ShowError(string.IsNullOrWhiteSpace(saveError) ? "保存失败，请重试。" : saveError);
            return false;
        }
        catch (Exception ex)
        {
            ShowError("保存失败：" + LogRedactor.Redact(ex.Message, connection));
            return false;
        }
        finally
        {
            _saving = false;
            EditorPanel.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = true;
            SaveButton.Content = "保存";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync()) DialogResult = true;
    }

    private void ShowError(string? error)
    {
        EditorErrorText.Text = error ?? string.Empty;
        EditorErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_saving) e.Cancel = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var workArea = transform is { } matrix
            ? Rect.Transform(new Rect(screen.X, screen.Y, screen.Width, screen.Height), matrix)
            : SystemParameters.WorkArea;
        MaxWidth = Math.Max(1, workArea.Width - 24);
        MaxHeight = Math.Max(1, workArea.Height - 24);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        Left = Math.Clamp(Left, workArea.Left + 12, workArea.Right - Width - 12);
        Top = Math.Clamp(Top, workArea.Top + 12, workArea.Bottom - Height - 12);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void LoadDraft()
    {
        var connection = _initialConnection;
        NameBox.Text = connection.Name;
        ProtocolBox.SelectedItem = connection.Protocol;
        RefreshDriveLetters(connection.DriveLetter);
        HostBox.Text = connection.Host;
        RemotePathBox.Text = connection.RemotePath;
        if (connection.Protocol == MountProtocol.S3 && string.IsNullOrWhiteSpace(connection.Host))
        {
            var legacyPath = connection.RemotePath.Trim('/').Split('/', 2);
            HostBox.Text = legacyPath[0];
            RemotePathBox.Text = legacyPath.Length > 1 ? legacyPath[1] : string.Empty;
        }
        UsernameBox.Text = connection.Protocol == MountProtocol.S3 ? connection.S3AccessKeyId : connection.Username;
        SecretBox.Password = connection.Secret;
        PortBox.Text = connection.Port == 0 ? string.Empty : connection.Port.ToString();
        SftpAuthenticationBox.SelectedItem = connection.SftpAuthentication;
        SftpKeyFileBox.Text = connection.SftpKeyFile;
        SftpKeyPassphraseBox.Password = connection.SftpKeyPassphrase;
        SftpKnownHostsFileBox.Text = connection.SftpKnownHostsFile;
        S3RegionBox.Text = connection.S3Region;
        S3EndpointBox.Text = connection.S3Endpoint;
        CacheModeBox.SelectedItem = connection.CacheMode;
        CustomCacheDirectoryBox.IsChecked = !string.IsNullOrWhiteSpace(connection.CacheDirectory);
        CacheDirectoryBox.Text = connection.CacheDirectory;
        CacheSizeBox.Text = connection.VfsCacheMaxSize;
        CacheFreeSpaceBox.Text = connection.VfsCacheMinFreeSpace;
        AdvancedArgumentsBox.Text = connection.AdvancedArguments;
        AutoMountBox.IsChecked = connection.AutoMount;
        ReadOnlyBox.IsChecked = connection.ReadOnly;
        UpdateProtocolFields();
        UpdateCacheDirectoryState();
    }

    private void RefreshDriveLetters(string? selected = null)
    {
        selected ??= DriveLetterBox.Text;
        DriveLetterBox.ItemsSource = _getDriveLetters(selected);
        DriveLetterBox.Text = selected;
    }

    private void DriveLetterBox_DropDownOpened(object sender, EventArgs e) => RefreshDriveLetters();

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SftpPanel is not null) UpdateProtocolFields();
    }

    private void SftpAuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SftpKeyPanel is not null) UpdateProtocolFields();
    }

    private void UpdateProtocolFields()
    {
        var protocol = ProtocolBox.SelectedItem is MountProtocol selected ? selected : MountProtocol.WebDav;
        var isS3 = protocol == MountProtocol.S3;
        var isSftp = protocol == MountProtocol.Sftp;
        S3Panel.Visibility = isS3 ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        PortPanel.Visibility = protocol is MountProtocol.Sftp or MountProtocol.Ftp ? Visibility.Visible : Visibility.Collapsed;
        SftpKeyPanel.Visibility = SftpAuthenticationBox.SelectedItem is SftpAuthenticationMode.KeyFile ? Visibility.Visible : Visibility.Collapsed;
        SecretPanel.Visibility = isSftp && SftpAuthenticationBox.SelectedItem is not SftpAuthenticationMode.Password
            ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Text = protocol switch { MountProtocol.WebDav => "WebDAV URL", MountProtocol.S3 => "Bucket", _ => "服务器地址" };
        RemotePathLabel.Text = isS3 ? "Prefix（Bucket 内目录，可留空）" : "远程路径";
        UsernameLabel.Text = isS3 ? "Access Key ID" : "用户名";
        SecretLabel.Text = isS3 ? "Secret Access Key" : "密码";
        EditHintText.Text = protocol switch
        {
            MountProtocol.WebDav => "例如 https://example.com/dav。保存后可测试连接，再挂载。",
            MountProtocol.S3 => "Bucket 与 Prefix 分开填写；S3 兼容存储请填写 Endpoint。",
            MountProtocol.Sftp => "绝对路径以 / 开头，相对路径从账号主目录开始。请指定受信任的 known_hosts 文件。",
            _ => "绝对路径以 / 开头。FTP 默认端口为 21。"
        };
    }

    private void CustomCacheDirectoryBox_Changed(object sender, RoutedEventArgs e) => UpdateCacheDirectoryState();

    private void UpdateCacheDirectoryState()
    {
        if (CacheDirectoryBox is null) return;
        CacheDirectoryBox.IsEnabled = CustomCacheDirectoryBox.IsChecked == true;
        CacheDirectoryBox.ToolTip = Path.Combine(_cacheRoot, _initialConnection.Id);
    }

    private void BrowseSftpFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Title = "选择认证文件" };
        if (dialog.ShowDialog(this) != true) return;
        if (sender is Button { Tag: "KnownHosts" }) SftpKnownHostsFileBox.Text = dialog.FileName;
        else SftpKeyFileBox.Text = dialog.FileName;
    }
}

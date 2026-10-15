using System.ComponentModel;
using Mountloom.Core.Models;

namespace Mountloom.App;

public sealed class ConnectionRow : INotifyPropertyChanged
{
    private MountRuntimeStatus _status;
    public ConnectionRow(MountConnection connection, MountRuntimeStatus status) { Connection = connection; _status = status; }
    public MountConnection Connection { get; private set; }
    internal void ApplyConnection(MountConnection connection)
    {
        Connection = connection;
        RefreshConnection();
    }
    public string Name => Connection.Name;
    public MountProtocol Protocol => Connection.Protocol;
    public string DriveLetter => string.IsNullOrEmpty(_status.MountedDriveLetter) ? Connection.DriveLetter : _status.MountedDriveLetter;
    public bool AutoMount => Connection.AutoMount;
    public MountState State => _status.State;
    public int? ProcessId => _status.ProcessId;
    public string LastError => string.IsNullOrEmpty(OperationError) ? _status.LastError : OperationError;
    public string OperationError { get; set; } = string.Empty;
    public bool NeedsReconnect { get; set; }
    public string StateText => State switch
    {
        MountState.NotMounted => "未挂载", MountState.Mounting => "挂载中", MountState.Mounted => "已挂载",
        MountState.Unmounting => "卸载中", MountState.Reconnecting => "重连中", MountState.Failed => "失败", _ => "已禁用"
    };
    public string StatusDetail
    {
        get
        {
            var parts = new List<string> { StateText };
            if (ProcessId is { } pid) parts.Add($"PID {pid}");
            if (_status.PendingUploads is { } pending) parts.Add($"待上传 {pending}");
            if (_status.ActiveUploads is { } uploading) parts.Add($"上传中 {uploading}");
            if (_status.CacheBytesUsed is { } bytes) parts.Add($"缓存 {bytes / 1024d / 1024d:N1} MB");
            if (_status.CacheOutOfSpace == true) parts.Add("缓存空间不足");
            if (!string.IsNullOrWhiteSpace(_status.HealthWarning)) parts.Add(_status.HealthWarning);
            if (!string.IsNullOrWhiteSpace(LastError)) parts.Add(LastError);
            if (NeedsReconnect) parts.Add("配置已修改，重连后生效");
            return string.Join(" · ", parts);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshStatus(MountRuntimeStatus status)
    {
        _status = status;
        if (status.State == MountState.NotMounted) NeedsReconnect = false;
        RefreshConnection();
    }
    public void RefreshConnection() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

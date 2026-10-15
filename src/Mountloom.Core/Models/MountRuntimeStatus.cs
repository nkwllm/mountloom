namespace Mountloom.Core.Models;

public sealed class MountRuntimeStatus
{
    public string ConnectionId { get; set; } = string.Empty;
    public MountState State { get; set; } = MountState.NotMounted;
    public int? ProcessId { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset? LastMountedAt { get; set; }
    public string MountedDriveLetter { get; set; } = string.Empty;
    public long? PendingUploads { get; set; }
    public long? ActiveUploads { get; set; }
    public long? OpenFiles { get; set; }
    public long? CacheBytesUsed { get; set; }
    public bool? CacheOutOfSpace { get; set; }
    public string HealthWarning { get; set; } = string.Empty;

    public MountRuntimeStatus Clone() => (MountRuntimeStatus)MemberwiseClone();
}

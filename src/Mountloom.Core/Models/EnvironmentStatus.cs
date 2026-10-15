namespace Mountloom.Core.Models;

public sealed class EnvironmentStatus
{
    public bool RcloneFound { get; set; }
    public string RclonePath { get; set; } = string.Empty;
    public string RcloneVersion { get; set; } = string.Empty;
    public bool WinFspFound { get; set; }
    public string WinFspVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

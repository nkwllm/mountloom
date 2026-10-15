namespace Mountloom.Core.Models;

public enum MountState
{
    NotMounted,
    Mounting,
    Mounted,
    Unmounting,
    Failed,
    Reconnecting,
    Disabled
}

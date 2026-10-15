using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mountloom.Core.Services.Runtime;

public sealed class WindowsMountProbe : IMountProbe
{
    public bool IsDrivePresent(string driveLetter) => DriveInfo.GetDrives().Any(drive =>
        string.Equals(DriveLetterService.Normalize(drive.Name), driveLetter, StringComparison.OrdinalIgnoreCase));

    public bool TryAcquireUnmountLock(string driveLetter, out IDisposable? lease, out string reason)
    {
        lease = null;
        if (!OperatingSystem.IsWindows())
        {
            reason = "Safe unmount requires Windows volume locking.";
            return false;
        }

        var handle = CreateFile(@"\\.\" + DriveLetterService.Validate(driveLetter),
            0x80000000 | 0x40000000, 0x1 | 0x2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            reason = "Cannot exclusively lock the mounted drive: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            handle.Dispose();
            return false;
        }

        const uint fsctlLockVolume = 0x00090018;
        if (!DeviceIoControl(handle, fsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            reason = "The drive has open files or its driver does not support safe volume locking: " +
                new Win32Exception(Marshal.GetLastWin32Error()).Message;
            handle.Dispose();
            return false;
        }

        // Closing the handle releases the volume lock, including exceptional paths.
        lease = handle;
        reason = string.Empty;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input,
        uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}

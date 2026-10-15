using System.Runtime.InteropServices;

namespace Mountloom.Core.Services.Runtime;

/// <summary>Refreshes shell drive views without restarting Explorer or changing drive mappings.</summary>
public sealed class WindowsMountShellNotifier : IMountShellNotifier
{
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlushNoWait = 0x2000;
    private const int ShcneDriveRemoved = 0x0080;
    private const int ShcneUpdateDir = 0x1000;

    public void NotifyDriveRemoved(string driveLetter)
    {
        var root = DriveLetterService.Validate(driveLetter) + "\\";
        if (!OperatingSystem.IsWindows()) { return; }

        // Explorer can retain a cached network-drive entry even after a shell folder
        // refresh. Re-send the device removal for this drive only, after the manager
        // has confirmed that its process exited and Windows released the drive.
        var volume = new DeviceBroadcastVolume
        {
            Size = (uint)Marshal.SizeOf<DeviceBroadcastVolume>(),
            DeviceType = 2, // DBT_DEVTYP_VOLUME
            UnitMask = 1u << (root[0] - 'A'),
            Flags = 2 // DBTF_NET
        };
        // Bound each recipient's wait; an unresponsive application must not prevent
        // mount cleanup. The shell notifications below are still sent on timeout.
        _ = SendMessageTimeout(new IntPtr(0xffff), 0x0219, new UIntPtr(0x8004),
            ref volume, 0x0002, 100, out _); // WM_DEVICECHANGE / DBT_DEVICEREMOVECOMPLETE

        // The driver can release a network drive without Explorer receiving its device
        // notification. Queue shell delivery without waiting on other applications.
        SHChangeNotifyPath(ShcneDriveRemoved, ShcnfPathW | ShcnfFlushNoWait, root, IntPtr.Zero);

        // Refresh This PC and its navigation tree as well: a drive removal alone can
        // leave a ghost network location there. This is also WinFsp's shell workaround.
        var computerFolder = new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7");
        var result = SHGetKnownFolderIDList(in computerFolder, 0, IntPtr.Zero, out var pidl);
        if (result < 0 || pidl == IntPtr.Zero) { return; }
        try { SHChangeNotifyIdList(ShcneUpdateDir, ShcnfFlushNoWait, pidl, IntPtr.Zero); }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotifyPath(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true)]
    private static extern void SHChangeNotifyIdList(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderIDList(in Guid folderId, uint flags, IntPtr token, out IntPtr pidl);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceBroadcastVolume
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public uint UnitMask;
        public ushort Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr eventId,
        ref DeviceBroadcastVolume volume, uint flags, uint timeoutMilliseconds, out UIntPtr result);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Mountloom.Core.Services.Runtime;

/// <summary>Temporarily assigns the app icon to an owned mount for the current Windows user.</summary>
public sealed class WindowsMountDriveIconService : IMountDriveIconService
{
    private static readonly object Sync = new();
    private readonly string _iconPath;
    private readonly IDriveIconRegistry? _registry;
    private readonly Action<string> _refresh;
    private readonly Func<string, bool> _isDrivePresent;

    public WindowsMountDriveIconService()
    {
        _iconPath = Path.Combine(AppContext.BaseDirectory, "Mountloom.ico");
        _registry = OperatingSystem.IsWindows() ? new WindowsDriveIconRegistry() : null;
        _refresh = RefreshDrive;
        _isDrivePresent = new WindowsMountProbe().IsDrivePresent;
    }

    internal WindowsMountDriveIconService(string iconPath, IDriveIconRegistry registry, Action<string> refresh,
        Func<string, bool> isDrivePresent)
    {
        _iconPath = Path.GetFullPath(iconPath);
        _registry = registry;
        _refresh = refresh;
        _isDrivePresent = isDrivePresent;
    }

    /// <summary>Recovers icons left by logoff or an interrupted app, without changing any present drive.</summary>
    public void RecoverStaleIcons()
    {
        if (!OperatingSystem.IsWindows() || _registry is null) { return; }
        var errors = new List<Exception>();
        lock (Sync)
        {
            foreach (var driveKey in _registry.ReadJournalDrives())
            {
                if (driveKey.Length != 1 || !DriveLetterService.IsValid(driveKey + ":")) { continue; }
                try
                {
                    var drive = DriveLetterService.Validate(driveKey + ":");
                    if (_isDrivePresent(drive)) { continue; }
                    var ownership = _registry.ReadOwnership(driveKey);
                    if (ownership is not null) { RestoreOwnership(drive, ownership.LeaseId); }
                }
                catch (Exception error) { errors.Add(error); }
            }
        }
        if (errors.Count != 0) { throw new AggregateException("Some abandoned drive icons could not be restored.", errors); }
    }

    public IDisposable? Apply(string driveLetter)
    {
        var drive = DriveLetterService.Validate(driveLetter);
        if (!OperatingSystem.IsWindows() || _registry is null || !File.Exists(_iconPath)) { return null; }
        lock (Sync)
        {
            var driveKey = drive[..1];
            var iconKey = driveKey + @"\DefaultIcon";
            var current = _registry.ReadDefault(iconKey);
            var abandoned = _registry.ReadOwnership(driveKey);
            var adopt = abandoned is not null && MatchesAssignment(current, abandoned);
            // Apply is called only for a freshly owned ready mount. If an old
            // release left its icon, keep the genuine original instead of backing
            // up the old release's icon path. A unique ID invalidates older leases.
            var ownership = new DriveIconOwnership(Guid.NewGuid().ToString("N"),
                adopt ? abandoned!.DriveExisted : _registry.KeyExists(driveKey),
                adopt ? abandoned!.IconExisted : _registry.KeyExists(iconKey),
                adopt ? abandoned!.Previous : current,
                new($"\"{_iconPath}\",0", RegistryValueKind.String),
                adopt ? current : null);
            // Persist and flush the whole snapshot before changing the icon. During
            // handover either recorded assignment is ours, even if a crash occurs
            // between this write and the Explorer override write below.
            _registry.WriteOwnership(driveKey, ownership);
            var lease = new IconLease(this, drive, ownership.LeaseId);
            try
            {
                _registry.WriteDefault(iconKey, ownership.Assigned);
                if (ownership.PriorAssignment is not null)
                {
                    _registry.WriteOwnership(driveKey, ownership with { PriorAssignment = null });
                }
                _refresh(drive);
                return lease;
            }
            catch
            {
                // If rollback also fails, retain the persisted journal for recovery.
                try { lease.Dispose(); } catch { }
                throw;
            }
        }
    }

    private static bool MatchesAssignment(DriveIconRegistryValue? current, DriveIconOwnership ownership) =>
        current == ownership.Assigned || (ownership.PriorAssignment is not null && current == ownership.PriorAssignment);

    private void RestoreOwnership(string drive, string leaseId)
    {
        lock (Sync)
        {
            var driveKey = drive[..1];
            var iconKey = driveKey + @"\DefaultIcon";
            var ownership = _registry!.ReadOwnership(driveKey);
            if (ownership is null || ownership.LeaseId != leaseId) { return; }
            // Preserve a replacement made by the user or another app while mounted.
            var changed = MatchesAssignment(_registry.ReadDefault(iconKey), ownership);
            if (changed)
            {
                if (ownership.Previous is null) { _registry.DeleteDefault(iconKey); }
                else { _registry.WriteDefault(iconKey, ownership.Previous); }
                if (!ownership.IconExisted) { _registry.DeleteKeyIfEmpty(iconKey); }
                if (!ownership.DriveExisted) { _registry.DeleteKeyIfEmpty(driveKey); }
            }
            _registry.DeleteOwnership(driveKey);
            if (changed) { _refresh(drive); }
        }
    }

    private sealed class IconLease(WindowsMountDriveIconService service, string drive, string leaseId) : IDisposable
    {
        public void Dispose() => service.RestoreOwnership(drive, leaseId);
    }

    private static void RefreshDrive(string drive)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // Ask Explorer to re-read only this drive's icon and attributes. The calls
        // queue delivery, so an unresponsive Explorer cannot delay safe shutdown.
        SHChangeNotifyPath(0x2000 | 0x0800, 0x0005 | 0x2000, drive + "\\", IntPtr.Zero);
        var computerFolder = new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7");
        if (SHGetKnownFolderIDList(in computerFolder, 0, IntPtr.Zero, out var pidl) < 0 || pidl == IntPtr.Zero) { return; }
        try { SHChangeNotifyIdList(0x1000, 0x2000, pidl, IntPtr.Zero); }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotifyPath(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true)]
    private static extern void SHChangeNotifyIdList(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderIDList(in Guid folderId, uint flags, IntPtr token, out IntPtr pidl);
}

internal sealed record DriveIconRegistryValue(object Data, RegistryValueKind Kind);
internal sealed record DriveIconOwnership(string LeaseId, bool DriveExisted, bool IconExisted,
    DriveIconRegistryValue? Previous, DriveIconRegistryValue Assigned, DriveIconRegistryValue? PriorAssignment = null);

internal interface IDriveIconRegistry
{
    bool KeyExists(string key);
    DriveIconRegistryValue? ReadDefault(string key);
    void WriteDefault(string key, DriveIconRegistryValue value);
    void DeleteDefault(string key);
    void DeleteKeyIfEmpty(string key);
    IReadOnlyList<string> ReadJournalDrives();
    DriveIconOwnership? ReadOwnership(string driveKey);
    void WriteOwnership(string driveKey, DriveIconOwnership ownership);
    void DeleteOwnership(string driveKey);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDriveIconRegistry : IDriveIconRegistry
{
    // This is the per-user Explorer drive-icon registration, not the machine-wide
    // DriveIcons key. No administrator rights or files on the remote are needed.
    private const string Root = @"Software\Classes\Applications\Explorer.exe\Drives";
    private const string JournalRoot = @"Software\Mountly\DriveIcons";

    public bool KeyExists(string key)
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(Root + "\\" + key);
        return registryKey is not null;
    }

    public DriveIconRegistryValue? ReadDefault(string key)
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(Root + "\\" + key);
        var data = registryKey?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return data is null ? null : new(data, registryKey!.GetValueKind(string.Empty));
    }

    public void WriteDefault(string key, DriveIconRegistryValue value)
    {
        using var registryKey = Registry.CurrentUser.CreateSubKey(Root + "\\" + key);
        registryKey.SetValue(string.Empty, value.Data, value.Kind);
        registryKey.Flush();
    }

    public void DeleteDefault(string key)
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(Root + "\\" + key, writable: true);
        registryKey?.DeleteValue(string.Empty, throwOnMissingValue: false);
        registryKey?.Flush();
    }

    public void DeleteKeyIfEmpty(string key)
    {
        var path = Root + "\\" + key;
        using (var registryKey = Registry.CurrentUser.OpenSubKey(path))
        {
            if (registryKey is null || registryKey.ValueCount != 0 || registryKey.SubKeyCount != 0) { return; }
        }
        // Never delete a tree: unrelated labels, values and subkeys remain intact.
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    public IReadOnlyList<string> ReadJournalDrives()
    {
        using var key = Registry.CurrentUser.OpenSubKey(JournalRoot);
        return key?.GetValueNames() ?? [];
    }

    public DriveIconOwnership? ReadOwnership(string driveKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(JournalRoot);
        var value = key?.GetValue(driveKey);
        if (value is null) { return null; }
        if (value is not byte[] bytes) { throw new InvalidDataException("Invalid drive icon recovery record."); }
        return DecodeOwnership(bytes);
    }

    public void WriteOwnership(string driveKey, DriveIconOwnership ownership)
    {
        using var key = Registry.CurrentUser.CreateSubKey(JournalRoot);
        // One registry value replaces the complete record atomically, so an update
        // cannot leave the original icon paired with a different lease's metadata.
        key.SetValue(driveKey, EncodeOwnership(ownership), RegistryValueKind.Binary);
        key.Flush();
    }

    public void DeleteOwnership(string driveKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(JournalRoot, writable: true);
        key?.DeleteValue(driveKey, throwOnMissingValue: false);
        key?.Flush();
    }

    internal static byte[] EncodeOwnership(DriveIconOwnership ownership) =>
        JsonSerializer.SerializeToUtf8Bytes(new PersistedOwnership(1, ownership));

    internal static DriveIconOwnership DecodeOwnership(byte[] bytes)
    {
        var record = JsonSerializer.Deserialize<PersistedOwnership>(bytes);
        if (record is not { Version: 1, Ownership: not null } || string.IsNullOrEmpty(record.Ownership.LeaseId))
        {
            throw new InvalidDataException("Unsupported drive icon recovery record.");
        }
        var ownership = record.Ownership;
        var assigned = DecodeValue(ownership.Assigned);
        var prior = ownership.PriorAssignment is null ? null : DecodeValue(ownership.PriorAssignment);
        if (assigned.Kind != RegistryValueKind.String || prior is { Kind: not RegistryValueKind.String })
        {
            throw new InvalidDataException("Invalid owned drive icon value.");
        }
        return ownership with
        {
            Previous = ownership.Previous is null ? null : DecodeValue(ownership.Previous),
            Assigned = assigned,
            PriorAssignment = prior
        };
    }

    private static DriveIconRegistryValue DecodeValue(DriveIconRegistryValue value)
    {
        var data = (JsonElement)value.Data;
        object decoded = value.Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => data.GetString()!,
            RegistryValueKind.DWord => data.GetInt32(),
            RegistryValueKind.QWord => data.GetInt64(),
            RegistryValueKind.MultiString => data.Deserialize<string[]>()!,
            RegistryValueKind.Binary or RegistryValueKind.None => data.GetBytesFromBase64(),
            _ => throw new InvalidDataException("Unsupported original drive icon value type.")
        };
        return new(decoded, value.Kind);
    }

    private sealed record PersistedOwnership(int Version, DriveIconOwnership Ownership);
}

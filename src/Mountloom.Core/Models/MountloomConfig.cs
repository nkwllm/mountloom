namespace Mountloom.Core.Models;

public sealed class MountloomConfig
{
    public AppSettings App { get; set; } = new();
    public List<MountConnection> Connections { get; set; } = [];
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int MaxLogLines { get; set; } = 1000;
    public string RclonePath { get; set; } = "rclone.exe";
    // Keep existing caches and logs in place after the Mountly -> Mountloom rename.
    public string CacheRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mountly",
        "Cache");
    public string LogRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mountly",
        "Logs");

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

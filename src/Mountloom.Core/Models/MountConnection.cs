namespace Mountloom.Core.Models;

public sealed class MountConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Mount";
    public MountProtocol Protocol { get; set; } = MountProtocol.WebDav;
    public string DriveLetter { get; set; } = "X:";
    public string RemotePath { get; set; } = "/";
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public int Port { get; set; }
    public SftpAuthenticationMode SftpAuthentication { get; set; } = SftpAuthenticationMode.Password;
    public string SftpKeyFile { get; set; } = string.Empty;
    public string SftpKeyPassphrase { get; set; } = string.Empty;
    public string SftpKnownHostsFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
    public string S3AccessKeyId { get; set; } = string.Empty;
    public string S3Region { get; set; } = string.Empty;
    public string S3Endpoint { get; set; } = string.Empty;
    public bool AutoMount { get; set; }
    public bool ReadOnly { get; set; }
    public CacheMode CacheMode { get; set; } = CacheMode.Full;
    public string CacheDirectory { get; set; } = string.Empty;
    public string AdvancedArguments { get; set; } = string.Empty;
    public string VfsCacheMaxSize { get; set; } = "10G";
    public string VfsCacheMinFreeSpace { get; set; } = "1G";

    public MountConnection Clone() => (MountConnection)MemberwiseClone();
}

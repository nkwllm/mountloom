using Mountloom.Core.Models;
using Mountloom.Core.Services;

namespace Mountloom.Tests;

public sealed class ProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mountloom.ProtocolTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _obscuredInputs = [];

    public ProtocolTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task WebDavCredentialsArePassedInAnIsolatedEnvironmentWithoutConfigFiles()
    {
        var connection = WebDav();
        connection.Username = "user with spaces";
        connection.Secret = "quotes\" and a trailing slash\\";
        var before = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);

        var launch = await Writer().CreateAsync(connection, "rclone.exe");

        var prefix = Prefix(connection);
        Assert.Equal("webdav", launch.Environment[prefix + "TYPE"]);
        Assert.Equal(connection.Host, launch.Environment[prefix + "URL"]);
        Assert.Equal("user with spaces", launch.Environment[prefix + "USER"]);
        Assert.Equal("obscured-for-test", launch.Environment[prefix + "PASS"]);
        Assert.Equal(connection.Secret, Assert.Single(_obscuredInputs));
        Assert.DoesNotContain(connection.Secret, launch.Environment.Values);
        Assert.Equal(before, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.All(launch.Environment.Keys, key => Assert.StartsWith(prefix, key));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/projects", "/projects")]
    [InlineData("projects", "projects")]
    [InlineData("", "")]
    public void SftpTargetPreservesAbsoluteAndRelativePathSemantics(string input, string expected)
    {
        var connection = Sftp();
        connection.RemotePath = input;

        var arguments = RcloneArguments.BuildMount(connection, Settings());

        Assert.Equal($"{RcloneConfigWriter.GetRemoteName(connection)}:{expected}", arguments[3]);
    }

    [Theory]
    [InlineData(SftpAuthenticationMode.Password, "PASS")]
    [InlineData(SftpAuthenticationMode.KeyFile, "KEY_FILE_PASS")]
    [InlineData(SftpAuthenticationMode.Agent, "KEY_USE_AGENT")]
    public async Task SftpAuthenticationMethodsDoNotMixPasswordsWithPrivateKeyPassphrases(SftpAuthenticationMode mode, string expectedField)
    {
        var connection = Sftp();
        connection.SftpAuthentication = mode;
        connection.Secret = "login-password";
        connection.SftpKeyPassphrase = "private-key-passphrase";
        connection.SftpKeyFile = Path.Combine(_directory, "test private key");
        await File.WriteAllTextAsync(connection.SftpKeyFile, "Only file existence matters to this configuration test.");
        connection.Port = 2222;

        var launch = await Writer().CreateAsync(connection, "rclone.exe");

        var prefix = Prefix(connection);
        Assert.Equal("2222", launch.Environment[prefix + "PORT"]);
        Assert.Equal(Path.GetFullPath(connection.SftpKnownHostsFile), launch.Environment[prefix + "KNOWN_HOSTS_FILE"]);
        Assert.True(launch.Environment.ContainsKey(prefix + expectedField));
        if (mode == SftpAuthenticationMode.Password)
        {
            Assert.Equal("login-password", Assert.Single(_obscuredInputs));
            Assert.False(launch.Environment.ContainsKey(prefix + "KEY_FILE_PASS"));
            Assert.False(launch.Environment.ContainsKey(prefix + "KEY_FILE"));
        }
        else if (mode == SftpAuthenticationMode.KeyFile)
        {
            Assert.Equal("private-key-passphrase", Assert.Single(_obscuredInputs));
            Assert.Equal(Path.GetFullPath(connection.SftpKeyFile), launch.Environment[prefix + "KEY_FILE"]);
            Assert.False(launch.Environment.ContainsKey(prefix + "PASS"));
        }
        else
        {
            Assert.Equal("true", launch.Environment[prefix + expectedField]);
            Assert.Empty(_obscuredInputs);
            Assert.False(launch.Environment.ContainsKey(prefix + "PASS"));
            Assert.False(launch.Environment.ContainsKey(prefix + "KEY_FILE_PASS"));
        }
    }

    [Fact]
    public async Task SftpWithoutKnownHostsFailsBeforePreparingCredentials()
    {
        var connection = Sftp();
        connection.SftpKnownHostsFile = Path.Combine(_directory, "missing-known-hosts");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer().CreateAsync(connection, "rclone.exe"));

        Assert.Empty(_obscuredInputs);
    }

    [Theory]
    [InlineData("bucket-name", "/", "bucket-name")]
    [InlineData("bucket-name", "/photos/2026/", "bucket-name/photos/2026")]
    [InlineData("", "bucket-name/photos", "bucket-name/photos")]
    public void S3BucketAndPrefixProduceTheSelectedRemoteRoot(string bucket, string prefix, string expected)
    {
        var connection = S3();
        connection.Host = bucket;
        connection.RemotePath = prefix;

        var arguments = RcloneArguments.BuildMount(connection, Settings());

        Assert.Equal($"{RcloneConfigWriter.GetRemoteName(connection)}:{expected}", arguments[3]);
    }

    [Theory]
    [InlineData("", "AWS")]
    [InlineData("https://storage.example.test", "Other")]
    public async Task S3ProviderAndCredentialsMatchEndpointWithoutObscuringTheAccessKey(string endpoint, string provider)
    {
        var connection = S3();
        connection.S3Endpoint = endpoint;
        connection.S3Region = "test-region";

        var launch = await Writer().CreateAsync(connection, "rclone.exe");

        var prefix = Prefix(connection);
        Assert.Equal(provider, launch.Environment[prefix + "PROVIDER"]);
        Assert.Equal("false", launch.Environment[prefix + "ENV_AUTH"]);
        Assert.Equal(connection.Secret, launch.Environment[prefix + "SECRET_ACCESS_KEY"]);
        Assert.Equal(connection.S3AccessKeyId, launch.Environment[prefix + "ACCESS_KEY_ID"]);
        Assert.Equal("test-region", launch.Environment[prefix + "REGION"]);
        Assert.Empty(_obscuredInputs);
    }

    [Fact]
    public void MountArgumentsKeepSpecialPathsAsSingleArgumentsAndUseNoConfigFile()
    {
        var connection = WebDav();
        connection.RemotePath = "/folder with spaces/quote\"/backslash\\";
        connection.CacheDirectory = Path.Combine(_directory, "cache with spaces") + Path.DirectorySeparatorChar;
        connection.ReadOnly = true;

        var arguments = RcloneArguments.BuildMount(connection, Settings());
        var start = ProcessRunner.CreateStartInfo("rclone.exe", arguments);

        Assert.Empty(start.Arguments);
        Assert.Equal(arguments, start.ArgumentList);
        Assert.Equal("--config", arguments[0]);
        Assert.Equal("NUL", arguments[1]);
        Assert.Equal(RcloneConfigWriter.GetRemoteName(connection) + ":folder with spaces/quote\"/backslash\\", arguments[3]);
        Assert.Equal(Path.GetFullPath(connection.CacheDirectory), arguments[arguments.ToList().IndexOf("--cache-dir") + 1]);
        Assert.Contains("--read-only", arguments);
        Assert.DoesNotContain(connection.Secret, arguments);
    }

    [Theory]
    [InlineData("yao", "yao")]
    [InlineData("  我的云盘 Photos  ", "我的云盘 Photos")]
    [InlineData("工作/资料:2026", "工作_资料_2026")]
    [InlineData("\\\\server\\share", "__server_share")]
    [InlineData("Photos\r\n\"*?[]|<>+=;,¦", "Photos_______________")]
    [InlineData("Photos. .", "Photos")]
    [InlineData("...", "Mountloom X")]
    [InlineData("/\\:*?\"<>|", "Mountloom X")]
    [InlineData("   ", "Mountloom X")]
    [InlineData(null, "Mountloom X")]
    public void NetworkDriveUsesTheConfiguredNameAsASafeShareLabel(string? name, string expected)
    {
        var connection = WebDav();
        connection.Name = name!;

        var arguments = RcloneArguments.BuildMount(connection, Settings());
        var start = ProcessRunner.CreateStartInfo("rclone.exe", arguments);

        Assert.Contains("--network-mode", arguments);
        Assert.Equal(expected, arguments[arguments.ToList().IndexOf("--volname") + 1]);
        Assert.Equal(arguments, start.ArgumentList);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456789", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("一二三四五六七八九十十一", "一二三四五六七八九十")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234📁", "abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("📁📁📁📁📁📁📁📁📁", "📁📁📁📁📁📁📁📁")]
    public void LongDriveNamesStopBeforeRcloneWouldSplitAUnicodeCharacter(string name, string expected)
    {
        var connection = WebDav();
        connection.Name = name;

        var arguments = RcloneArguments.BuildMount(connection, Settings());

        Assert.Equal(expected, arguments[arguments.ToList().IndexOf("--volname") + 1]);
    }

    [Fact]
    public void RenamingADriveDoesNotChangeItsRemoteOrCacheIdentity()
    {
        var connection = WebDav();
        connection.Name = "Before";
        var before = RcloneArguments.BuildMount(connection, Settings()).ToList();

        connection.Name = "After";
        var after = RcloneArguments.BuildMount(connection, Settings());

        before[before.IndexOf("--volname") + 1] = "After";
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("--config other.conf")]
    [InlineData("--rc")]
    [InlineData("--read-only=false")]
    [InlineData("--cache-dir C:\\other")]
    [InlineData("--sftp-pass secret")]
    [InlineData("--log-file C:\\other.log")]
    [InlineData("--dump auth")]
    [InlineData("--volname other")]
    [InlineData("remote:other")]
    public void AdvancedArgumentsCannotReplaceManagedConfigurationOrExposeCredentials(string input)
    {
        Assert.Throws<InvalidOperationException>(() => RcloneArguments.ParseAdvanced(input));
    }

    [Fact]
    public void AdvancedArgumentsRetainSupportedValuesAndRejectMissingValues()
    {
        Assert.Equal(new[] { "--buffer-size", "32M", "--vfs-write-back=5s", "--no-modtime=true" },
            RcloneArguments.ParseAdvanced("--buffer-size 32M --vfs-write-back=5s --no-modtime=true"));
        Assert.Throws<InvalidOperationException>(() => RcloneArguments.ParseAdvanced("--buffer-size --no-modtime"));
        Assert.Throws<InvalidOperationException>(() => RcloneArguments.ParseAdvanced("--no-modtime=maybe"));
        Assert.Throws<ArgumentException>(() => RcloneArguments.ParseAdvanced("--buffer-size \"32M"));
    }

    [Theory]
    [InlineData("https://user:secret@example.test/dav")]
    [InlineData("https://example.test/dav#fragment")]
    [InlineData("file:///C:/data")]
    public void WebDavRejectsUnsupportedUrlsAndEmbeddedCredentials(string host)
    {
        var connection = WebDav();
        connection.Host = host;
        Assert.Throws<InvalidOperationException>(() => ConnectionValidator.Validate(connection));
    }

    [Theory]
    [InlineData("sftp.example.test:2222")]
    [InlineData("sftp://sftp.example.test")]
    [InlineData("user@sftp.example.test")]
    public void SftpHostMustNotContainPortsUrlsOrCredentials(string host)
    {
        var connection = Sftp();
        connection.Host = host;
        Assert.Throws<InvalidOperationException>(() => ConnectionValidator.Validate(connection));
    }

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("127.0.0.1")]
    [InlineData("sftp.example.test")]
    public void SftpAcceptsHostnamesAndIpAddresses(string host)
    {
        var connection = Sftp();
        connection.Host = host;
        ConnectionValidator.Validate(connection);
    }

    [Fact]
    public async Task CredentialPreparationFailurePropagatesWithoutPlaintextFallback()
    {
        var connection = WebDav();
        var writer = new RcloneConfigWriter((_, _, _) => throw new InvalidOperationException("Injected obscure failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CreateAsync(connection, "rclone.exe"));

        Assert.Empty(Directory.GetFiles(_directory, "*.conf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelledConfigurationDoesNotPrepareCredentials()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer().CreateAsync(WebDav(), "rclone.exe", cancellation.Token));

        Assert.Empty(_obscuredInputs);
    }

    [Fact]
    public void RemoteNameRetainsLegacyGuidIdentityButSafelyMapsPunctuation()
    {
        var connection = WebDav();
        var guid = connection.Id;
        Assert.Equal("mountly_" + guid, RcloneConfigWriter.GetRemoteName(connection));
        connection.Id = "existing-connection";
        var mapped = RcloneConfigWriter.GetRemoteName(connection);
        Assert.Equal(mapped, RcloneConfigWriter.GetRemoteName(connection));
        Assert.Matches("^mountly_id_[a-f0-9]{64}$", mapped);
    }

    private RcloneConfigWriter Writer() => new((_, value, _) =>
    {
        _obscuredInputs.Add(value);
        return Task.FromResult("obscured-for-test");
    });

    private AppSettings Settings() => new() { CacheRoot = Path.Combine(_directory, "cache") };
    private static string Prefix(MountConnection connection) => "RCLONE_CONFIG_" + RcloneConfigWriter.GetRemoteName(connection).ToUpperInvariant() + "_";
    private static MountConnection WebDav() => new() { Host = "https://storage.example.test/dav", Secret = "private-password" };
    private static MountConnection S3() => new()
    {
        Protocol = MountProtocol.S3, Host = "bucket-name", S3AccessKeyId = "test-access-key", Secret = "test-secret-key"
    };
    private MountConnection Sftp()
    {
        var hosts = Path.Combine(_directory, "known_hosts");
        File.WriteAllText(hosts, "Test fixture; no server connection is made.");
        return new MountConnection
        {
            Protocol = MountProtocol.Sftp, Host = "sftp.example.test", SftpKnownHostsFile = hosts, Secret = "test-password"
        };
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Mountloom.ProtocolTests"));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_directory)), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the isolated test directory.");
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

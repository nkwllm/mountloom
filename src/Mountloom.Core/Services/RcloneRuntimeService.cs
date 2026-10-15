using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Mountloom.Core.Services;

public sealed record RcloneRuntimePackage(string Version, Uri DownloadUri, string ArchiveSha256,
    string ExecutableSha256, string ArchiveDirectory);

/// <summary>Resolves only the application's pinned runtime; never uses user settings or PATH.</summary>
public sealed class RcloneRuntimeService
{
    public static RcloneRuntimePackage DefaultPackage { get; } = new(
        "1.75.2",
        new Uri("https://downloads.rclone.org/v1.75.2/rclone-v1.75.2-windows-amd64.zip"),
        "8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818",
        "41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4",
        "rclone-v1.75.2-windows-amd64");

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _applicationDirectory;
    private readonly string _runtimeDirectory;
    private readonly HttpClient _httpClient;
    private readonly RcloneRuntimePackage _package;
    private readonly Func<string, CancellationToken, Task<bool>> _executableProbe;
    private readonly TimeSpan _downloadTimeout;

    public event Action<string>? ProgressChanged;

    public RcloneRuntimeService() : this(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mountly", "Runtime", "rclone")) { } // Retain the original per-user runtime location.

    // Directory, transport and probe injection keeps tests away from AppData and real downloads.
    public RcloneRuntimeService(string applicationDirectory, string runtimeDirectory,
        HttpClient? httpClient = null, RcloneRuntimePackage? package = null,
        Func<string, CancellationToken, Task<bool>>? executableProbe = null,
        TimeSpan? downloadTimeout = null)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory);
        _runtimeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory));
        _httpClient = httpClient ?? SharedClient;
        _package = package ?? DefaultPackage;
        if (!Version.TryParse(_package.Version, out _) || !SafeComponent(_package.ArchiveDirectory)
            || _package.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !IsHash(_package.ArchiveSha256) || !IsHash(_package.ExecutableSha256))
            throw new ArgumentException("The managed rclone package definition is invalid.", nameof(package));
        _executableProbe = executableProbe ?? ProbeVersionAsync;
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(3);
        if (_downloadTimeout <= TimeSpan.Zero || _downloadTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }

    public async Task<string> EnsureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bundled = Path.Combine(_applicationDirectory, "rclone.exe");
        if (await IsUsableAsync(bundled, cancellationToken))
        {
            ProgressChanged?.Invoke($"rclone {_package.Version} 已就绪（应用内置）。");
            return bundled;
        }

        var gate = InstallGates.GetOrAdd(_runtimeDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            AssertNoReparsePoints(_runtimeDirectory);
            Directory.CreateDirectory(_runtimeDirectory);
            // Share.None also serializes installations from separate application processes.
            await using var fileLock = await AcquireInstallLockAsync(cancellationToken);
            var destination = Path.Combine(_runtimeDirectory, _package.Version);
            AssertNoReparsePoints(destination);
            var installed = Path.Combine(destination, "rclone.exe");
            if (await IsUsableAsync(installed, cancellationToken))
            {
                ProgressChanged?.Invoke($"rclone {_package.Version} 已就绪。");
                return installed;
            }

            ProgressChanged?.Invoke($"正在自动准备 rclone {_package.Version}…");
            await InstallAsync(destination, cancellationToken);
            ProgressChanged?.Invoke($"rclone {_package.Version} 已就绪。");
            return installed;
        }
        finally { gate.Release(); }
    }

    private async Task InstallAsync(string destination, CancellationToken cancellationToken)
    {
        var stage = Path.Combine(_runtimeDirectory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var archivePath = Path.Combine(stage, "download.zip");
            await DownloadAsync(archivePath, cancellationToken);

            if (!await HashMatchesAsync(archivePath, _package.ArchiveSha256, cancellationToken))
                throw new InvalidDataException("rclone 下载校验失败，请重试；未经验证的文件不会运行。");
            ProgressChanged?.Invoke("下载校验通过，正在安装 rclone…");

            var stagedExecutable = Path.Combine(stage, "rclone.exe");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Extract only known names; archive paths can never choose a filesystem destination.
                await ExtractAsync(archive, "rclone.exe", stagedExecutable, 256L * 1024 * 1024, true, cancellationToken);
                foreach (var name in new[] { "README.txt", "git-log.txt", "LICENSE.txt", "COPYING" })
                    await ExtractAsync(archive, name, Path.Combine(stage, name), 8L * 1024 * 1024, false, cancellationToken);
            }
            if (!await IsUsableAsync(stagedExecutable, cancellationToken))
                throw new InvalidDataException("下载的 rclone 未通过版本或可执行文件校验，请重试。");

            cancellationToken.ThrowIfCancellationRequested();
            AssertNoReparsePoints(destination);
            Directory.CreateDirectory(destination);
            foreach (var name in new[] { "README.txt", "git-log.txt", "LICENSE.txt", "COPYING" })
            {
                var source = Path.Combine(stage, name);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(destination, name);
                AssertNoReparsePoints(target);
                File.Move(source, target, overwrite: true);
            }
            var executable = Path.Combine(destination, "rclone.exe");
            AssertNoReparsePoints(executable);
            // The executable is committed last with an atomic same-volume rename. A failed or
            // cancelled download leaves no executable that a later Ensure call could accept.
            File.Move(stagedExecutable, executable, overwrite: true);
        }
        finally
        {
            try
            {
                AssertNoReparsePoints(stage);
                Directory.Delete(stage, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(string archivePath, CancellationToken cancellationToken)
    {
        // With ResponseHeadersRead, HttpClient.Timeout only covers receipt of the headers.
        // Bound the body as well so a stalled download cannot hold the installation lock forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(_package.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 100L * 1024 * 1024)
                throw new InvalidDataException("The rclone download exceeds the expected size.");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            await WriteBoundedAsync(source, archivePath, 100L * 1024 * 1024, deadline.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("rclone 下载超时，请检查网络后重试。", error);
        }
    }

    private async Task ExtractAsync(ZipArchive archive, string name, string destination, long limit,
        bool required, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(_package.ArchiveDirectory + "/" + name);
        if (entry is null)
        {
            if (required) throw new InvalidDataException("The rclone archive does not contain the expected executable.");
            return;
        }
        if (entry.Length > limit) throw new InvalidDataException("The rclone archive contains an oversized file.");
        await using var source = entry.Open();
        await WriteBoundedAsync(source, destination, limit, cancellationToken);
    }

    private static async Task WriteBoundedAsync(Stream source, string path, long limit, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The rclone download exceeds the expected size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private async Task<bool> IsUsableAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        try
        {
            AssertNoReparsePoints(path);
            if (!await HashMatchesAsync(path, _package.ExecutableSha256, cancellationToken)) return false;
            return await _executableProbe(path, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        { return false; }
    }

    private async Task<bool> ProbeVersionAsync(string path, CancellationToken cancellationToken)
    {
        var output = await ProcessRunner.RunCaptureAsync(path, ["version"], cancellationToken,
            environment: new Dictionary<string, string>(), timeout: TimeSpan.FromSeconds(8));
        var firstLine = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return output.ExitCode == 0 && firstLine == "rclone v" + _package.Version;
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FileStream> AcquireInstallLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_runtimeDirectory, ".install.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertNoReparsePoints(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, cancellationToken); }
        }
    }

    private static void AssertNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The managed rclone location cannot contain a symbolic link or junction.");
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool SafeComponent(string value) => value.Length > 0 && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

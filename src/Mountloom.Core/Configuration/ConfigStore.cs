using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mountloom.Core.Models;
using Mountloom.Core.Security;

namespace Mountloom.Core.Configuration;

public sealed class ConfigStore
{
    public const int CurrentSchemaVersion = 1;
    private static readonly string[] CredentialFields = ["Secret", "S3AccessKeyId", "SftpKeyPassphrase"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _fileLock;
    private readonly ISecretProtector _secretProtector;
    private bool _writesBlocked;
    private bool _hasUsableConfig;

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string BackupPath => ConfigPath + ".bak";
    public string? LastLoadWarning { get; private set; }
    public bool CanSave => !_writesBlocked;

    // The original data location is a compatibility contract, independent of the product name.
    public ConfigStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mountly"))
    {
    }

    public ConfigStore(string configDirectory, ISecretProtector? secretProtector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ConfigDirectory = Path.GetFullPath(configDirectory);
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        _fileLock = FileLocks.GetOrAdd(ConfigPath, _ => new SemaphoreSlim(1, 1));
        _secretProtector = secretProtector ?? new DpapiSecretProtector();
    }

    public async Task<MountloomConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            LastLoadWarning = null;
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(ConfigPath) && !File.Exists(BackupPath))
            {
                var empty = new MountloomConfig();
                await CommitAsync(CreateDocument(empty), keepPreviousBackup: false, cancellationToken);
                _writesBlocked = false;
                _hasUsableConfig = true;
                return empty;
            }

            LoadedConfig loaded;
            try
            {
                loaded = await ReadAsync(ConfigPath, cancellationToken);
            }
            catch (UnsupportedConfigVersionException)
            {
                // An older application must never replace a newer schema with an old backup.
                throw;
            }
            catch (Exception primaryError) when (IsRecoverableLoadError(primaryError))
            {
                try
                {
                    loaded = await ReadAsync(BackupPath, cancellationToken);
                }
                catch (Exception backupError) when (IsRecoverableLoadError(backupError))
                {
                    throw new InvalidDataException(
                        "Configuration and backup could not be loaded. Saving is disabled to preserve both files.",
                        new AggregateException(primaryError, backupError));
                }

                await PreserveDamagedConfigAsync(cancellationToken);
                var recoveredDocument = CreateDocument(loaded.Config);
                await ReplaceFileAsync(BackupPath, recoveredDocument, null, cancellationToken);
                await CommitAsync(recoveredDocument, keepPreviousBackup: false, cancellationToken);
                LastLoadWarning = "Configuration was recovered from config.json.bak. Any damaged primary file was preserved as a DPAPI-protected recovery file.";
                if (loaded.Warning is not null) AppendWarning(loaded.Warning);
                _writesBlocked = false;
                _hasUsableConfig = true;
                CleanupLegacyRcloneConfigs();
                return loaded.Config;
            }

            if (loaded.IsLegacy)
            {
                // Preserve an existing prior version while encrypting it. An interrupted
                // migration must leave both the primary and its last good backup recoverable.
                var migratedDocument = CreateDocument(loaded.Config);
                await ProtectLegacyBackupAsync(cancellationToken);
                if (!File.Exists(BackupPath))
                    await ReplaceFileAsync(BackupPath, migratedDocument, null, cancellationToken);
                await CommitAsync(migratedDocument, keepPreviousBackup: false, cancellationToken);
                AppendWarning("Existing configuration credentials were migrated to Windows DPAPI protection.");
                if (loaded.Warning is not null) AppendWarning(loaded.Warning);
            }
            else
            {
                await ProtectLegacyBackupAsync(cancellationToken);
            }

            _writesBlocked = false;
            _hasUsableConfig = true;
            CleanupLegacyRcloneConfigs();
            return loaded.Config;
        }
        catch
        {
            _writesBlocked = true;
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(MountloomConfig config, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (_writesBlocked)
            {
                throw new InvalidOperationException("Saving is disabled because configuration loading failed. Recover the existing configuration before saving.");
            }
            if (!_hasUsableConfig && (File.Exists(ConfigPath) || File.Exists(BackupPath)))
                throw new InvalidOperationException("Load the existing configuration successfully before saving changes.");
            // Validate and encrypt before touching the last usable on-disk configuration.
            var document = CreateDocument(config);
            Directory.CreateDirectory(ConfigDirectory);
            await CommitAsync(document, keepPreviousBackup: true, cancellationToken);
            _hasUsableConfig = true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<LoadedConfig> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Configuration root must be an object.");
        // Check the version before interpreting required fields so a future schema cannot
        // accidentally trigger a fallback to an older backup.
        var hasVersion = TryGetProperty(root, "SchemaVersion", out var versionElement);
        if (hasVersion)
        {
            if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
                throw new InvalidDataException("Configuration SchemaVersion must be an integer.");
            if (version != CurrentSchemaVersion)
                throw new UnsupportedConfigVersionException($"Configuration schema {version} is not supported by this version of Mountloom. Existing files have been preserved.");
        }
        if (!TryGetProperty(root, "App", out var app) || app.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, "Connections", out var connections) || connections.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Configuration must contain an App object and a Connections array.");

        if (!hasVersion)
        {
            foreach (var connection in connections.EnumerateArray()) ValidateStoredIdentity(connection);
            var legacy = JsonSerializer.Deserialize<MountloomConfig>(json, SerializerOptions)
                ?? throw new InvalidDataException("Legacy configuration is empty.");
            Validate(legacy);
            return new LoadedConfig(legacy, true, MigrateLegacyS3Paths(legacy));
        }

        var document = JsonSerializer.Deserialize<StoredConfig>(json, SerializerOptions)
            ?? throw new InvalidDataException("Configuration is empty.");
        if (document.App is null || document.Connections is null)
            throw new InvalidDataException("Configuration contains null settings or connections.");

        var result = new MountloomConfig { App = document.App, Connections = [] };
        foreach (var storedConnection in document.Connections)
        {
            if (storedConnection?.Settings is null || string.IsNullOrWhiteSpace(storedConnection.ProtectedCredentials))
                throw new InvalidDataException("A connection is missing settings or protected credentials.");
            var settings = (JsonObject)storedConnection.Settings.DeepClone();
            using (var identity = JsonDocument.Parse(settings.ToJsonString()))
                ValidateStoredIdentity(identity.RootElement);
            var decrypted = _secretProtector.Unprotect(Convert.FromBase64String(storedConnection.ProtectedCredentials));
            try
            {
                var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted)
                    ?? throw new InvalidDataException("Protected credentials are invalid.");
                foreach (var field in CredentialFields)
                {
                    if (!credentials.TryGetValue(field, out var value) || value is null)
                        throw new InvalidDataException("Protected credentials are incomplete.");
                    settings[field] = value;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
            result.Connections.Add(settings.Deserialize<MountConnection>(SerializerOptions)
                ?? throw new InvalidDataException("A connection could not be read."));
        }
        Validate(result);
        return new LoadedConfig(result, false);
    }

    private StoredConfig CreateDocument(MountloomConfig config)
    {
        Validate(config);
        var document = new StoredConfig { SchemaVersion = CurrentSchemaVersion, App = config.App.Clone(), Connections = [] };
        foreach (var connection in config.Connections)
        {
            // Keep the on-disk DTO separate from the in-memory model while retaining new
            // non-secret protocol settings without a second, easily stale field mapping.
            var settings = JsonSerializer.SerializeToNode(connection, SerializerOptions)!.AsObject();
            var credentials = new Dictionary<string, string>();
            foreach (var field in CredentialFields)
            {
                credentials[field] = settings[field]?.GetValue<string>() ?? string.Empty;
                settings.Remove(field);
            }
            // Older UI versions mirrored the S3 access key into Username as well.
            if (connection.Protocol == MountProtocol.S3)
                settings[nameof(MountConnection.Username)] = string.Empty;
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
            try
            {
                document.Connections!.Add(new StoredConnection
                {
                    Settings = settings,
                    ProtectedCredentials = Convert.ToBase64String(_secretProtector.Protect(plaintext))
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        return document;
    }

    private static void Validate(MountloomConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.App is null || config.Connections is null)
            throw new InvalidDataException("Configuration settings and connection collection must not be null.");
        ValidateStrings(config.App);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in config.Connections)
        {
            if (connection is null)
                throw new InvalidDataException("Connection collection must not contain null entries.");
            ValidateStrings(connection);
            if (string.IsNullOrWhiteSpace(connection.Id) || connection.Id.Length > 64 ||
                connection.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                !ids.Add(connection.Id))
                throw new InvalidDataException("Connection IDs must be unique and contain only ASCII letters, digits, hyphens or underscores.");
            // Validate structure only; incomplete drafts remain saveable.
            foreach (var property in typeof(MountConnection).GetProperties().Where(property => property.PropertyType.IsEnum))
            {
                if (!Enum.IsDefined(property.PropertyType, property.GetValue(connection)!))
                    throw new InvalidDataException($"Connection contains an invalid {property.Name} value.");
            }
        }
    }

    private static void ValidateStrings(object value)
    {
        foreach (var property in value.GetType().GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            if (property.GetValue(value) is null)
                throw new InvalidDataException($"{property.Name} must not be null.");
        }
    }

    private static string? MigrateLegacyS3Paths(MountloomConfig config)
    {
        var requiresReview = false;
        foreach (var connection in config.Connections.Where(connection => connection.Protocol == MountProtocol.S3))
        {
            // The original implementation ignored Host, so RemotePath is the source of truth
            // for existing working mounts and their pending VFS uploads.
            var oldTarget = connection.RemotePath.Trim().Trim('/');
            if (!string.IsNullOrEmpty(oldTarget))
            {
                var separator = oldTarget.IndexOf('/');
                connection.Host = separator < 0 ? oldTarget : oldTarget[..separator];
                connection.RemotePath = separator < 0 ? string.Empty : oldTarget[(separator + 1)..];
            }
            else if (!string.IsNullOrWhiteSpace(connection.Host))
            {
                connection.AutoMount = false;
                requiresReview = true;
            }
        }
        return requiresReview
            ? "Review the migrated S3 bucket settings before mounting: older versions ignored the Bucket field. Automatic mounting was disabled for these ambiguous connections."
            : null;
    }

    private static void ValidateStoredIdentity(JsonElement connection)
    {
        if (connection.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(connection, "Id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("Every saved connection must have an explicit ID.");
    }

    private Task CommitAsync(StoredConfig document, bool keepPreviousBackup, CancellationToken cancellationToken)
        => ReplaceFileAsync(ConfigPath, document, keepPreviousBackup ? BackupPath : null, cancellationToken);

    private static async Task ReplaceFileAsync(string targetPath, StoredConfig document, string? backupPath, CancellationToken cancellationToken)
    {
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(targetPath))
                File.Replace(tempPath, targetPath, backupPath);
            else
                File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private async Task PreserveDamagedConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath)) return;
        var plaintext = await File.ReadAllBytesAsync(ConfigPath, cancellationToken);
        try
        {
            var protectedBytes = _secretProtector.Protect(plaintext);
            var path = Path.Combine(ConfigDirectory, $"config.corrupt.{Guid.NewGuid():N}.protected");
            await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task ProtectLegacyBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BackupPath)) return;
        try
        {
            var backup = await ReadAsync(BackupPath, cancellationToken);
            if (backup.IsLegacy)
                await ReplaceFileAsync(BackupPath, CreateDocument(backup.Config), null, cancellationToken);
        }
        catch (Exception error) when (IsRecoverableLoadError(error))
        {
            // Preserve damaged backup bytes under DPAPI rather than retaining plaintext.
            var bytes = await File.ReadAllBytesAsync(BackupPath, cancellationToken);
            try
            {
                var protectedPath = Path.Combine(ConfigDirectory, $"config.backup-corrupt.{Guid.NewGuid():N}.protected");
                await File.WriteAllBytesAsync(protectedPath, _secretProtector.Protect(bytes), cancellationToken);
                File.Delete(BackupPath);
                AppendWarning("The unusable backup was preserved as a DPAPI-protected recovery file.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private void CleanupLegacyRcloneConfigs()
    {
        var directory = Path.Combine(ConfigDirectory, "rclone");
        if (!Directory.Exists(directory)) return;
        try
        {
            // Only old GUID-named generated files are eligible; never touch caches,
            // arbitrary .conf files, or redirected directories/files.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                AppendWarning("Legacy rclone credential cleanup was skipped because its directory is redirected.");
                return;
            }
            foreach (var path in Directory.EnumerateFiles(directory, "*.conf", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(id, "N", out _) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.ReadLines(path).FirstOrDefault() != $"[mountly_{id}]") continue;
                // Old randomized obscure values contribute to rclone's VFS backend identity.
                // Preserve their exact original bytes before removing plaintext, so pending
                // uploads remain recoverable without keeping readable credential files.
                var recoveryDirectory = Path.Combine(directory, "recovery");
                if (Directory.Exists(recoveryDirectory) &&
                    (File.GetAttributes(recoveryDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    AppendWarning("Legacy rclone credential cleanup was skipped because its recovery directory is redirected.");
                    return;
                }
                Directory.CreateDirectory(recoveryDirectory);
                var originalBytes = File.ReadAllBytes(path);
                try
                {
                    var protectedBytes = _secretProtector.Protect(originalBytes);
                    var recoveryPath = Path.Combine(recoveryDirectory, $"{id}.{Guid.NewGuid():N}.conf.dpapi");
                    using (var stream = new FileStream(recoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(protectedBytes);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Delete(path);
                }
                finally { CryptographicOperations.ZeroMemory(originalBytes); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            AppendWarning("Some legacy rclone files still contain credentials and could not be removed. Close old Mountloom instances and retry.");
        }
    }

    private void AppendWarning(string warning)
        => LastLoadWarning = string.IsNullOrWhiteSpace(LastLoadWarning) ? warning : LastLoadWarning + " " + warning;

    private static bool IsRecoverableLoadError(Exception error)
        => error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or InvalidDataException;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record LoadedConfig(MountloomConfig Config, bool IsLegacy, string? Warning = null);
    private sealed class StoredConfig
    {
        public int SchemaVersion { get; set; }
        public AppSettings? App { get; set; }
        public List<StoredConnection>? Connections { get; set; }
    }
    private sealed class StoredConnection
    {
        public JsonObject? Settings { get; set; }
        public string? ProtectedCredentials { get; set; }
    }
}

public sealed class UnsupportedConfigVersionException(string message) : NotSupportedException(message);

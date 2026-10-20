using Microsoft.Win32;
using Mountloom.Core.Services.Runtime;

namespace Mountloom.Tests;

public sealed class DriveIconTests : IDisposable
{
    private readonly string _icon = Path.Combine(Path.GetTempPath(), "Mountloom icon " + Guid.NewGuid().ToString("N") + ".ico");
    private readonly FakeRegistry _registry = new();
    private readonly List<string> _refreshes = [];
    private readonly HashSet<string> _presentDrives = new(StringComparer.OrdinalIgnoreCase);

    public DriveIconTests() => File.WriteAllBytes(_icon, [0]);

    private WindowsMountDriveIconService CreateService(string? icon = null) =>
        new(icon ?? _icon, _registry, _refreshes.Add, _presentDrives.Contains);

    [Fact]
    public void LeaseUsesQuotedSharedIconAndRemovesOnlyItsNewKeys()
    {
        _registry.WriteDefault(@"Y\DefaultIcon", new("other.ico", RegistryValueKind.String));
        var lease = CreateService().Apply("x:");
        Assert.Equal(new($"\"{_icon}\",0", RegistryValueKind.String), _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Equal(["X:"], _refreshes);

        lease!.Dispose();
        lease.Dispose();

        Assert.False(_registry.KeyExists("X"));
        Assert.Equal("other.ico", _registry.ReadDefault(@"Y\DefaultIcon")!.Data);
        Assert.Equal(["X:", "X:"], _refreshes);
        Assert.Empty(_registry.Journal);
    }

    [Fact]
    public void RestoresOriginalValueAndTypeWithoutExpandingEnvironmentVariables()
    {
        var original = new DriveIconRegistryValue(@"%USERPROFILE%\old.ico", RegistryValueKind.ExpandString);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        _registry.WriteNamed(@"X\DefaultLabel", "", "My files");
        _registry.WriteNamed(@"X\DefaultIcon", "unrelated", "keep");

        CreateService().Apply("X:")!.Dispose();

        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Equal("keep", _registry.Values[@"X\DefaultIcon"]["unrelated"].Data);
        Assert.Equal("My files", _registry.ReadDefault(@"X\DefaultLabel")!.Data);
    }

    [Fact]
    public void ExistingEmptyKeysAndNewUnrelatedValuesRemainIntact()
    {
        _registry.EnsureKey(@"X\DefaultIcon");
        var lease = CreateService().Apply("X:");
        _registry.WriteNamed(@"X\DefaultIcon", "added-later", "keep");

        lease!.Dispose();

        Assert.True(_registry.KeyExists(@"X\DefaultIcon"));
        Assert.Null(_registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Equal("keep", _registry.Values[@"X\DefaultIcon"]["added-later"].Data);
    }

    [Fact]
    public void UserReplacementDuringMountIsPreserved()
    {
        var lease = CreateService().Apply("X:");
        var replacement = new DriveIconRegistryValue("chosen-later.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"X\DefaultIcon", replacement);

        lease!.Dispose();

        Assert.Equal(replacement, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Single(_refreshes);
    }

    [Fact]
    public void MissingIconAndInvalidLetterCannotWriteOverrides()
    {
        File.Delete(_icon);
        Assert.Null(CreateService().Apply("X:"));
        Assert.Throws<ArgumentException>(() => CreateService().Apply(@"C:\remote"));
        Assert.Empty(_registry.Values);
        Assert.Empty(_refreshes);
    }

    [Fact]
    public void RefreshFailureRestoresThePreviousIconBeforeThrowing()
    {
        var original = new DriveIconRegistryValue("previous.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        var service = new WindowsMountDriveIconService(_icon, _registry,
            _ => throw new InvalidOperationException("Shell unavailable"), _presentDrives.Contains);

        Assert.Throws<InvalidOperationException>(() => service.Apply("X:"));

        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Empty(_registry.Journal);
    }

    [Fact]
    public void RestartRecoversOriginalIconEvenAfterOldReleaseFileWasDeleted()
    {
        var original = new DriveIconRegistryValue(@"%USERPROFILE%\chosen.ico", RegistryValueKind.ExpandString);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        _registry.WriteNamed(@"X\DefaultLabel", "", "Keep my label");
        CreateService().Apply("X:"); // Simulate process termination without disposing the lease.
        File.Delete(_icon);

        CreateService().RecoverStaleIcons();

        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Equal("Keep my label", _registry.ReadDefault(@"X\DefaultLabel")!.Data);
        Assert.Empty(_registry.Journal);
    }

    [Fact]
    public void RecoverySkipsPresentDrivesAndPreservesExternalReplacement()
    {
        CreateService().Apply("X:");
        CreateService().Apply("Y:");
        _presentDrives.Add("X:");
        var external = new DriveIconRegistryValue("user-chosen.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"Y\DefaultIcon", external);

        CreateService().RecoverStaleIcons();

        Assert.Equal($"\"{_icon}\",0", _registry.ReadDefault(@"X\DefaultIcon")!.Data);
        Assert.NotNull(_registry.ReadOwnership("X"));
        Assert.Null(_registry.ReadOwnership("Y"));
        Assert.Equal(external, _registry.ReadDefault(@"Y\DefaultIcon"));
        _presentDrives.Clear();
        CreateService().RecoverStaleIcons();
        Assert.False(_registry.KeyExists("X"));
        Assert.Empty(_registry.Journal);
    }

    [Fact]
    public void NewReleaseAdoptsTrueOriginalAndOldLeaseCannotUndoNewOwnership()
    {
        var original = new DriveIconRegistryValue("original.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        var oldLease = CreateService().Apply("X:");
        _presentDrives.Add("X:");
        CreateService().RecoverStaleIcons(); // An orphaned drive is still present at startup.
        File.WriteAllBytes(_icon + ".next.ico", [0]);
        var newLease = CreateService(_icon + ".next.ico").Apply("X:");

        oldLease!.Dispose();

        Assert.Equal($"\"{_icon}.next.ico\",0", _registry.ReadDefault(@"X\DefaultIcon")!.Data);
        Assert.Equal(original, _registry.ReadOwnership("X")!.Previous);
        newLease!.Dispose();
        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Empty(_registry.Journal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedReleaseHandoverCanRestoreEitherOwnedAssignment(bool newIconWasWritten)
    {
        var original = new DriveIconRegistryValue("original.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        CreateService().Apply("X:");
        var old = _registry.ReadOwnership("X")!;
        var next = old with
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            Assigned = new("next-release.ico,0", RegistryValueKind.String),
            PriorAssignment = old.Assigned
        };
        _registry.WriteOwnership("X", next);
        if (newIconWasWritten) { _registry.WriteDefault(@"X\DefaultIcon", next.Assigned); }

        CreateService().RecoverStaleIcons();

        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));
        Assert.Empty(_registry.Journal);
    }

    [Fact]
    public void SnapshotIsDurableBeforeOverrideAndJournalFailureLeavesOriginalUntouched()
    {
        var original = new DriveIconRegistryValue("original.ico", RegistryValueKind.String);
        _registry.WriteDefault(@"X\DefaultIcon", original);
        _registry.FailJournalWrites = true;
        Assert.Throws<IOException>(() => CreateService().Apply("X:"));
        Assert.Equal(original, _registry.ReadDefault(@"X\DefaultIcon"));

        _registry.FailJournalWrites = false;
        _registry.BeforeDefaultWrite = (key, value) =>
        {
            var saved = _registry.ReadOwnership("X");
            Assert.NotNull(saved);
            Assert.Equal(original, saved.Previous);
            Assert.Equal(value, saved.Assigned);
        };
        var lease = CreateService().Apply("X:");
        _registry.BeforeDefaultWrite = null;
        lease!.Dispose();
    }

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.None)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    public void JournalPreservesEveryOriginalRegistryValueType(RegistryValueKind kind)
    {
        object original = kind switch
        {
            RegistryValueKind.MultiString => new[] { "one", "two" },
            RegistryValueKind.Binary or RegistryValueKind.None => new byte[] { 0, 127, 255 },
            RegistryValueKind.DWord => -123,
            RegistryValueKind.QWord => long.MaxValue,
            _ => @"%USERPROFILE%\原有图标.ico"
        };
        _registry.WriteDefault(@"X\DefaultIcon", new(original, kind));
        CreateService().Apply("X:");

        CreateService().RecoverStaleIcons();

        var restored = _registry.ReadDefault(@"X\DefaultIcon")!;
        Assert.Equal(kind, restored.Kind);
        Assert.Equal(original.GetType(), restored.Data.GetType());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(restored.Data));
    }

    public void Dispose()
    {
        File.Delete(_icon);
        File.Delete(_icon + ".next.ico");
    }

    private sealed class FakeRegistry : IDriveIconRegistry
    {
        public Dictionary<string, Dictionary<string, DriveIconRegistryValue>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Journal { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailJournalWrites { get; set; }
        public Action<string, DriveIconRegistryValue>? BeforeDefaultWrite { get; set; }
        public void EnsureKey(string key)
        {
            var separator = key.LastIndexOf('\\');
            if (separator >= 0) { EnsureKey(key[..separator]); }
            Values.TryAdd(key, []);
        }
        public bool KeyExists(string key) => Values.ContainsKey(key);
        public DriveIconRegistryValue? ReadDefault(string key) => Values.GetValueOrDefault(key)?.GetValueOrDefault("");
        public void WriteDefault(string key, DriveIconRegistryValue value)
        {
            BeforeDefaultWrite?.Invoke(key, value);
            EnsureKey(key);
            Values[key][""] = value;
        }
        public void WriteNamed(string key, string name, string value)
        {
            EnsureKey(key);
            Values[key][name] = new(value, RegistryValueKind.String);
        }
        public void DeleteDefault(string key) => Values.GetValueOrDefault(key)?.Remove("");
        public void DeleteKeyIfEmpty(string key)
        {
            if (Values.TryGetValue(key, out var values) && values.Count == 0 &&
                !Values.Keys.Any(child => child.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))) { Values.Remove(key); }
        }
        public IReadOnlyList<string> ReadJournalDrives() => Journal.Keys.ToArray();
        public DriveIconOwnership? ReadOwnership(string driveKey) => Journal.TryGetValue(driveKey, out var bytes)
            ? WindowsDriveIconRegistry.DecodeOwnership(bytes) : null;
        public void WriteOwnership(string driveKey, DriveIconOwnership ownership)
        {
            if (FailJournalWrites) { throw new IOException("Journal unavailable"); }
            Journal[driveKey] = WindowsDriveIconRegistry.EncodeOwnership(ownership);
        }
        public void DeleteOwnership(string driveKey) => Journal.Remove(driveKey);
    }
}

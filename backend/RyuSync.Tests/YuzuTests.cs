using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public class YuzuTests
{
    private const string Title = "0100F2C0115B6000";
    private const string User = "0123456789ABCDEF0123456789ABCDEF";
    private static string Save(string nand, string user = User, string title = Title)
    {
        var path = Path.Combine(nand, "user", "save", "0000000000000000", user, title);
        Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, ".yuzu_save_size"), new byte[16]); return path;
    }
    private static (StateDatabase Db, Binding Binding, SyncEngine Engine, Snapshots Snapshots, RevisionRepository Remote) Yuzu(Fixture f, string name, string? text)
    {
        var device = f.Device(name); var root = Save(Path.Combine(f.Root, name + "-nand"));
        if (text is not null) File.WriteAllText(Path.Combine(root, "save.dat"), text);
        var settings = device.Db.Settings with { ProtocolVersion = 2 }; device.Db.Settings = settings;
        var binding = device.Binding with { SaveRoot = root, EmulatorKind = "yuzu", EmulatorBuild = "test-build", UserId = User.ToLowerInvariant(), SaveDataId = "account" };
        return (device.Db, binding, device.Engine, device.Snapshots, new(f.Store, settings, Path.Combine(device.Db.Root, "staging")));
    }
    private static void Trial(StateDatabase db, Binding binding, IEnumerable<Revision> rows) => db.Put("compatibility", binding.Id, new CompatibilityApproval(binding.Id, Compatibility.Signature(binding, rows), "trial"));

    [Fact]
    public void ScanCustomNandFindsAccountSavesWithoutChangingFiles()
    {
        using var f = new Fixture(); var nand = Path.Combine(f.Root, "custom-nand"); var save = Save(nand);
        File.WriteAllText(Path.Combine(save, "progress"), "game"); Save(nand, new string('0', 32));
        var before = Snapshots.Inspect(nand).Hash;
        var installation = new EmulatorInstallation { Id = "deck-yuzu", Kind = "yuzu", NandRoot = nand };
        var found = Assert.Single(Emulators.Scan(installation).Saves);
        Assert.Equal(Title, found.TitleId); Assert.Equal(User.ToLowerInvariant(), found.UserId);
        Assert.Equal(save, found.ContentPath); Assert.Equal("yuzu", found.EmulatorKind); Assert.Equal("deck-yuzu", found.InstallationId);
        Assert.Equal("ready", found.Status); Assert.Equal(before, Snapshots.Inspect(nand).Hash);
        Assert.NotEqual(found.Id, Assert.Single(Emulators.Scan(installation with { Id = "another" }).Saves).Id);
    }
    [Fact]
    public void UnsupportedLayoutIsNotGuessedAndEmptyDirectoriesRequireInitialization()
    {
        using var f = new Fixture(); var nand = Path.Combine(f.Root, "nand"); var root = Save(nand);
        File.Delete(Path.Combine(root, ".yuzu_save_size")); Directory.CreateDirectory(Path.Combine(nand, "user", "save", "account"));
        var result = Emulators.Scan(new() { Id = "test", Kind = "yuzu", NandRoot = nand });
        Assert.Equal("initialization-required", Assert.Single(result.Saves).Status); Assert.NotEmpty(result.Warnings);
    }
    [Fact]
    public void InvalidSizeMetadataIsNotRegistered()
    {
        using var f = new Fixture(); var nand = Path.Combine(f.Root, "nand"); var root = Save(nand);
        File.WriteAllText(Path.Combine(root, ".yuzu_save_size"), "unknown");
        var result = Emulators.Scan(new() { Id = "test", Kind = "yuzu", NandRoot = nand });
        Assert.Empty(result.Saves); Assert.NotEmpty(result.Warnings);
    }
    [Fact]
    public void SymlinkInsideNandIsRejectedButSelectedRootAliasIsResolved()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var nand = Path.Combine(f.Root, "nand"); Save(nand);
        var alias = Path.Combine(f.Root, "nand-alias"); Directory.CreateSymbolicLink(alias, nand);
        Assert.Single(Emulators.Scan(new() { Id = "test", Kind = "yuzu", NandRoot = alias }).Saves);
        var userRoot = Path.Combine(nand, "user", "save", "0000000000000000");
        Directory.CreateSymbolicLink(Path.Combine(userRoot, new string('1', 32)), f.Root);
        Assert.Throws<UserError>(() => Emulators.Scan(new() { Id = "test", Kind = "yuzu", NandRoot = nand }));
    }
    [Fact]
    public async Task YuzuRoundTripExcludesOnlyRootMetadataAndPreservesFullBackup()
    {
        using var f = new Fixture(); var a = Yuzu(f, "pc", "initial"); var b = Yuzu(f, "deck", null);
        var marker = Path.Combine(b.Binding.SaveRoot, ".yuzu_save_size"); var localMetadata = Enumerable.Repeat((byte)7, 16).ToArray(); File.WriteAllBytes(marker, localMetadata);
        Directory.CreateDirectory(Path.Combine(a.Binding.SaveRoot, "game-data"));
        File.WriteAllText(Path.Combine(a.Binding.SaveRoot, "game-data", ".yuzu_save_size"), "a game-owned nested file");
        File.WriteAllText(Path.Combine(a.Binding.SaveRoot, ".hidden-progress"), "hidden game progress");
        Assert.Equal("uploaded", (await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)).Status);
        var revision = Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
        Assert.DoesNotContain(revision.Files, file => file.Path == ".yuzu_save_size");
        Assert.Contains(revision.Files, file => file.Path == "game-data/.yuzu_save_size");
        Assert.Equal("yuzu", revision.SourceEmulator); Assert.Equal(2, revision.SchemaVersion);
        Assert.Equal("downloaded", (await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)).Status);
        Assert.Equal(localMetadata, File.ReadAllBytes(marker)); Assert.Equal("initial", File.ReadAllText(Path.Combine(b.Binding.SaveRoot, "save.dat")));
        Assert.False(Directory.Exists(Path.Combine(b.Binding.SaveRoot, Title)));
        var backup = Assert.Single(b.Db.List<Backup>("backup")); Assert.Equal(localMetadata, File.ReadAllBytes(Path.Combine(backup.Path, ".yuzu_save_size")));
        File.WriteAllBytes(marker, new byte[16]); Assert.Equal("unchanged", (await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)).Status);
        File.WriteAllText(Path.Combine(b.Binding.SaveRoot, "save.dat"), "deck progress");
        await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        Assert.Equal("deck progress", File.ReadAllText(Path.Combine(a.Binding.SaveRoot, "save.dat")));
    }
    [Fact]
    public async Task CrossEmulatorRequiresTrialPreservesBothVersionsAndRoundTrips()
    {
        using var f = new Fixture(); var r = f.Device("ryujinx", "ryu progress"); var y = Yuzu(f, "yuzu", "yuzu progress");
        r.Db.Settings = r.Db.Settings with { ProtocolVersion = 2 };
        var remote = new RevisionRepository(f.Store, r.Db.Settings, Path.Combine(r.Db.Root, "staging"));
        await r.Engine.Sync(r.Binding, r.Db.Settings, remote, null, default);
        var result = await y.Engine.Sync(y.Binding, y.Db.Settings, y.Remote, null, default);
        Assert.Equal("compatibility-required", result.Status); Assert.Empty(y.Db.List<Backup>("backup"));
        Trial(y.Db, y.Binding, await y.Remote.ReadAll(y.Binding.RemoteKey, default));
        var conflict = await y.Engine.Sync(y.Binding, y.Db.Settings, y.Remote, null, default); Assert.Equal("conflict", conflict.Status);
        await y.Engine.Sync(y.Binding, y.Db.Settings, y.Remote, new(conflict.Heads![0], conflict.LocalHash!, conflict.Heads), default);
        Assert.Equal("ryu progress", File.ReadAllText(Path.Combine(y.Binding.SaveRoot, "save.dat")));
        Assert.Equal("yuzu progress", File.ReadAllText(Path.Combine(Assert.Single(y.Db.List<Backup>("backup")).Path, "save.dat")));
        var rows = await remote.ReadAll(r.Binding.RemoteKey, default); Assert.Equal(3, rows.Count);
        Trial(r.Db, r.Binding, rows);
        await r.Engine.Sync(r.Binding, r.Db.Settings, remote, null, default);
        File.WriteAllText(Path.Combine(y.Binding.SaveRoot, "save.dat"), "played on Yuzu");
        await y.Engine.Sync(y.Binding, y.Db.Settings, y.Remote, null, default);
        Assert.Equal("downloaded", (await r.Engine.Sync(r.Binding, r.Db.Settings, remote, null, default)).Status);
        Assert.Equal("played on Yuzu", Fixture.Read(r.Binding));
        Assert.False(File.Exists(Path.Combine(r.Binding.SaveRoot, "0", ".yuzu_save_size")));
    }
    [Fact]
    public void SourceBuildChangeInvalidatesApproval()
    {
        using var f = new Fixture(); var y = Yuzu(f, "yuzu", "progress");
        var rows = new[] { new Revision { SourceEmulator = "ryujinx", SourceBuild = "a", GameVersion = "1.0" } };
        Trial(y.Db, y.Binding, rows); Assert.Equal("trial", Compatibility.State(y.Db, y.Binding, rows));
        Assert.Equal("unconfirmed", Compatibility.State(y.Db, y.Binding, [rows[0] with { SourceBuild = "b" }]));
        Assert.Equal("unconfirmed", Compatibility.State(y.Db, y.Binding with { GameVersion = "2.0" }, rows));
        Assert.Equal("unconfirmed", Compatibility.State(y.Db, y.Binding, [.. rows, new() { SourceEmulator = "yuzu", SourceBuild = "test-build", GameVersion = "new-update" }]));
    }
    [Fact]
    public async Task RestoreIntentSurvivesNewEngineAndDoesNotUploadInitializationProgress()
    {
        using var f = new Fixture(); var a = Yuzu(f, "pc", "server progress"); var b = Yuzu(f, "deck", "fresh initial progress");
        await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        var head = Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
        b.Db.Put("restore", b.Binding.Id, new RestoreIntent(b.Binding.Id, head.Id, b.Remote.Scope));
        var restarted = new SyncEngine(b.Db, new(b.Db)) { IdleCheck = _ => { } };
        Assert.Equal("downloaded", (await restarted.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)).Status);
        Assert.Equal("server progress", File.ReadAllText(Path.Combine(b.Binding.SaveRoot, "save.dat")));
        Assert.Single(await b.Remote.ReadAll(b.Binding.RemoteKey, default)); Assert.Empty(b.Db.List<RestoreIntent>("restore"));
        Assert.Equal("fresh initial progress", File.ReadAllText(Path.Combine(Assert.Single(b.Db.List<Backup>("backup")).Path, "save.dat")));
    }
    [Fact]
    public async Task StaleRestoreDoesNotOverwriteOrUploadInitialSave()
    {
        using var f = new Fixture(); var a = Yuzu(f, "pc", "server"); var b = Yuzu(f, "deck", "initial");
        await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        b.Db.Put("restore", b.Binding.Id, new RestoreIntent(b.Binding.Id, new string('a', 32), b.Remote.Scope));
        await Assert.ThrowsAsync<UserError>(() => b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default));
        Assert.Equal("initial", File.ReadAllText(Path.Combine(b.Binding.SaveRoot, "save.dat"))); Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
    }
    [Fact]
    public void MetadataMutationDuringApplyAbortsBeforeReplacement()
    {
        using var f = new Fixture(); var a = Yuzu(f, "source", "new"); var b = Yuzu(f, "target", "old");
        var snapshot = a.Snapshots.Create(a.Binding.SaveRoot, Emulators.Metadata(a.Binding));
        var count = 0;
        Assert.Throws<UserError>(() => b.Snapshots.Apply(b.Binding, snapshot, Emulators.Inspect(b.Binding).Hash, () =>
        {
            if (++count == 2) File.WriteAllBytes(Path.Combine(b.Binding.SaveRoot, ".yuzu_save_size"), Enumerable.Repeat((byte)8, 16).ToArray());
        }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(b.Binding.SaveRoot, "save.dat"))); Assert.Empty(b.Db.List<ApplyJournal>("journal"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(b.Binding.SaveRoot)!, "*.ryusync-new-*"));
    }
    [Fact]
    public async Task V1AndV2StorageScopesAreIsolated()
    {
        using var f = new Fixture(); var r = f.Device("pc");
        await r.Engine.Sync(r.Binding, r.Db.Settings, r.Remote, null, default);
        var v2 = new RevisionRepository(f.Store, r.Db.Settings with { ProtocolVersion = 2 }, Path.Combine(r.Db.Root, "staging"));
        Assert.Empty(await v2.ReadAll(r.Binding.RemoteKey, default)); Assert.NotEqual(r.Remote.Scope, v2.Scope);
        Assert.Single(await r.Remote.ReadAll(r.Binding.RemoteKey, default));
    }
    [Fact]
    public void ImportUsesSqliteBackupWithoutChangingOriginalSettings()
    {
        using var f = new Fixture(); var source = f.Device("old"); source.Db.Put("binding", source.Binding.Id, source.Binding);
        var original = File.ReadAllBytes(Path.Combine(source.Db.Root, "state.db"));
        var copy = LegacyImport.Read(source.Db.Root, Path.Combine(f.Root, "new-state"));
        Assert.Single(copy.Bindings); Assert.Equal(1, copy.Settings.ProtocolVersion); Assert.True(File.Exists(copy.BackupPath));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(source.Db.Root, "state.db"))); Assert.Equal(1, source.Db.Settings.ProtocolVersion);
    }
    [Fact]
    public void ImportRejectsUnsentWork()
    {
        using var f = new Fixture(); var source = f.Device("old"); source.Db.Put("journal", "unfinished", new ApplyJournal("unfinished", "", "", "", "", ""));
        Assert.Throws<UserError>(() => LegacyImport.Read(source.Db.Root, Path.Combine(f.Root, "new-state")));
    }
    [Fact]
    public void ImportRejectsRunningAgentLock()
    {
        using var f = new Fixture(); var source = f.Device("old");
        using var processLock = new FileStream(Path.Combine(source.Db.Root, "agent.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => LegacyImport.Read(source.Db.Root, Path.Combine(f.Root, "new-state")));
    }
    [Fact]
    public async Task DuplicatePhysicalPathAcrossInstallationsIsRejected()
    {
        using var f = new Fixture(); var device = f.Device("pc"); var nand = Path.Combine(f.Root, "nand"); Save(nand);
        var a = new EmulatorInstallation { Id = "first", Name = "First", Kind = "yuzu", NandRoot = nand };
        var b = a with { Id = "second", Name = "Second" };
        device.Db.Settings = device.Db.Settings with { ProtocolVersion = 2, Installations = [a, b] };
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        var first = Assert.Single(coordinator.Scan(a.Id).Saves); var second = Assert.Single(coordinator.Scan(b.Id).Saves);
        await coordinator.Register(first.Id, "main", a.Id);
        await Assert.ThrowsAsync<UserError>(() => coordinator.Register(second.Id, "different-slot", b.Id));
        Assert.Single(device.Db.List<Binding>("binding"));
    }
    [Fact]
    public void YuzuCannotBeConfiguredForLegacyProtocol()
    {
        var installation = new EmulatorInstallation { Id = "yuzu", Name = "Yuzu", Kind = "yuzu" };
        Assert.Throws<UserError>(() => Coordinator.ValidateSettings(new() { ProtocolVersion = 1, Installations = [installation] }));
    }
    [Fact]
    public void ReservedManagementFileInIncomingPayloadIsRejectedBeforeBackup()
    {
        using var f = new Fixture(); var target = Yuzu(f, "target", "original");
        var tree = new TreeInfo("", [new(".yuzu_save_size", 1, Hashing.Text("x"))], []);
        Assert.Throws<UserError>(() => target.Snapshots.Apply(target.Binding, new("unused.zip", tree, ""), Emulators.Inspect(target.Binding).Hash, () => { }));
        Assert.Empty(target.Db.List<Backup>("backup")); Assert.Equal("original", File.ReadAllText(Path.Combine(target.Binding.SaveRoot, "save.dat")));
    }
}

using RyuSync;
using Xunit;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace RyuSync.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ryusync-tests-" + Guid.NewGuid().ToString("N"));
    public LocalStore Store { get; }
    public Fixture() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "remote")); }
    public (StateDatabase Db, Binding Binding, SyncEngine Engine, Snapshots Snapshots, RevisionRepository Remote) Device(string name, string? text = "initial")
    {
        var local = Path.Combine(Root, name); var saveRoot = Path.Combine(local, "bis", "user", "save", "0000000000000003"); Directory.CreateDirectory(Path.Combine(saveRoot, "0"));
        if (text is not null) File.WriteAllText(Path.Combine(saveRoot, "0", "save.dat"), text);
        File.WriteAllText(Path.Combine(saveRoot, "ExtraData0"), "emulator metadata must remain local");
        var db = new StateDatabase(Path.Combine(Root, name + "-state")); var settings = new AppSettings { DeviceName = name, WebDavUrl = "https://example.invalid/", Workspace = "tests" }; db.Settings = settings;
        var b = new Binding(name, "0100F2C0115B6000", "Test Game", name, "0000000000000003", saveRoot); var snaps = new Snapshots(db); var engine = new SyncEngine(db, snaps) { IdleCheck = _ => { } };
        return (db, b, engine, snaps, new RevisionRepository(Store, settings, Path.Combine(db.Root, "staging")));
    }
    public static void Write(Binding b, string text) => File.WriteAllText(Path.Combine(b.SaveRoot, "0", "save.dat"), text);
    public static string Read(Binding b) => File.ReadAllText(Path.Combine(b.SaveRoot, "0", "save.dat"));
    public void Dispose() { Directory.Delete(Root, true); }
}
public sealed class LocalStore(string root) : IObjectStore
{
    private string P(string p) => Path.Combine(root, p);
    public Task EnsureDirectory(string p, CancellationToken ct = default) { Directory.CreateDirectory(P(p)); return Task.CompletedTask; }
    public Task<string[]> List(string p, CancellationToken ct = default) => Task.FromResult(Directory.Exists(P(p)) ? Directory.EnumerateFileSystemEntries(P(p)).Select(x => Path.GetFileName(x)).ToArray() : []);
    public Task<byte[]> Read(string p, int max, CancellationToken ct = default) { var data = File.ReadAllBytes(P(p)); if (data.Length > max) throw new UserError("too large"); return Task.FromResult(data); }
    public Task Download(string p, string target, long max, CancellationToken ct = default) { if (new FileInfo(P(p)).Length > max) throw new UserError("too large"); File.Copy(P(p), target); return Task.CompletedTask; }
    public Task Upload(string p, string source, CancellationToken ct = default) { File.Copy(source, P(p)); return Task.CompletedTask; }
    public Task Write(string p, byte[] bytes, CancellationToken ct = default) { using var f = new FileStream(P(p), FileMode.CreateNew); f.Write(bytes); return Task.CompletedTask; }
    public Task Delete(string p, CancellationToken ct = default) { File.Delete(P(p)); return Task.CompletedTask; }
    public void Dispose() { }
}
public class SyncTests
{
    [Fact]
    public async Task UploadDownloadAndRoundTripPreserveMetadataAndBackup()
    {
        using var f = new Fixture(); var a = f.Device("pc"); var b = f.Device("deck", null);
        Assert.Equal("uploaded", (await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)).Status);
        Assert.Equal("downloaded", (await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)).Status);
        Assert.Equal("initial", Fixture.Read(b.Binding)); Assert.Single(b.Db.List<Backup>("backup"));
        Assert.Equal("emulator metadata must remain local", File.ReadAllText(Path.Combine(b.Binding.SaveRoot, "ExtraData0")));
        Fixture.Write(b.Binding, "deck progress"); Assert.Equal("uploaded", (await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)).Status);
        Assert.Equal("downloaded", (await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)).Status); Assert.Equal("deck progress", Fixture.Read(a.Binding));
        var backup = Assert.Single(a.Db.List<Backup>("backup")); Assert.Equal("initial", File.ReadAllText(Path.Combine(backup.Path, "0", "save.dat")));
        Assert.Empty(a.Db.List<ApplyJournal>("journal"));
    }
    [Fact]
    public async Task DivergentOfflineProgressIsNotOverwrittenAndRemoteSelectionPreservesBoth()
    {
        using var f = new Fixture(); var a = f.Device("pc"); var b = f.Device("deck");
        await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default); await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default);
        Fixture.Write(a.Binding, "pc progress"); Fixture.Write(b.Binding, "deck progress"); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        var conflict = await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default); Assert.Equal("conflict", conflict.Status); Assert.Equal("deck progress", Fixture.Read(b.Binding));
        var resolved = await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, new(conflict.Heads![0], conflict.LocalHash!, conflict.Heads), default);
        Assert.Equal("downloaded", resolved.Status); Assert.Equal("pc progress", Fixture.Read(b.Binding));
        var rows = await b.Remote.ReadAll(b.Binding.RemoteKey, default); Assert.Single(RevisionRepository.Heads(rows)); Assert.Equal(4, rows.Count);
        Assert.Contains(rows, x => x.ContentHash == Snapshots.Inspect(Path.Combine(Assert.Single(b.Db.List<Backup>("backup")).Path, "0")).Hash);
    }
    [Fact]
    public async Task TwoConcurrentChildrenRemainInHistoryAndCanBeMerged()
    {
        using var f = new Fixture(); var a = f.Device("pc"); var b = f.Device("deck");
        await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default); await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default);
        var parent = (await a.Remote.ReadAll(a.Binding.RemoteKey, default))[0]; Fixture.Write(a.Binding, "A"); Fixture.Write(b.Binding, "B");
        var sa = a.Snapshots.Create(Discovery.ContentPath(a.Binding)); var sb = b.Snapshots.Create(Discovery.ContentPath(b.Binding));
        await a.Remote.Publish(a.Binding, sa, [parent.Id], default); await b.Remote.Publish(b.Binding, sb, [parent.Id], default);
        Assert.Equal(2, RevisionRepository.Heads(await a.Remote.ReadAll(a.Binding.RemoteKey, default)).Length);
        var c = await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default); Assert.Equal("conflict", c.Status);
        await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, new("local", c.LocalHash!, c.Heads!), default);
        var rows = await a.Remote.ReadAll(a.Binding.RemoteKey, default); Assert.Equal(4, rows.Count); Assert.Equal(2, Assert.Single(RevisionRepository.Heads(rows)).Parents.Length);
    }
    [Fact]
    public async Task StaleConflictDecisionIsRejected()
    {
        using var f = new Fixture(); var a = f.Device("pc", "A"); var b = f.Device("deck", "B"); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        var c = await b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default); Fixture.Write(b.Binding, "changed after preview");
        await Assert.ThrowsAsync<UserError>(() => b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, new("local", c.LocalHash!, c.Heads!), default));
        Assert.Equal("changed after preview", Fixture.Read(b.Binding)); Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
    }
    [Fact]
    public async Task MissingRemoteHistoryDoesNotResetBaseline()
    {
        using var f = new Fixture(); var a = f.Device("pc"); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        var head = Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default)); await f.Store.Delete("ryusync/tests/saves/" + a.Binding.RemoteKey + "/commits/" + head.Id + ".json");
        await Assert.ThrowsAsync<UserError>(() => a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)); Assert.Equal("initial", Fixture.Read(a.Binding));
    }
    [Fact]
    public async Task EmptyLocalDeletionIsNotUploaded()
    {
        using var f = new Fixture(); var a = f.Device("pc"); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default); File.Delete(Path.Combine(a.Binding.SaveRoot, "0", "save.dat"));
        Assert.Equal("empty", (await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)).Status); Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
    }
    [Fact]
    public async Task RunningEmulatorPreventsAllSync()
    {
        using var f = new Fixture(); var a = f.Device("pc"); a.Engine.IdleCheck = _ => throw new UserError("playing");
        await Assert.ThrowsAsync<UserError>(() => a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default)); Assert.Empty(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
    }
    [Fact]
    public async Task CorruptArchiveIsNeverApplied()
    {
        using var f = new Fixture(); var a = f.Device("pc"); var b = f.Device("deck", null); await a.Engine.Sync(a.Binding, a.Db.Settings, a.Remote, null, default);
        var head = Assert.Single(await a.Remote.ReadAll(a.Binding.RemoteKey, default));
        var path = Path.Combine(f.Root, "remote", "ryusync", "tests", "saves", a.Binding.RemoteKey, "objects", head.Id + ".zip"); File.WriteAllText(path, "corrupt");
        await Assert.ThrowsAsync<UserError>(() => b.Engine.Sync(b.Binding, b.Db.Settings, b.Remote, null, default)); Assert.Empty(Directory.GetFiles(Path.Combine(b.Binding.SaveRoot, "0")));
    }
    [Fact]
    public void RecoveryRestoresPreviousDirectoryAfterInterruptedMove()
    {
        using var f = new Fixture(); var a = f.Device("pc"); var prev = a.Binding.SaveRoot + ".old"; Directory.Move(a.Binding.SaveRoot, prev);
        a.Db.Put("journal", "test", new ApplyJournal("test", a.Binding.SaveRoot, a.Binding.SaveRoot + ".new", prev, "unused", "unused"));
        Assert.NotEmpty(a.Snapshots.Recover()); Assert.Equal("initial", Fixture.Read(a.Binding)); Assert.Single(a.Db.List<ApplyJournal>("journal"));
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("a\\b")]
    [InlineData("a/CON.txt")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("trailing.")]
    public void UnsafeCrossPlatformPathsAreRejected(string path) => Assert.Throws<UserError>(() => Snapshots.SafeRelative(path));
    [Fact]
    public void ZipTraversalIsRejectedBeforeWritingOutsideStaging()
    {
        using var f = new Fixture(); var zip = Path.Combine(f.Root, "evil.zip"); using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var w = new StreamWriter(z.CreateEntry("../escape").Open()); w.Write("evil"); }
        var tree = new TreeInfo(Snapshots.TreeHash([], []), [], []); Assert.Throws<UserError>(() => Snapshots.Extract(zip, tree, Path.Combine(f.Root, "extract"))); Assert.False(File.Exists(Path.Combine(f.Root, "escape")));
    }
    [Fact]
    public void LinksInsideSaveAreRejected()
    {
        if (OperatingSystem.IsWindows()) return; using var f = new Fixture(); var a = f.Device("pc"); File.CreateSymbolicLink(Path.Combine(a.Binding.SaveRoot, "0", "linked"), Path.Combine(a.Binding.SaveRoot, "ExtraData0"));
        Assert.Throws<UserError>(() => Snapshots.Inspect(Discovery.ContentPath(a.Binding)));
    }
    [Fact]
    public void TreeHashDoesNotDependOnTimestampsOrEntryOrder()
    {
        var files = new List<FileEntry> { new("b", 1, Hashing.Text("b")), new("a", 1, Hashing.Text("a")) }; Assert.Equal(Snapshots.TreeHash(files, []), Snapshots.TreeHash(files.AsEnumerable().Reverse().ToList(), []));
    }
}
public class DiscoveryTests
{
    internal static byte[] Index(ulong title, ulong save, byte type = 1)
    {
        var b = new byte[152]; Encoding.ASCII.GetBytes("IMKV").CopyTo(b, 0); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        Encoding.ASCII.GetBytes("IMEN").CopyTo(b, 12); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 64); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), title); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 42); b[56] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(88), save); b[112] = 1; return b;
    }
    [Fact]
    public void IndexMapsGameAndUserToUnrelatedSaveId()
    {
        using var f = new Fixture(); var root = Path.Combine(f.Root, "ryujinx"); var index = Path.Combine(root, "bis", "system", "save", "8000000000000000", "0"); Directory.CreateDirectory(index);
        File.WriteAllBytes(Path.Combine(index, "imkvdb.arc"), Index(0x0100F2C0115B6000, 123));
        Directory.CreateDirectory(Path.Combine(root, "bis", "user", "save", "000000000000007b", "0"));
        var metadata = Path.Combine(root, "games", "0100f2c0115b6000", "gui"); Directory.CreateDirectory(metadata); File.WriteAllText(Path.Combine(metadata, "metadata.json"), "{\"title\":\"게임 이름\"}");
        var s = Assert.Single(Discovery.Scan(root).Saves); Assert.Equal("0100F2C0115B6000", s.TitleId); Assert.Equal("000000000000007b", s.SaveDataId); Assert.Equal("게임 이름", s.Title); Assert.Equal("0000000000000000000000000000002a", s.UserId); Assert.Equal("ready", s.Status);
    }
    [Theory][InlineData(0)][InlineData(11)][InlineData(151)] public void TruncatedIndexIsRejected(int length) => Assert.Throws<UserError>(() => Discovery.ReadIndex(Index(1, 2)[..length]));
    [Fact] public void UnknownLayoutIsRejected() { var b = Index(1, 2); b[16] = 63; Assert.Throws<UserError>(() => Discovery.ReadIndex(b)); }
    [Fact]
    public void DeviceSaveIsExcluded()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "bis", "system", "save", "8000000000000000", "0"); Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "imkvdb.arc"), Index(1, 2, 3)); Assert.Empty(Discovery.Scan(f.Root).Saves);
    }
    [Fact] public void MissingIndexDoesNotGuessGameFolders() { using var f = new Fixture(); var scan = Discovery.Scan(f.Root); Assert.Empty(scan.Saves); Assert.Single(scan.Warnings); }
}

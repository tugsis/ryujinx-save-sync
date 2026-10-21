using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public class GameTitleTests
{
    private const string TitleId = "0100F2C0115B6000";
    private static EmulatorInstallation Installation(Fixture fixture)
    {
        var root = Path.Combine(fixture.Root, "yuzu");
        var save = Path.Combine(root, "nand", "user", "save", "0000000000000000", "1234567890abcdef1234567890abcdef", TitleId);
        Directory.CreateDirectory(save); File.WriteAllBytes(Path.Combine(save, ".yuzu_save_size"), new byte[16]);
        return new() { Id = "yuzu", Name = "Yuzu", Kind = "yuzu", DataRoot = root };
    }
    private static string Cache(EmulatorInstallation installation)
    {
        var root = Path.Combine(installation.DataRoot, "cache", "game_list"); Directory.CreateDirectory(root);
        return Path.Combine(root, TitleId + ".appname.txt");
    }
    [Fact]
    public void YuzuReadsUtf8NameFromTitleCacheWithoutChangingSave()
    {
        using var fixture = new Fixture(); var installation = Installation(fixture);
        var before = Snapshots.Inspect(Path.Combine(installation.DataRoot, "nand")).Hash;
        File.WriteAllText(Cache(installation), "\uFEFF젤다 테스트 게임\n");
        var save = Assert.Single(Emulators.Scan(installation).Saves);
        Assert.Equal("젤다 테스트 게임", save.Title); Assert.Equal("yuzu-cache", save.TitleSource);
        Assert.Equal(before, Snapshots.Inspect(Path.Combine(installation.DataRoot, "nand")).Hash);
    }
    [Fact]
    public void ExplicitCacheFolderWorksWithOnlyCustomNand()
    {
        using var fixture = new Fixture(); var installation = Installation(fixture);
        var cache = Path.GetDirectoryName(Cache(installation))!; File.WriteAllText(Cache(installation), "Named Game");
        var custom = installation with { DataRoot = "", NandRoot = Path.Combine(installation.DataRoot, "nand"), GameListCacheRoot = cache };
        Assert.Equal("Named Game", Assert.Single(Emulators.Scan(custom).Saves).Title);
    }
    [Fact]
    public void InvalidCacheFallsBackToTitleId()
    {
        using var fixture = new Fixture(); var installation = Installation(fixture); var cache = Cache(installation);
        foreach (var bytes in new[] { new byte[] { 0xFF, 0xFF }, new byte[5000], System.Text.Encoding.UTF8.GetBytes("line1\nline2"), Array.Empty<byte>() })
        {
            File.WriteAllBytes(cache, bytes); var save = Assert.Single(Emulators.Scan(installation).Saves);
            Assert.Equal(TitleId, save.Title); Assert.Equal("title-id", save.TitleSource);
        }
    }
    [Fact]
    public void LinkedCacheFileIsNotRead()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(); var installation = Installation(fixture); var file = Cache(installation);
        var outside = Path.Combine(fixture.Root, "outside-name"); File.WriteAllText(outside, "Do not read"); File.CreateSymbolicLink(file, outside);
        Assert.Equal(TitleId, Assert.Single(Emulators.Scan(installation).Saves).Title);
    }
    [Fact]
    public async Task NameOverrideUpdatesViewsWithoutChangingBindingsAndCanBeReset()
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc"); var installation = Installation(fixture);
        File.WriteAllText(Cache(installation), "Cached Original");
        device.Db.Settings = device.Db.Settings with { ProtocolVersion = 2, Installations = [installation] };
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        var discovered = Assert.Single(coordinator.Scan(installation.Id).Saves);
        var registered = await coordinator.Register(discovered.Id, "main", installation.Id);
        var before = device.Db.Get<Binding>("binding", registered.Id)!;
        await coordinator.SetGameTitle(TitleId.ToLowerInvariant(), "  내가 정한 게임 이름  ");
        var view = Assert.Single(coordinator.Bindings());
        Assert.Equal("내가 정한 게임 이름", view.Title); Assert.Equal("manual", view.TitleSource);
        Assert.Equal(registered.RemoteKey, view.RemoteKey); Assert.Equal(before, device.Db.Get<Binding>("binding", registered.Id));
        Assert.Equal("내가 정한 게임 이름", Assert.Single(coordinator.Scan(installation.Id).Saves).Title);
        await coordinator.SetGameTitle(TitleId, null);
        Assert.Equal("Cached Original", Assert.Single(coordinator.Bindings()).Title);
        File.WriteAllText(Cache(installation), "Updated Cache"); coordinator.Scan(installation.Id);
        Assert.Equal("Updated Cache", Assert.Single(coordinator.Bindings()).Title);
        var nandOnly = installation with { Id = "nand-only", DataRoot = "", NandRoot = Path.Combine(installation.DataRoot, "nand") };
        device.Db.Settings = device.Db.Settings with { Installations = [installation, nandOnly] };
        coordinator.Scan(nandOnly.Id);
        Assert.Equal("Updated Cache", Assert.Single(coordinator.Bindings()).Title);
    }
    [Fact]
    public async Task OverrideBeforeRegistrationDoesNotPreventReset()
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc"); var installation = Installation(fixture);
        device.Db.Settings = device.Db.Settings with { ProtocolVersion = 2, Installations = [installation] };
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        await coordinator.SetGameTitle(TitleId, "Manual Name");
        var discovered = Assert.Single(coordinator.Scan(installation.Id).Saves);
        await coordinator.Register(discovered.Id, "main", installation.Id);
        await coordinator.SetGameTitle(TitleId, null);
        Assert.Equal(TitleId, Assert.Single(coordinator.Bindings()).Title);
    }
    [Fact]
    public void RememberedServerNameIsFallbackAndCacheHasPriority()
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc");
        GameTitles.Remember(device.Db, TitleId, "Server Name", "server");
        Assert.Equal("Server Name", GameTitles.Resolve(device.Db, TitleId, TitleId).Title);
        GameTitles.Remember(device.Db, TitleId, "Local Cache Name", "yuzu-cache");
        GameTitles.Remember(device.Db, TitleId, "Older Server Name", "server");
        Assert.Equal("Local Cache Name", GameTitles.Resolve(device.Db, TitleId, TitleId).Title);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void InvalidManualNamesAreRejected(string title) => Assert.Throws<UserError>(() => GameTitles.ValidateName(title));
    [Fact] public void TitleIdCannotEscapeCacheDirectory() => Assert.Throws<UserError>(() => GameTitles.NormalizeId("../secret"));
}

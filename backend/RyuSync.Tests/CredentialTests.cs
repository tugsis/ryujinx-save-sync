using System.Text;
using System.Text.Json;
using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public sealed class WindowsCredentialFactAttribute : FactAttribute
{
    public WindowsCredentialFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows Credential Manager requires Windows."; }
}

public sealed class FakeCredentialStore : ICredentialStore
{
    public string Provider => "test-store";
    public Dictionary<string, string> Secrets { get; } = [];
    public string State { get; set; } = "available";
    public bool FailAfterWrite { get; set; }
    public bool FailDelete { get; set; }
    public int Reads { get; private set; }
    public Task<string> Probe(CancellationToken ct) => Task.FromResult(State);
    public Task<SecretRead> Read(string key, bool prompt, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(State == "available" ? Secrets.TryGetValue(key, out var value) ? new SecretRead("available", value) : new("missing") : new SecretRead(State));
    }
    public Task Write(string key, string secret, CancellationToken ct)
    {
        Secrets[key] = secret;
        if (FailAfterWrite) throw new CredentialStoreError("error");
        return Task.CompletedTask;
    }
    public Task Delete(string key, bool prompt, CancellationToken ct)
    {
        if (FailDelete || State != "available") throw new CredentialStoreError(State == "available" ? "error" : State);
        Secrets.Remove(key); return Task.CompletedTask;
    }
}
public class CredentialTests
{
    [Fact]
    public async Task PlaintextStorageRestartsWithoutOsServiceAndSupportsLockDeletionAndBinding()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore { State = "unavailable" };
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Initialize();
        await manager.Apply("file-secret", true, id, "file");
        var path = Assert.Single(Directory.GetFiles(Path.Combine(d.Db.Root, "credentials-plaintext"), "*.json"));
        Assert.Contains("file-secret", File.ReadAllText(path));
        Assert.DoesNotContain("file-secret", JsonSerializer.Serialize(d.Db.Get<CredentialDocument>("credentials", "local")));
        Assert.DoesNotContain("file-secret", JsonSerializer.Serialize(manager.Status()));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.Equal("file", restarted.Status().Source); Assert.Equal("file-secret", restarted.Acquire(d.Db.Settings).Password);
        restarted.Lock(); await restarted.Initialize(); Assert.False(restarted.Status().Ready);
        await restarted.Unlock(id); Assert.True(restarted.Status().Ready);
        var original = d.Db.Settings;
        d.Db.Settings = original with { WebDavUser = "different" }; restarted.ChangeConnection(d.Db.Settings);
        await restarted.Initialize(); Assert.False(restarted.Status().Ready);
        Assert.Single(restarted.Retired());
        d.Db.Settings = original; restarted.ChangeConnection(original); await restarted.Unlock(id);
        await restarted.Delete(id); Assert.False(File.Exists(path));
        restarted = new Credentials(d.Db, store); await restarted.Initialize(); Assert.False(restarted.Status().Ready);
    }

    [Fact]
    public async Task SwitchingStorageRetiresPreviousSecretAndFailedOsSaveKeepsFile()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("os-first", true, id);
        await manager.Apply("file-next", true, id, "file"); Assert.Empty(store.Secrets);
        store.FailAfterWrite = true;
        await Assert.ThrowsAsync<UserError>(() => manager.Apply("failed-os", true, id));
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.Equal("file-next", restarted.Acquire(d.Db.Settings).Password);
        store.FailAfterWrite = false;
        await restarted.Apply("os-final", true, id);
        Assert.Empty(Directory.GetFiles(Path.Combine(d.Db.Root, "credentials-plaintext"), "*.json"));
        restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.Equal("os-final", restarted.Acquire(d.Db.Settings).Password);
    }

    [Fact]
    public async Task CorruptPlaintextFileDoesNotBreakStartupAndCanBeReplaced()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore { State = "unavailable" };
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("secret", true, id, "file");
        var path = Assert.Single(Directory.GetFiles(Path.Combine(d.Db.Root, "credentials-plaintext"), "*.json"));
        File.WriteAllText(path, "broken");
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.False(restarted.Status().Ready); Assert.Equal("error", restarted.Status().StoreState);
        await restarted.Apply("replacement", true, id, "file");
        Assert.Equal("replacement", restarted.Acquire(d.Db.Settings).Password);
        Assert.Single(Directory.GetFiles(Path.Combine(d.Db.Root, "credentials-plaintext"), "*.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid\0secret")]
    public async Task CorruptStoredSecretDoesNotPreventStartup(string secret)
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("working", true, id);
        store.Secrets[Assert.Single(store.Secrets.Keys)] = secret;
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.False(restarted.Status().Ready); Assert.True(restarted.Status().Saved);
        Assert.Equal("error", restarted.Status().StoreState);
        await Assert.ThrowsAsync<UserError>(() => restarted.Unlock(id));
        restarted.UseSession("replacement");
        Assert.True(restarted.Status().Ready);
    }

    [WindowsCredentialFact]
    public async Task WindowsNativeStoreRoundTripUsesOnlyItsOwnDisposableTestKey()
    {
        var store = new WindowsCredentialStore(); var key = "RyuSync/Test/" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal("available", await store.Probe(timeout.Token));
            Assert.Equal("missing", (await store.Read(key, false, timeout.Token)).State);
            await store.Write(key, "  UTF-8 암호 test  ", timeout.Token);
            Assert.Equal("  UTF-8 암호 test  ", (await store.Read(key, false, timeout.Token)).Secret);
            await store.Delete(key, false, timeout.Token);
            Assert.Equal("missing", (await store.Read(key, false, timeout.Token)).State);
        }
        finally { await store.Delete(key, false, CancellationToken.None); }
    }
    [Fact]
    public async Task SavedCredentialSurvivesRestartAndNeverAppearsInDatabaseOrStatus()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); await manager.Initialize(); var id = manager.Status().ConnectionId;
        const string secret = "  비밀-password-test-123  ";
        await manager.Apply(secret, true, id);
        Assert.Equal(secret, manager.Acquire(d.Db.Settings).Password);
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.True(restarted.Status().Ready); Assert.True(restarted.Status().Saved);
        Assert.Equal("os-store", restarted.Status().Source); Assert.Equal("unknown", restarted.Status().Verification);
        Assert.Equal(secret, restarted.Acquire(d.Db.Settings).Password);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(restarted.Status()));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(d.Db.Get<CredentialDocument>("credentials", "local")));
        foreach (var path in Directory.GetFiles(d.Db.Root, "state.db*"))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream();
            file.CopyTo(contents);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(contents.ToArray()));
        }
        await restarted.Delete(id); Assert.Empty(store.Secrets); Assert.False(restarted.Status().Ready);
        var deletedRestart = new Credentials(d.Db, store); await deletedRestart.Initialize(); Assert.False(deletedRestart.Status().Saved);
    }
    [Fact]
    public async Task FailedReplacementKeepsWorkingSecretAndCleansPartialWriteAfterRestart()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("working", true, id); store.FailAfterWrite = true;
        await Assert.ThrowsAsync<UserError>(() => manager.Apply("bad replacement", true, id));
        Assert.Equal("working", manager.Acquire(d.Db.Settings).Password);
        Assert.Equal(2, store.Secrets.Count); Assert.True(manager.Status().CleanupPending);
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.Equal("working", restarted.Acquire(d.Db.Settings).Password); Assert.Single(store.Secrets);
        Assert.False(restarted.Status().CleanupPending);
    }
    [Fact]
    public async Task FailedDeletionIsDurableAndIsNeverLoadedAgain()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("secret", true, id); store.FailDelete = true;
        await Assert.ThrowsAsync<UserError>(() => manager.Delete(id));
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.False(restarted.Status().Ready); Assert.True(restarted.Status().CleanupPending); Assert.False(restarted.Status().Saved);
        store.FailDelete = false; await restarted.Delete(id, Assert.Single(restarted.Retired()).Id);
        Assert.Empty(store.Secrets); Assert.False(restarted.Status().CleanupPending);
    }
    [Fact]
    public async Task ExternalOsDeletionClearsMissingReferenceWithoutUsingAnotherCredential()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store);
        await manager.Apply("secret", true, manager.Status().ConnectionId); store.Secrets.Clear();
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.False(restarted.Status().Ready); Assert.False(restarted.Status().Saved);
        Assert.Equal("missing", restarted.Status().StoreState);
    }
    [Fact]
    public async Task SessionLockPreventsAutomaticReloadAndExplicitUnlockRestoresAccess()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store); var id = manager.Status().ConnectionId;
        await manager.Apply("secret", true, id); manager.Lock(); int reads = store.Reads;
        await manager.Probe(); await manager.Initialize();
        Assert.Equal(reads, store.Reads); Assert.Throws<UserError>(() => manager.Acquire(d.Db.Settings));
        await manager.Unlock(id); Assert.Equal("secret", manager.Acquire(d.Db.Settings).Password);
        Assert.False(manager.Status().SessionLocked);
    }
    [Fact]
    public async Task ConnectionChangeNeverReusesEnvironmentOrSavedSecretAndExposesRetiredEntry()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var manager = new Credentials(d.Db, store, "environment"); var id = manager.Status().ConnectionId;
        await manager.Apply("saved", true, id);
        var changed = d.Db.Settings with { WebDavUrl = "https://different.invalid/path/" }; d.Db.Settings = changed; manager.ChangeConnection(changed);
        Assert.False(manager.Status().Ready); Assert.Throws<UserError>(() => manager.Acquire(changed));
        Assert.Throws<UserError>(() => manager.Expect(id)); Assert.Single(manager.Retired());
        await manager.Delete(manager.Status().ConnectionId, manager.Retired()[0].Id); Assert.Empty(store.Secrets);
    }
    [Fact]
    public async Task EnvironmentPriorityAndExplicitSessionOverrideDoNotPersistImplicitly()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore();
        var old = new Credentials(d.Db, store); await old.Apply("saved", true, old.Status().ConnectionId);
        var manager = new Credentials(d.Db, store, "environment"); await manager.Initialize();
        Assert.Equal("environment", manager.Acquire(d.Db.Settings).Password);
        manager.UseSession("session"); Assert.Equal("session", manager.Acquire(d.Db.Settings).Password);
        Assert.Equal("saved", Assert.Single(store.Secrets).Value);
        await manager.Delete(manager.Status().ConnectionId); Assert.False(manager.Status().Ready);
        var restarted = new Credentials(d.Db, store, "environment"); await restarted.Initialize();
        Assert.Equal("environment", restarted.Status().Source); Assert.False(restarted.Status().Saved);
    }
    [Fact]
    public async Task UnavailableOrLockedStoreAllowsSessionInputWithoutPersistingIt()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var store = new FakeCredentialStore { State = "unavailable" };
        var manager = new Credentials(d.Db, store); await manager.Initialize();
        Assert.Equal("unavailable", manager.Status().StoreState); manager.UseSession("temporary");
        Assert.True(manager.Status().Ready); Assert.Empty(store.Secrets);
        store.State = "available"; await manager.Apply("saved", true, manager.Status().ConnectionId); store.State = "locked";
        var restarted = new Credentials(d.Db, store); await restarted.Initialize();
        Assert.False(restarted.Status().Ready); Assert.Equal("locked", restarted.Status().StoreState);
        restarted.UseSession("temporary"); Assert.Equal("temporary", restarted.Acquire(d.Db.Settings).Password);
    }
    [Fact]
    public void ConnectionKeysPreservePathsAndAccountsButIgnoreWorkspaceAndDefaultPortSpelling()
    {
        var settings = new AppSettings { WebDavUrl = "https://EXAMPLE.com:443/Dav", WebDavUser = "User" };
        var key = Credentials.ConnectionId(settings);
        Assert.Equal(key, Credentials.ConnectionId(settings with { WebDavUrl = "https://example.com/Dav/", Workspace = "different", ProtocolVersion = 2 }));
        Assert.NotEqual(key, Credentials.ConnectionId(settings with { WebDavUrl = "https://example.com/dav/" }));
        Assert.NotEqual(key, Credentials.ConnectionId(settings with { WebDavUser = "user" }));
        Assert.NotEqual(key, Credentials.ConnectionId(settings with { WebDavUrl = "http://example.com/Dav/" }));
    }
    [Fact]
    public void RejectedLeaseCannotAutomaticallyRetryOrInvalidateANewPassword()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var manager = new Credentials(d.Db, new FakeCredentialStore());
        manager.UseSession("one"); var old = manager.Acquire(d.Db.Settings);
        old.Report(403); Assert.True(manager.Status().Ready); Assert.Equal("forbidden", manager.Status().Verification);
        old.Report(401); Assert.False(manager.Status().Ready); Assert.Equal("rejected", manager.Status().Verification);
        Assert.Throws<UserError>(() => manager.Acquire(d.Db.Settings));
        manager.UseSession("two"); old.Report(401); Assert.Equal("two", manager.Acquire(d.Db.Settings).Password);
    }
    [Fact]
    public async Task DifferentStateFoldersHaveDifferentOsItemKeys()
    {
        using var f = new Fixture(); var a = f.Device("a"); var b = f.Device("b"); var store = new FakeCredentialStore();
        var first = new Credentials(a.Db, store); var second = new Credentials(b.Db, store);
        await first.Apply("one", true, first.Status().ConnectionId);
        await second.Apply("two", true, second.Status().ConnectionId);
        await first.Delete(first.Status().ConnectionId);
        await second.Initialize(); Assert.Equal("two", second.Acquire(b.Db.Settings).Password); Assert.Single(store.Secrets);
    }
    [Theory]
    [InlineData("")]
    [InlineData("bad\0secret")]
    public void InvalidSecretsAreRejected(string value) => Assert.Throws<UserError>(() => Credentials.ValidatePassword(value));
    [Fact] public void OversizeSecretIsRejected() => Assert.Throws<UserError>(() => Credentials.ValidatePassword(new string('가', 1400)));
    [Fact]
    public async Task ActiveLaunchPreventsCredentialMutations()
    {
        using var f = new Fixture(); var d = f.Device("pc"); var manager = new Credentials(d.Db, new FakeCredentialStore());
        var coordinator = new Coordinator(d.Db, d.Snapshots, d.Engine, manager);
        d.Db.Put("launch", "running", new LaunchSession("running", "playing", "test", null, null, DateTimeOffset.UtcNow));
        Assert.Throws<UserError>(() => coordinator.Unlock("secret"));
        Assert.Throws<UserError>(() => coordinator.LockCredential());
        await Assert.ThrowsAsync<UserError>(() => coordinator.ApplyCredential("secret", false, manager.Status().ConnectionId));
        await Assert.ThrowsAsync<UserError>(() => coordinator.DeleteCredential(manager.Status().ConnectionId));
    }
}

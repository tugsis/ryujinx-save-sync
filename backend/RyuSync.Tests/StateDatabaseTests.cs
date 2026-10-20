using RyuSync;
using Xunit;

namespace RyuSync.Tests;

public class StateDatabaseTests
{
    [Fact]
    public void DisposeReleasesNativeHandlesBeforeDeletingDatabaseDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ryusync-db-lifetime-" + Guid.NewGuid().ToString("N"));
        using var database = new StateDatabase(root);
        var file = Path.Combine(root, "state.db");
        try
        {
            database.Put("test", "value", "persisted");
            Assert.Equal("persisted", database.Get<string>("test", "value"));
            // WAL remains while the disposed per-operation connections are pooled.
            // Unlike deleting an open file on Unix, this checks that the native
            // SQLite handle is really closed on every supported OS.
            Assert.True(File.Exists(file + "-wal"));

            database.Dispose();

            Assert.False(File.Exists(file + "-wal"));
            Assert.False(File.Exists(file + "-shm"));
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Directory.Delete(root, true);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            database.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DisposingOneDatabaseDoesNotClearAnotherDatabasesPool()
    {
        using var fixture = new Fixture();
        var a = fixture.Device("pc");
        var b = fixture.Device("deck");
        var secondWal = Path.Combine(b.Db.Root, "state.db-wal");
        Assert.True(File.Exists(secondWal));

        a.Db.Dispose();

        Assert.True(File.Exists(secondWal));
        Assert.Equal("deck", b.Db.Settings.DeviceName);
        Assert.Throws<ObjectDisposedException>(() => a.Db.List<string>("test"));
        a.Db.Dispose(); // Idempotent, including fixture teardown.
    }
}

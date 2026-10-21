using Microsoft.Data.Sqlite;
namespace RyuSync;

public record LegacyImportData(AppSettings Settings, Binding[] Bindings, string BackupPath);
public static class LegacyImport
{
    public static LegacyImportData Read(string sourceRoot, string destinationRoot)
    {
        sourceRoot = Emulators.CanonicalDirectory(sourceRoot);
        if (sourceRoot == Path.GetFullPath(destinationRoot)) throw new UserError("가져올 상태 폴더와 현재 폴더는 달라야 합니다.");
        var file = Path.Combine(sourceRoot, "state.db");
        if (!File.Exists(file)) throw new UserError("가져올 state.db가 없습니다.");
        var lockPath = Path.Combine(sourceRoot, "agent.lock");
        using var agentLock = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); source.Open();
        var folder = Path.Combine(destinationRoot, "imports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var backupPath = Path.Combine(folder, "state.db");
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString())) { target.Open(); source.BackupDatabase(target); }
        using var copy = new StateDatabase(folder);
        var settings = copy.Get<AppSettings>("settings", "app") ?? throw new UserError("이전 설정을 찾지 못했습니다.");
        if (settings.ProtocolVersion != 1 || settings.Installations.Length != 0) throw new UserError("v1 Ryujinx 설정만 가져올 수 있습니다.");
        if (copy.List<PendingUpload>("pending").Count > 0 || copy.List<ApplyJournal>("journal").Count > 0 || copy.List<RestoreIntent>("restore").Count > 0 || copy.List<LaunchSession>("launch").Any(l => l.Status is "playing" or "preparing" or "post-sync")) throw new UserError("이전 앱의 미전송·복구·실행 작업을 먼저 해결하세요.");
        var bindings = copy.List<Binding>("binding").ToArray();
        if (bindings.Any(b => b.EmulatorKind != "ryujinx" || b.InstallationId != "legacy")) throw new UserError("이전 기본 Ryujinx 연결만 가져올 수 있습니다.");
        return new(settings, bindings, backupPath);
    }
}

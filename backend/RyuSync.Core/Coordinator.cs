using System.Diagnostics;
using System.Text.RegularExpressions;
namespace RyuSync;

public sealed class Coordinator
{
    private readonly StateDatabase db;
    private readonly Snapshots snapshots;
    private readonly SyncEngine engine;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? password;
    private readonly object launchLock = new();
    private Task? launchTask;
    public Coordinator(StateDatabase db, Snapshots snapshots, SyncEngine engine)
    {
        this.db = db; this.snapshots = snapshots; this.engine = engine;
        password = Environment.GetEnvironmentVariable("RYUSYNC_WEBDAV_PASSWORD");
        snapshots.Recover();
        foreach (var job in db.List<Job>("job").Where(j => j.Status == "running")) db.Put("job", job.Id, job with { Status = "interrupted", Message = "프로그램이 종료되어 작업이 중단되었습니다. 다시 동기화해 주세요." });
    }
    public bool HasCredential => password is not null;
    public void Unlock(string? value) { password = value; }
    public static void ValidateSettings(AppSettings s)
    {
        if (s.DeviceName.Trim().Length is < 1 or > 60) throw new UserError("기기 이름은 1~60자로 입력하세요.");
        if (!Regex.IsMatch(s.Workspace, "^[a-zA-Z0-9_-]{1,64}$")) throw new UserError("동기화 공간은 영문·숫자·밑줄·하이픈 1~64자로 입력하세요.");
        if (s.Arguments.Length > 64 || s.Arguments.Any(x => x.Length > 4096)) throw new UserError("실행 인수가 너무 깁니다.");
        if (s.WebDavUrl.Length > 2048 || s.WebDavUser.Length > 256) throw new UserError("저장소 설정이 너무 깁니다.");
    }
    public async Task<AppSettings> SaveSettings(AppSettings value)
    {
        ValidateSettings(value); await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 세션이 진행 중에는 설정을 바꿀 수 없습니다.");
            var old = db.Settings;
            if (old.DataRoot != value.DataRoot && db.List<Binding>("binding").Count > 0) throw new UserError("데이터 폴더 변경 전에 등록된 게임의 동기화를 해제하세요.");
            if (old.WebDavUrl != value.WebDavUrl || old.WebDavUser != value.WebDavUser) password = null;
            value = value with { DeviceId = old.DeviceId }; db.Settings = value; return value;
        }
        finally { gate.Release(); }
    }
    private IObjectStore Store() => new WebDavStore(db.Settings, password ?? throw new UserError("WebDAV 비밀번호를 입력해 연결 잠금을 해제하세요."));
    private RevisionRepository Repository(IObjectStore store) => new(store, db.Settings, Path.Combine(db.Root, "staging"));
    public async Task TestConnection()
    {
        using var store = Store(); var folder = "ryusync/" + db.Settings.Workspace + "/connection-tests"; await store.EnsureDirectory(folder);
        var path = folder + "/" + Guid.NewGuid().ToString("N") + ".txt"; var bytes = System.Text.Encoding.UTF8.GetBytes("RyuSync connection test");
        await store.Write(path, bytes); try { if (!(await store.Read(path, 1024)).SequenceEqual(bytes)) throw new UserError("연결 테스트의 다운로드 내용이 다릅니다."); } finally { await store.Delete(path); }
    }
    public async Task<Revision[]> Catalog() { using var store = Store(); return await Repository(store).Catalog(CancellationToken.None); }
    public async Task<Binding> Register(string discoveryId, string slot)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 세션 중에는 게임 연결을 바꿀 수 없습니다.");
            if (!Regex.IsMatch(slot, "^[a-zA-Z0-9_-]{1,32}$")) throw new UserError("프로필 연결 이름은 영문·숫자·밑줄·하이픈 1~32자로 입력하세요.");
            ProcessGuard.EnsureIdle(db.Settings); var save = Discovery.Scan(db.Settings.DataRoot).Saves.SingleOrDefault(x => x.Id == discoveryId) ?? throw new UserError("다시 탐색한 뒤 게임을 선택하세요.");
            var b = new Binding(save.Id, save.TitleId, save.Title, save.UserId, save.SaveDataId, save.SaveRoot, slot);
            if (db.List<Binding>("binding").Any(x => x.Id != b.Id && x.RemoteKey == b.RemoteKey)) throw new UserError("같은 게임·프로필 연결 이름이 이미 사용 중입니다. 다른 연결 이름을 지정하세요.");
            db.Put("binding", b.Id, b); return b;
        }
        finally { gate.Release(); }
    }
    public async Task RemoveBinding(string id)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("Ryujinx 실행 중에는 연결을 해제할 수 없습니다."); db.Delete("binding", id); db.Delete("baseline", id);
        }
        finally { gate.Release(); }
    }
    public LaunchSession? ActiveLaunch => db.List<LaunchSession>("launch").FirstOrDefault(x => x.Status is "preparing" or "playing" or "post-sync");
    public Job StartSync(string? bindingId, string source = "web", Resolution? resolution = null)
    {
        if (bindingId is not null && !db.List<Binding>("binding").Any(x => x.Id == bindingId)) throw new UserError("등록된 게임이 없습니다.");
        var active = ActiveLaunch;
        var job = new Job(Guid.NewGuid().ToString("N"), "sync", source, active is null ? "queued" : "waiting", active is null ? "동기화 대기 중" : "Ryujinx 종료 후 동기화합니다.", DateTimeOffset.UtcNow, []);
        db.Put("job", job.Id, job);
        if (active is null) _ = Task.Run(() => RunJob(job, bindingId, resolution));
        return job;
    }
    private void VerifyBinding(Binding b)
    {
        var found = Discovery.Scan(db.Settings.DataRoot).Saves.SingleOrDefault(x => x.Id == b.Id);
        if (found is null || found.UserId != b.UserId || found.TitleId != b.TitleId || found.SaveDataId != b.SaveDataId || found.Status != "ready") throw new UserError("세이브 연결이 변경되었거나 초기화되지 않았습니다. 다시 탐색하세요.");
    }
    private async Task ReplayPending(RevisionRepository remote, CancellationToken ct)
    {
        foreach (var p in db.List<PendingUpload>("pending"))
        {
            if (p.Scope != remote.Scope) continue;
            if (!db.List<Binding>("binding").Any(b => b.Id == p.Binding.Id && b.RemoteKey == p.Binding.RemoteKey)) continue;
            if (!File.Exists(p.Snapshot.ArchivePath) || Hashing.FileHash(p.Snapshot.ArchivePath) != p.Snapshot.ArchiveHash) throw new UserError("대기 중인 스냅샷이 손상되었습니다. 백업을 확인하세요.");
            var rows = await remote.ReadAll(p.Binding.RemoteKey, ct);
            if (p.Parents.Any(parent => !rows.Any(x => x.Id == parent))) throw new UserError("미전송 버전의 부모 이력이 없습니다. 저장소를 확인하세요.");
            var existing = rows.FirstOrDefault(r => r.DeviceId == db.Settings.DeviceId && r.ContentHash == p.Snapshot.Tree.Hash && r.Parents.Order().SequenceEqual(p.Parents.Order()));
            if (existing is null) await remote.Publish(p.Binding, p.Snapshot, p.Parents, ct);
            db.Delete("pending", p.Id); Snapshots.CleanSnapshot(p.Snapshot);
        }
    }
    private async Task<Job> SyncInsideGate(Job job, string? bindingId, Resolution? resolution)
    {
        job = job with { Status = "running", Message = "서버와 로컬 버전을 비교하고 있습니다." }; db.Put("job", job.Id, job);
        try
        {
            ProcessGuard.EnsureIdle(db.Settings); if (db.List<ApplyJournal>("journal").Count > 0) throw new UserError("미완료 파일 적용을 먼저 복구하세요."); using var store = Store(); var remote = Repository(store); await ReplayPending(remote, CancellationToken.None);
            var bindings = db.List<Binding>("binding").Where(x => x.Enabled && (bindingId is null || x.Id == bindingId)).ToArray();
            if (bindings.Length == 0) throw new UserError("동기화할 게임을 먼저 등록하세요."); var results = new List<SyncOutcome>();
            foreach (var binding in bindings)
            {
                try { VerifyBinding(binding); results.Add(await engine.Sync(binding, db.Settings, remote, resolution, CancellationToken.None)); }
                catch (Exception e) when (e is UserError or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                { results.Add(new(binding.Id, "failed", Friendly(e))); }
                job = job with { Results = results.ToArray() }; db.Put("job", job.Id, job);
            }
            var status = results.Any(x => x.Status == "failed") ? "failed" : results.Any(x => x.Status == "conflict") ? "conflict" : results.Any(x => x.Status == "empty") ? "blocked" : "completed";
            job = job with { Status = status, Message = status == "completed" ? "동기화를 완료했습니다." : "확인이 필요한 항목이 있습니다.", Results = results.ToArray() };
        }
        catch (Exception e) { job = job with { Status = "failed", Message = Friendly(e) }; }
        db.Put("job", job.Id, job); return job;
    }
    private async Task RunJob(Job job, string? bindingId, Resolution? resolution)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) { db.Put("job", job.Id, job with { Status = "waiting", Message = "Ryujinx 종료 후 동기화합니다." }); return; }
            await SyncInsideGate(job, bindingId, resolution);
        }
        finally { gate.Release(); }
    }
    public LaunchSession StartLaunch()
    {
        lock (launchLock)
        {
            if (ActiveLaunch is not null || launchTask is { IsCompleted: false }) throw new UserError("이미 실행 준비 또는 플레이 중입니다.");
            var s = db.Settings; if (!File.Exists(s.Executable)) throw new UserError("Ryujinx 실행 파일을 먼저 설정하세요.");
            if (new[] { "sh", "bash", "cmd", "powershell", "pwsh" }.Contains(Path.GetFileNameWithoutExtension(s.Executable).ToLowerInvariant())) throw new UserError("셸 대신 실제 Ryujinx 실행 파일을 지정하세요.");
            var session = new LaunchSession(Guid.NewGuid().ToString("N"), "preparing", "실행 전 동기화를 준비합니다.", null, null, DateTimeOffset.UtcNow); db.Put("launch", session.Id, session);
            launchTask = Task.Run(() => RunLaunch(session)); return session;
        }
    }
    private async Task RunLaunch(LaunchSession session)
    {
        Process? process = null; await gate.WaitAsync();
        try
        {
            var job = new Job(Guid.NewGuid().ToString("N"), "sync", "steam-pre", "queued", "실행 전 동기화", DateTimeOffset.UtcNow, []);
            session = session with { JobId = job.Id }; db.Put("launch", session.Id, session);
            job = await SyncInsideGate(job, null, null);
            if (job.Status != "completed") { db.Put("launch", session.Id, session with { Status = "blocked", Message = "실행 전 동기화를 해결한 뒤 다시 실행하세요." }); return; }
            var s = db.Settings; ProcessGuard.EnsureIdle(s); var start = new ProcessStartInfo(s.Executable) { UseShellExecute = false, WorkingDirectory = string.IsNullOrWhiteSpace(s.WorkingDirectory) ? Path.GetDirectoryName(s.Executable)! : s.WorkingDirectory };
            foreach (var argument in s.Arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new UserError("Ryujinx 프로세스를 시작하지 못했습니다.");
            session = session with { Status = "playing", Message = "Ryujinx 실행 중입니다.", ProcessId = process.Id, ProcessStartedAt = process.StartTime.ToUniversalTime() }; db.Put("launch", session.Id, session);
        }
        catch (Exception e) { db.Put("launch", session.Id, session with { Status = "failed", Message = Friendly(e) }); return; }
        finally { gate.Release(); }
        try { await process.WaitForExitAsync(); await Task.Delay(1000); await FinishLaunch(session); } catch (Exception e) { db.Put("launch", session.Id, session with { Status = "failed", Message = Friendly(e) }); } finally { process.Dispose(); }
    }
    private async Task FinishLaunch(LaunchSession session)
    {
        await gate.WaitAsync(); try
        {
            db.Put("launch", session.Id, session with { Status = "post-sync", Message = "종료 후 동기화 중입니다." });
            var job = new Job(Guid.NewGuid().ToString("N"), "sync", "steam-post", "queued", "종료 후 동기화", DateTimeOffset.UtcNow, []);
            job = await SyncInsideGate(job, null, null);
            db.Put("launch", session.Id, session with { Status = job.Status == "completed" ? "completed" : "needs-attention", Message = job.Message, JobId = job.Id });
            foreach (var waiting in db.List<Job>("job").Where(x => x.Status == "waiting")) db.Put("job", waiting.Id, waiting with { Status = job.Status, Message = "종료 후 동기화 작업에 포함되었습니다.", Results = job.Results });
        }
        finally { gate.Release(); }
    }
    public void RecoverLaunches()
    {
        foreach (var session in db.List<LaunchSession>("launch").Where(x => x.Status is "preparing" or "playing" or "post-sync"))
        {
            if (session.ProcessId is int pid && session.ProcessStartedAt is DateTime start)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p.StartTime.ToUniversalTime() == start) { launchTask = Task.Run(async () => { using (p) { await p.WaitForExitAsync(); await FinishLaunch(session); } }); continue; }
                    p.Dispose();
                }
                catch (ArgumentException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            db.Put("launch", session.Id, session with { Status = "needs-attention", Message = "중단된 실행 세션입니다. 프로세스 종료를 확인한 뒤 웹에서 동기화하세요." });
        }
    }
    public static string Friendly(Exception e) => e switch
    {
        UserError => e.Message,
        TaskCanceledException => "요청 시간이 초과되었습니다. 로컬 세이브와 미전송 스냅샷은 보존됩니다.",
        HttpRequestException => "서버에 연결하지 못했습니다. 주소·네트워크·인증서를 확인하세요.",
        UnauthorizedAccessException => "파일 접근 권한이 없습니다.",
        IOException => "파일 작업에 실패했습니다. 저장 공간과 파일 잠금, 백업을 확인하세요.",
        _ => "작업을 완료하지 못했습니다. 진단 로그를 확인하세요."
    };
}

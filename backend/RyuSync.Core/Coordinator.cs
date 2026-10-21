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
        foreach (var job in db.List<Job>("job").Where(j => j.Status is "running" or "queued" || j.Status == "waiting" && ActiveLaunch is null))
        {
            db.Put("job", job.Id, job with { Status = "interrupted", Message = "프로그램이 종료되어 작업이 중단되었습니다. 다시 동기화해 주세요." }); db.Delete("sync-request", job.Id);
        }
    }
    public bool HasCredential => password is not null;
    public void Unlock(string? value) { password = value; }
    public static void ValidateSettings(AppSettings s)
    {
        if (s.DeviceName.Trim().Length is < 1 or > 60) throw new UserError("기기 이름은 1~60자로 입력하세요.");
        if (!Regex.IsMatch(s.Workspace, "^[a-zA-Z0-9_-]{1,64}$")) throw new UserError("동기화 공간은 영문·숫자·밑줄·하이픈 1~64자로 입력하세요.");
        if (s.ProtocolVersion is not (1 or 2)) throw new UserError("지원하지 않는 저장소 버전입니다.");
        if (s.Installations.Length > 16 || s.Installations.Select(i => i.Id).Distinct().Count() != s.Installations.Length) throw new UserError("설치본은 중복 없이 16개까지 등록할 수 있습니다.");
        foreach (var i in s.Installations)
        {
            if (i.Id == "legacy" || !Regex.IsMatch(i.Id, "^[a-zA-Z0-9_-]{1,64}$") || string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 80 || i.Build.Length > 80) throw new UserError("설치본 ID·이름·빌드를 확인하세요.");
            _ = Emulators.Adapter(i.Kind);
            if (i.Kind == "yuzu" && s.ProtocolVersion != 2) throw new UserError("Yuzu 설치본은 v2 저장소에서만 등록할 수 있습니다.");
            if (i.Arguments.Length > 64 || i.Arguments.Any(a => a.Length > 4096)) throw new UserError("설치본 실행 인수가 너무 깁니다.");
        }
        if (s.Arguments.Length > 64 || s.Arguments.Any(x => x.Length > 4096)) throw new UserError("실행 인수가 너무 깁니다.");
        if (s.WebDavUrl.Length > 2048 || s.WebDavUser.Length > 256) throw new UserError("저장소 설정이 너무 깁니다.");
    }
    public async Task<AppSettings> SaveSettings(AppSettings value)
    {
        value = value with { Installations = value.Installations.Select(i => i with { Arguments = i.Arguments.Where(a => a.Length > 0).ToArray() }).ToArray() };
        ValidateSettings(value); await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 세션이 진행 중에는 설정을 바꿀 수 없습니다.");
            var old = db.Settings;
            if (old.ProtocolVersion != value.ProtocolVersion && (db.List<Binding>("binding").Count > 0 || db.List<PendingUpload>("pending").Count > 0)) throw new UserError("등록된 연결 또는 미전송 버전이 있는 상태에서 저장소 버전을 변경할 수 없습니다.");
            foreach (var binding in db.List<Binding>("binding").Where(b => b.InstallationId != "legacy"))
            {
                var before = Emulators.Installation(old, binding.InstallationId); var after = Emulators.Installation(value, binding.InstallationId);
                if (before.Kind != after.Kind || before.DataRoot != after.DataRoot || before.NandRoot != after.NandRoot || before.Build != after.Build) throw new UserError("등록된 게임을 해제한 뒤 설치본의 종류·경로·빌드를 변경하세요.");
            }
            foreach (var profile in db.List<LaunchProfile>("launch-profile")) _ = Emulators.Installation(value, profile.InstallationId);
            if (old.DataRoot != value.DataRoot && db.List<Binding>("binding").Any(b => b.InstallationId == "legacy")) throw new UserError("데이터 폴더 변경 전에 등록된 게임의 동기화를 해제하세요.");
            if (old.WebDavUrl != value.WebDavUrl || old.WebDavUser != value.WebDavUser) password = null;
            value = value with { DeviceId = old.DeviceId }; db.Settings = value; return value;
        }
        finally { gate.Release(); }
    }
    private IObjectStore Store() => new WebDavStore(db.Settings, password ?? throw new UserError("WebDAV 비밀번호를 입력해 연결 잠금을 해제하세요."));
    private RevisionRepository Repository(IObjectStore store) => new(store, db.Settings, Path.Combine(db.Root, "staging"));
    public async Task TestConnection()
    {
        using var store = Store(); var folder = Repository(store).Prefix + "/connection-tests"; await store.EnsureDirectory(folder);
        var path = folder + "/" + Guid.NewGuid().ToString("N") + ".txt"; var bytes = System.Text.Encoding.UTF8.GetBytes("RyuSync connection test");
        await store.Write(path, bytes); try { if (!(await store.Read(path, 1024)).SequenceEqual(bytes)) throw new UserError("연결 테스트의 다운로드 내용이 다릅니다."); } finally { await store.Delete(path); }
    }
    public async Task<Revision[]> Catalog() { using var store = Store(); return await Repository(store).Catalog(CancellationToken.None); }
    public async Task<Binding> Register(string discoveryId, string slot, string installationId = "legacy", string gameVersion = "", string? restoreReservationId = null)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 세션 중에는 게임 연결을 바꿀 수 없습니다.");
            if (!Regex.IsMatch(slot, "^[a-zA-Z0-9_-]{1,32}$")) throw new UserError("프로필 연결 이름은 영문·숫자·밑줄·하이픈 1~32자로 입력하세요.");
            ProcessGuard.EnsureIdle(db.Settings); var installation = Emulators.Installation(db.Settings, installationId); var save = Scan(installationId).Saves.SingleOrDefault(x => x.Id == discoveryId) ?? throw new UserError("다시 탐색한 뒤 게임을 선택하세요.");
            if (save.Status != "ready") throw new UserError("게임을 한 번 실행·저장·종료하고 다시 탐색하세요.");
            if (gameVersion.Length > 80) throw new UserError("게임 버전은 80자 이내로 입력하세요.");
            var b = new Binding(save.Id, save.TitleId, save.Title, save.UserId, save.SaveDataId, save.SaveRoot, slot) { InstallationId = installationId, EmulatorKind = installation.Kind, EmulatorBuild = installation.Build, GameVersion = gameVersion };
            if (db.List<Binding>("binding").Any(x => x.Id == b.Id)) throw new UserError("이미 등록된 세이브입니다. 연결을 해제한 뒤 변경하세요.");
            foreach (var other in db.List<Binding>("binding"))
            {
                var a = Path.GetFullPath(other.SaveRoot) + Path.DirectorySeparatorChar; var c = Path.GetFullPath(b.SaveRoot) + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (a.StartsWith(c, comparison) || c.StartsWith(a, comparison)) throw new UserError("같거나 겹치는 세이브 경로는 중복 등록할 수 없습니다.");
                if (other.RemoteKey == b.RemoteKey && other.InstallationId == b.InstallationId) throw new UserError("같은 설치본의 사용자마다 다른 슬롯을 사용하세요.");
            }
            RestoreReservation? reservation = null;
            if (restoreReservationId is not null)
            {
                reservation = db.Get<RestoreReservation>("restore-reservation", restoreReservationId) ?? throw new UserError("복원 예약이 없습니다.");
                using var store = Store();
                if (reservation.InstallationId != installationId || reservation.TitleId != b.TitleId || reservation.Slot != slot || reservation.Scope != Repository(store).Scope) throw new UserError("선택한 게임·설치본·슬롯이 복원 예약과 다릅니다.");
                db.Put("restore", b.Id, new RestoreIntent(b.Id, reservation.RevisionId, reservation.Scope));
            }
            else if (db.List<RestoreReservation>("restore-reservation").Any(r => r.InstallationId == installationId && r.TitleId == b.TitleId)) throw new UserError("이 게임은 서버 복원 예약이 있습니다. 예약을 선택해 연결하거나 예약을 해제하세요.");
            db.Put("binding", b.Id, b);
            if (reservation is not null) db.Delete("restore-reservation", reservation.Id);
            return b;
        }
        finally { gate.Release(); }
    }
    public async Task RemoveBinding(string id)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("에뮬레이터 실행 중에는 연결을 해제할 수 없습니다."); db.Delete("binding", id); db.Delete("baseline", id); db.Delete("compatibility", id); db.Delete("restore", id);
        }
        finally { gate.Release(); }
    }
    public async Task<LegacyImportData> ImportLegacy(string sourceRoot)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null || db.List<Binding>("binding").Count != 0 || db.List<PendingUpload>("pending").Count != 0 || db.List<ApplyJournal>("journal").Count != 0 || db.List<RestoreReservation>("restore-reservation").Count != 0 || db.List<LaunchProfile>("launch-profile").Count != 0 || db.Settings.Installations.Length != 0) throw new UserError("비어 있는 새 v2 상태 폴더에서만 가져올 수 있습니다.");
            ProcessGuard.EnsureIdle(db.Settings); var imported = LegacyImport.Read(sourceRoot, db.Root);
            var settings = imported.Settings with { ProtocolVersion = 2, DeviceId = db.Settings.DeviceId }; ValidateSettings(settings); ProcessGuard.EnsureIdle(settings);
            var saves = Emulators.Scan(Emulators.Installation(settings, "legacy")).Saves;
            foreach (var binding in imported.Bindings)
            {
                var found = saves.SingleOrDefault(s => s.Id == binding.Id && s.TitleId == binding.TitleId && s.UserId == binding.UserId && s.SaveDataId == binding.SaveDataId && s.SaveRoot == binding.SaveRoot && s.Status == "ready");
                if (found is null) throw new UserError("이전 세이브 경로가 현재 설치본과 다릅니다. 새로 탐색·등록하세요. 원본은 변경하지 않았습니다.");
            }
            // Settings and bindings are restored from one validated SQLite snapshot.
            db.ImportConfiguration(settings, imported.Bindings);
            password = Environment.GetEnvironmentVariable("RYUSYNC_WEBDAV_PASSWORD");
            return imported;
        }
        finally { gate.Release(); }
    }
    public async Task<Revision[]> LegacyCatalog()
    {
        using var store = Store(); return await new RevisionRepository(store, db.Settings with { ProtocolVersion = 1 }, Path.Combine(db.Root, "staging")).Catalog(default);
    }
    public async Task<Revision> ImportLegacyRevision(string bindingId, string revisionId)
    {
        await gate.WaitAsync(); try
        {
            if (db.Settings.ProtocolVersion != 2 || ActiveLaunch is not null) throw new UserError("실행이 중지된 v2 설정에서만 가져올 수 있습니다.");
            ProcessGuard.EnsureIdle(db.Settings);
            var binding = db.Get<Binding>("binding", bindingId) ?? throw new UserError("동기화 대상을 먼저 연결하세요."); VerifyBinding(binding);
            using var store = Store(); var remote = Repository(store);
            if ((await remote.ReadAll(binding.RemoteKey, default)).Count != 0 || db.List<PendingUpload>("pending").Count != 0) throw new UserError("비어 있는 v2 그룹에만 이전 서버 버전을 가져올 수 있습니다.");
            var old = new RevisionRepository(store, db.Settings with { ProtocolVersion = 1 }, Path.Combine(db.Root, "staging"));
            var revision = (await old.ReadAll(binding.RemoteKey, default)).SingleOrDefault(r => r.Id == revisionId) ?? throw new UserError("이전 서버 버전을 찾지 못했습니다.");
            var snapshot = await old.Fetch(revision, default);
            try
            {
                var source = binding with { EmulatorKind = revision.SourceEmulator, EmulatorBuild = revision.SourceBuild, GameVersion = revision.GameVersion };
                return await remote.Publish(source, snapshot, [], default, old.Scope, revision.Id);
            }
            finally { Snapshots.CleanSnapshot(snapshot); }
        }
        finally { gate.Release(); }
    }
    public async Task<RestoreReservation> ReserveRestore(string installationId, string revisionId)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 복원을 예약할 수 없습니다.");
            _ = Emulators.Installation(db.Settings, installationId);
            using var store = Store(); var remote = Repository(store);
            var revision = (await remote.Catalog(default)).SingleOrDefault(r => r.Id == revisionId) ?? throw new UserError("현재 서버 버전을 다시 조회하세요.");
            if (db.List<Binding>("binding").Any(b => b.InstallationId == installationId && b.TitleId == revision.TitleId)) throw new UserError("이미 연결된 게임은 충돌 선택으로 복원하세요.");
            if (db.List<RestoreReservation>("restore-reservation").Any(r => r.InstallationId == installationId && r.TitleId == revision.TitleId)) throw new UserError("이미 복원 예약이 있습니다.");
            var reservation = new RestoreReservation(Guid.NewGuid().ToString("N"), installationId, revision.TitleId, revision.Slot, revision.Id, remote.Scope);
            db.Put("restore-reservation", reservation.Id, reservation); return reservation;
        }
        finally { gate.Release(); }
    }
    public async Task CancelRestore(string id)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 복원 예약을 변경할 수 없습니다.");
            db.Delete("restore-reservation", id);
        }
        finally { gate.Release(); }
    }
    public ScanResult Scan(string installationId)
    {
        ProcessGuard.EnsureIdle(db.Settings);
        var scan = Emulators.Scan(Emulators.Installation(db.Settings, installationId));
        var names = db.List<Binding>("binding").GroupBy(b => b.TitleId).ToDictionary(g => g.Key, g => g.First().Title);
        return scan with { Saves = scan.Saves.Select(s => s.Title == s.TitleId && names.TryGetValue(s.TitleId, out var name) ? s with { Title = name } : s).ToList() };
    }
    public async Task<LaunchProfile> SaveLaunchProfile(LaunchProfile profile)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 프로필을 변경할 수 없습니다.");
            _ = Emulators.Installation(db.Settings, profile.InstallationId);
            if (!Regex.IsMatch(profile.Id, "^[a-zA-Z0-9_-]{1,64}$") || profile.Name.Length is < 1 or > 80 || profile.Arguments.Length > 64 || profile.Arguments.Any(a => a.Length > 4096)) throw new UserError("실행 프로필 정보를 확인하세요.");
            var available = db.List<Binding>("binding").Where(b => b.InstallationId == profile.InstallationId).Select(b => b.RemoteKey).ToHashSet();
            if (profile.GroupKeys.Any(g => !available.Contains(g))) throw new UserError("선택한 설치본에 연결되지 않은 그룹입니다.");
            db.Put("launch-profile", profile.Id, profile); return profile;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteLaunchProfile(string id)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 프로필을 삭제할 수 없습니다.");
            db.Delete("launch-profile", id);
        }
        finally { gate.Release(); }
    }
    private LaunchProfile Launch(string? id) => id is null ? new("default", "기본 Ryujinx", "legacy", [], []) : db.Get<LaunchProfile>("launch-profile", id) ?? throw new UserError("실행 프로필이 없습니다.");
    private string[] Groups(LaunchSession session)
    {
        var profile = Launch(session.LaunchProfileId);
        var keys = db.List<Binding>("binding").Where(b => b.InstallationId == profile.InstallationId && b.Enabled).Select(b => b.RemoteKey);
        return keys.Where(k => profile.GroupKeys.Length == 0 || profile.GroupKeys.Contains(k)).Distinct().ToArray();
    }
    public async Task<SyncOutcome> CompareCompatibility(string bindingId)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 비교할 수 없습니다.");
            var binding = db.Get<Binding>("binding", bindingId) ?? throw new UserError("등록된 게임이 없습니다."); VerifyBinding(binding);
            using var store = Store(); var rows = await Repository(store).ReadAll(binding.RemoteKey, default); var heads = RevisionRepository.Heads(rows);
            return new(bindingId, Compatibility.Required(binding, rows) ? "compatibility-required" : "compatible", "호환성 상태: " + Compatibility.State(db, binding, rows), Emulators.Inspect(binding).Hash, heads.Select(r => r.Id).ToArray(), heads);
        }
        finally { gate.Release(); }
    }
    public async Task<CompatibilityApproval> ApproveCompatibility(string bindingId, string state, string localHash, string[] expectedHeads)
    {
        if (state is not ("trial" or "confirmed")) throw new UserError("호환성 승인 상태가 올바르지 않습니다.");
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) throw new UserError("실행 중에는 호환성 승인을 바꿀 수 없습니다.");
            ProcessGuard.EnsureIdle(db.Settings);
            var binding = db.Get<Binding>("binding", bindingId) ?? throw new UserError("등록된 게임이 없습니다."); VerifyBinding(binding);
            using var store = Store(); var rows = await Repository(store).ReadAll(binding.RemoteKey, default);
            if (Emulators.Inspect(binding).Hash != localHash || !RevisionRepository.Heads(rows).Select(r => r.Id).Order().SequenceEqual(expectedHeads.Order())) throw new UserError("비교 상태가 바뀌었습니다. 다시 동기화하고 승인하세요.");
            if (!Compatibility.Required(binding, rows)) throw new UserError("교차 에뮬레이터 세이브가 없습니다.");
            var signature = Compatibility.Signature(binding, rows);
            if (state == "confirmed" && db.Get<CompatibilityApproval>("compatibility", bindingId) is not { State: "trial" }) throw new UserError("시험 적용을 먼저 승인하고 게임에서 로드·저장을 확인하세요.");
            if (state == "confirmed" && db.Get<CompatibilityApproval>("compatibility", bindingId)?.Signature != signature) throw new UserError("출처·빌드·게임 버전이 바뀌었습니다. 시험 적용을 다시 승인하세요.");
            var approval = new CompatibilityApproval(bindingId, signature, state); db.Put("compatibility", bindingId, approval); return approval;
        }
        finally { gate.Release(); }
    }
    public LaunchSession? ActiveLaunch => db.List<LaunchSession>("launch").FirstOrDefault(x => x.Status is "preparing" or "playing" or "post-sync");
    public Job StartSync(string? bindingId, string source = "web", Resolution? resolution = null)
    {
        if (bindingId is not null && !db.List<Binding>("binding").Any(x => x.Id == bindingId)) throw new UserError("등록된 게임이 없습니다.");
        var active = ActiveLaunch;
        var job = new Job(Guid.NewGuid().ToString("N"), "sync", source, active is null ? "queued" : "waiting", active is null ? "동기화 대기 중" : "에뮬레이터 종료 후 동기화합니다.", DateTimeOffset.UtcNow, []);
        db.Put("job", job.Id, job); db.Put("sync-request", job.Id, new QueuedSyncRequest(job.Id, bindingId, resolution));
        if (active is null) _ = Task.Run(() => RunJob(job, bindingId, resolution));
        return job;
    }
    private void VerifyBinding(Binding b)
    {
        var found = Scan(b.InstallationId).Saves.SingleOrDefault(x => x.Id == b.Id);
        if (found is null || found.UserId != b.UserId || found.TitleId != b.TitleId || found.SaveDataId != b.SaveDataId || found.SaveRoot != b.SaveRoot || found.Status != "ready") throw new UserError("세이브 연결이 변경되었거나 초기화되지 않았습니다. 다시 탐색하세요.");
    }
    private async Task ReplayPending(RevisionRepository remote, CancellationToken ct)
    {
        foreach (var p in db.List<PendingUpload>("pending"))
        {
            if (p.Scope != remote.Scope) continue;
            if (!db.List<Binding>("binding").Any(b => b.Id == p.Binding.Id && b.RemoteKey == p.Binding.RemoteKey && b.SaveRoot == p.Binding.SaveRoot && b.EmulatorKind == p.Binding.EmulatorKind && b.EmulatorBuild == p.Binding.EmulatorBuild && b.GameVersion == p.Binding.GameVersion)) continue;
            if (!File.Exists(p.Snapshot.ArchivePath) || Hashing.FileHash(p.Snapshot.ArchivePath) != p.Snapshot.ArchiveHash) throw new UserError("대기 중인 스냅샷이 손상되었습니다. 백업을 확인하세요.");
            var rows = await remote.ReadAll(p.Binding.RemoteKey, ct);
            if (p.Parents.Any(parent => !rows.Any(x => x.Id == parent))) throw new UserError("미전송 버전의 부모 이력이 없습니다. 저장소를 확인하세요.");
            var existing = rows.FirstOrDefault(r => r.DeviceId == db.Settings.DeviceId && r.SourceEmulator == p.Binding.EmulatorKind && r.SourceBuild == p.Binding.EmulatorBuild && r.GameVersion == p.Binding.GameVersion && r.ContentHash == p.Snapshot.Tree.Hash && r.Parents.Order().SequenceEqual(p.Parents.Order()));
            if (existing is null) await remote.Publish(p.Binding, p.Snapshot, p.Parents, ct);
            db.Delete("pending", p.Id); Snapshots.CleanSnapshot(p.Snapshot);
        }
    }
    private async Task<Job> SyncInsideGate(Job job, string? bindingId, Resolution? resolution, string[]? groupKeys = null, bool requireConfirmed = false)
    {
        job = job with { Status = "running", Message = "서버와 로컬 버전을 비교하고 있습니다." }; db.Put("job", job.Id, job);
        try
        {
            ProcessGuard.EnsureIdle(db.Settings); if (db.List<ApplyJournal>("journal").Count > 0) throw new UserError("미완료 파일 적용을 먼저 복구하세요."); using var store = Store(); var remote = Repository(store); await ReplayPending(remote, CancellationToken.None);
            var bindings = db.List<Binding>("binding").Where(x => x.Enabled && (bindingId is null || x.Id == bindingId) && (groupKeys is null || groupKeys.Contains(x.RemoteKey))).OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
            if (bindings.Length == 0) throw new UserError("동기화할 게임을 먼저 등록하세요."); var results = new List<SyncOutcome>();
            foreach (var binding in bindings)
            {
                try
                {
                    VerifyBinding(binding);
                    if (requireConfirmed)
                    {
                        var rows = await remote.ReadAll(binding.RemoteKey, CancellationToken.None);
                        if (Compatibility.Required(binding, rows) && Compatibility.State(db, binding, rows) != "confirmed")
                        {
                            results.Add(new(binding.Id, "compatibility-required", "게임에서 교차 세이브의 로드·저장을 확인하고 호환성 확인을 완료한 뒤 실행하세요.", Emulators.Inspect(binding).Hash, RevisionRepository.Heads(rows).Select(r => r.Id).ToArray(), RevisionRepository.Heads(rows)));
                            continue;
                        }
                    }
                    results.Add(await engine.Sync(binding, db.Settings, remote, resolution, CancellationToken.None));
                }
                catch (Exception e) when (e is UserError or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                { results.Add(new(binding.Id, "failed", Friendly(e))); }
                job = job with { Results = results.ToArray() }; db.Put("job", job.Id, job);
            }
            var status = results.Any(x => x.Status == "failed") ? "failed" : results.Any(x => x.Status == "conflict") ? "conflict" : results.Any(x => x.Status is "empty" or "compatibility-required") ? "blocked" : "completed";
            job = job with { Status = status, Message = status == "completed" ? "동기화를 완료했습니다." : "확인이 필요한 항목이 있습니다.", Results = results.ToArray() };
        }
        catch (Exception e) { job = job with { Status = "failed", Message = Friendly(e) }; }
        db.Put("job", job.Id, job); db.Delete("sync-request", job.Id); return job;
    }
    private async Task RunJob(Job job, string? bindingId, Resolution? resolution)
    {
        await gate.WaitAsync(); try
        {
            if (ActiveLaunch is not null) { db.Put("job", job.Id, job with { Status = "waiting", Message = "에뮬레이터 종료 후 동기화합니다." }); return; }
            await SyncInsideGate(job, bindingId, resolution);
        }
        finally { gate.Release(); }
    }
    public LaunchSession StartLaunch(string? profileId = null)
    {
        lock (launchLock)
        {
            if (!gate.Wait(0)) throw new UserError("설정 변경 또는 동기화가 진행 중입니다. 작업 완료 후 실행하세요.");
            try
            {
                if (ActiveLaunch is not null || launchTask is { IsCompleted: false }) throw new UserError("이미 실행 준비 또는 플레이 중입니다.");
                var profile = Launch(profileId);
                if (db.List<RestoreReservation>("restore-reservation").Any(r => r.InstallationId == profile.InstallationId)) throw new UserError("서버 복원이 예약된 게임은 에뮬레이터를 수동 실행해 초기화한 뒤 종료하고 탐색·연결하세요.");
                var s = Emulators.Installation(db.Settings, profile.InstallationId); if (!File.Exists(s.Executable)) throw new UserError("에뮬레이터 실행 파일을 먼저 설정하세요.");
                if (new[] { "sh", "bash", "cmd", "powershell", "pwsh" }.Contains(Path.GetFileNameWithoutExtension(s.Executable).ToLowerInvariant())) throw new UserError("셸 대신 실제 에뮬레이터 실행 파일을 지정하세요.");
                if (s.Executable.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(s.Executable).Equals("flatpak", StringComparison.OrdinalIgnoreCase)) throw new UserError("AppImage/Flatpak 실행 추적은 아직 검증되지 않았습니다. 추출한 실제 실행 파일을 사용하거나 종료 후 웹에서 동기화하세요.");
                var session = new LaunchSession(Guid.NewGuid().ToString("N"), "preparing", "실행 전 동기화를 준비합니다.", null, null, DateTimeOffset.UtcNow) { InstallationId = profile.InstallationId, LaunchProfileId = profileId }; db.Put("launch", session.Id, session);
                launchTask = Task.Run(() => RunLaunch(session)); return session;
            }
            finally { gate.Release(); }
        }
    }
    private async Task RunLaunch(LaunchSession session)
    {
        Process? process = null; await gate.WaitAsync();
        try
        {
            var job = new Job(Guid.NewGuid().ToString("N"), "sync", "steam-pre", "queued", "실행 전 동기화", DateTimeOffset.UtcNow, []);
            session = session with { JobId = job.Id }; db.Put("launch", session.Id, session);
            job = await SyncInsideGate(job, null, null, Groups(session), true);
            if (job.Status != "completed") { db.Put("launch", session.Id, session with { Status = "blocked", Message = "실행 전 동기화를 해결한 뒤 다시 실행하세요." }); return; }
            var s = Emulators.Installation(db.Settings, session.InstallationId); ProcessGuard.EnsureIdle(db.Settings); var start = new ProcessStartInfo(s.Executable) { UseShellExecute = false, WorkingDirectory = string.IsNullOrWhiteSpace(s.WorkingDirectory) ? Path.GetDirectoryName(s.Executable)! : s.WorkingDirectory };
            foreach (var argument in s.Arguments.Concat(Launch(session.LaunchProfileId).Arguments)) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new UserError("에뮬레이터 프로세스를 시작하지 못했습니다.");
            session = session with { Status = "playing", Message = "에뮬레이터 실행 중입니다.", ProcessId = process.Id, ProcessStartedAt = process.StartTime.ToUniversalTime() }; db.Put("launch", session.Id, session);
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
            job = await SyncInsideGate(job, null, null, Groups(session));
            db.Put("launch", session.Id, session with { Status = job.Status == "completed" ? "completed" : "needs-attention", Message = job.Message, JobId = job.Id });
            foreach (var waiting in db.List<Job>("job").Where(x => x.Status == "waiting").OrderBy(x => x.CreatedAt))
            {
                var request = db.Get<QueuedSyncRequest>("sync-request", waiting.Id);
                if (request is not null) await SyncInsideGate(waiting, request.BindingId, request.Resolution);
                else db.Put("job", waiting.Id, waiting with { Status = "interrupted", Message = "대기 요청 정보가 없어 다시 동기화해야 합니다." });
            }
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
            foreach (var waiting in db.List<Job>("job").Where(j => j.Status == "waiting"))
            {
                db.Put("job", waiting.Id, waiting with { Status = "interrupted", Message = "실행 추적이 중단되어 대기 작업도 보류했습니다. 종료 확인 후 다시 요청하세요." }); db.Delete("sync-request", waiting.Id);
            }
        }
    }
    public static string Friendly(Exception e) => e switch
    {
        UserError => e.Message,
        TaskCanceledException => "요청 시간이 초과되었습니다. 로컬 세이브와 미전송 스냅샷은 보존됩니다.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "HTTPS 인증서 또는 TLS 연결을 확인하지 못했습니다. 서버·리버스 프록시의 인증서 만료일, 도메인, 신뢰 체인과 기기의 날짜·시간을 확인하세요.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "WebDAV 서버의 도메인을 찾지 못했습니다. 서버 주소와 DNS 설정을 확인하세요.",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "WebDAV 서버에 접속하지 못했습니다. 서버 주소·포트, 서버 실행 상태와 네트워크를 확인하세요.",
        HttpRequestException => "서버에 연결하지 못했습니다. 주소·네트워크·인증서를 확인하세요.",
        UnauthorizedAccessException => "파일 접근 권한이 없습니다.",
        IOException => "파일 작업에 실패했습니다. 저장 공간과 파일 잠금, 백업을 확인하세요.",
        _ => "작업을 완료하지 못했습니다. 진단 로그를 확인하세요."
    };
}

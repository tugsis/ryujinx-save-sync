using System.Diagnostics;
namespace RyuSync;

public static class ProcessGuard
{
    public static void EnsureIdle(AppSettings settings)
    {
        var configured = Path.GetFileNameWithoutExtension(settings.Executable);
        foreach (var process in Process.GetProcesses()) using (process)
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains("ryujinx", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(configured) && name.Equals(configured, StringComparison.OrdinalIgnoreCase))) throw new UserError("Ryujinx가 실행 중입니다. 종료 후 동기화하세요.");
            }
            catch (InvalidOperationException) { }
        }
    }
}
public sealed class SyncEngine(StateDatabase db, Snapshots snapshots)
{
    public Action<AppSettings> IdleCheck { get; set; } = ProcessGuard.EnsureIdle;
    private void Idle(AppSettings settings)
    {
        if (db.List<ApplyJournal>("journal").Count > 0) throw new UserError("미완료 파일 적용이 있습니다. 복구 안내를 먼저 확인하세요."); IdleCheck(settings);
    }
    public async Task<SyncOutcome> Sync(Binding binding, AppSettings settings, RevisionRepository remote, Resolution? resolution, CancellationToken ct)
    {
        Idle(settings); var saveFull = Path.GetFullPath(binding.SaveRoot) + Path.DirectorySeparatorChar; var stateFull = Path.GetFullPath(db.Root) + Path.DirectorySeparatorChar;
        if (saveFull.StartsWith(stateFull, StringComparison.OrdinalIgnoreCase) || stateFull.StartsWith(saveFull, StringComparison.OrdinalIgnoreCase)) throw new UserError("앱 상태 폴더와 세이브 폴더가 겹칠 수 없습니다.");
        var content = Discovery.ContentPath(binding); var local = Snapshots.Inspect(content);
        await Task.Delay(250, ct); Idle(settings); if (Snapshots.Inspect(content).Hash != local.Hash) throw new UserError("세이브가 변경 중입니다. 잠시 후 다시 시도하세요.");
        var rows = await remote.ReadAll(binding.RemoteKey, ct); var heads = RevisionRepository.Heads(rows);
        var baseline = db.Get<Baseline>("baseline", binding.Id); if (baseline?.Scope != remote.Scope) baseline = null;
        if (baseline is not null && !rows.Any(r => r.Id == baseline.RevisionId)) throw new UserError("기준 버전이 서버에서 사라졌습니다. 저장소와 이력을 확인하세요.");
        SyncOutcome Conflict() => new(binding.Id, "conflict", "로컬과 서버의 버전이 다릅니다. 사용할 진행 상황을 선택하세요.", local.Hash, heads.Select(x => x.Id).ToArray(), heads);
        void SaveBase(Revision r) => db.Put("baseline", binding.Id, new Baseline(binding.Id, remote.Scope, r.Id, r.ContentHash));
        async Task<Revision> Upload(Snapshot snapshot, string[] parents)
        {
            Idle(settings);
            // Snapshot stays on disk and is registered until a verified remote commit is published.
            var pendingId = Guid.NewGuid().ToString("N"); db.Put("pending", pendingId, new PendingUpload(pendingId, binding, remote.Scope, snapshot, parents));
            var r = await remote.Publish(binding, snapshot, parents, ct); db.Delete("pending", pendingId); return r;
        }
        async Task<SyncOutcome> Published(Revision r)
        {
            SaveBase(r); var latest = RevisionRepository.Heads(await remote.ReadAll(binding.RemoteKey, ct));
            if (latest.Length > 1) return new(binding.Id, "conflict", "동시 업로드가 감지되었습니다. 모든 버전을 보존했습니다.", local.Hash, latest.Select(x => x.Id).ToArray(), latest);
            return new(binding.Id, "uploaded", "세이브를 서버에 업로드했습니다.");
        }
        if (resolution is not null)
        {
            if (resolution.ExpectedLocalHash != local.Hash || !resolution.ExpectedHeads.Order().SequenceEqual(heads.Select(x => x.Id).Order())) throw new UserError("충돌 상태가 바뀌었습니다. 다시 비교한 뒤 선택하세요.");
            if (heads.Length == 0) throw new UserError("해결할 원격 버전이 없습니다.");
            var parents = heads.Select(x => x.Id).ToList();
            if (resolution.Choice == "local")
            {
                var snap = snapshots.Create(content); try { var r = await Upload(snap, parents.ToArray()); var result = await Published(r); Snapshots.CleanSnapshot(snap); return result; } catch { throw; }
            }
            var chosen = heads.SingleOrDefault(x => x.Id == resolution.Choice) ?? throw new UserError("선택한 버전이 더 이상 현재 버전이 아닙니다.");
            var chosenSnapshot = await remote.Fetch(chosen, ct);
            try
            {
                if (local.Files.Count > 0 && local.Hash != chosen.ContentHash)
                {
                    var localSnapshot = snapshots.Create(content);
                    var candidate = await Upload(localSnapshot, baseline is null ? [] : [baseline.RevisionId]); parents.Add(candidate.Id); Snapshots.CleanSnapshot(localSnapshot);
                }
                // Publish the selected content as a merge; the unselected local version remains in history.
                var merged = await Upload(chosenSnapshot, parents.ToArray());
                Idle(settings); snapshots.Apply(binding, chosenSnapshot, local.Hash, () => Idle(settings)); SaveBase(merged);
                var result = await Published(merged); return result.Status == "conflict" ? result : new(binding.Id, "downloaded", "선택한 서버 버전을 적용했습니다. 이전 로컬 버전도 보존했습니다.");
            }
            finally { if (!db.List<PendingUpload>("pending").Any(x => x.Snapshot.ArchivePath == chosenSnapshot.ArchivePath)) Snapshots.CleanSnapshot(chosenSnapshot); }
        }
        if (heads.Length > 1) return Conflict();
        if (heads.Length == 0)
        {
            if (local.Files.Count == 0) return new(binding.Id, "empty", "로컬 세이브가 비어 있습니다. 자동 업로드를 보류했습니다.");
            var snapshot = snapshots.Create(content); var r = await Upload(snapshot, []); var result = await Published(r); Snapshots.CleanSnapshot(snapshot); return result;
        }
        var head = heads[0];
        if (head.ContentHash == local.Hash) { SaveBase(head); return new(binding.Id, "unchanged", "서버와 로컬 세이브가 같습니다."); }
        if (baseline is null && local.Files.Count > 0) return Conflict();
        if (baseline is not null && local.Hash != baseline.ContentHash)
        {
            if (head.Id != baseline.RevisionId) return Conflict();
            if (local.Files.Count == 0) return new(binding.Id, "empty", "세이브가 비었습니다. 자동 삭제 전파를 보류했습니다.");
            var snapshot = snapshots.Create(content); var r = await Upload(snapshot, [baseline.RevisionId]); var result = await Published(r); Snapshots.CleanSnapshot(snapshot); return result;
        }
        var download = await remote.Fetch(head, ct);
        try { Idle(settings); snapshots.Apply(binding, download, local.Hash, () => Idle(settings)); SaveBase(head); return new(binding.Id, "downloaded", "백업 후 서버의 세이브를 적용했습니다."); }
        finally { Snapshots.CleanSnapshot(download); }
    }
}

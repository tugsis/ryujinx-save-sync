using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace RyuSync;

public static class Hashing
{
    public static string Text(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string FileHash(string path) { using var s = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(s)); }
}
public sealed class Snapshots(StateDatabase db)
{
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const int MaxEntries = 20000;
    public static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UserError("세이브 내부의 링크는 지원하지 않습니다: " + Path.GetFileName(path));
    }
    public static void SafeRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)) throw new UserError("안전하지 않은 파일 경로입니다.");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) throw new UserError("지원하지 않는 파일 이름입니다.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || Enumerable.Range(1, 9).Any(i => stem == $"COM{i}" || stem == $"LPT{i}")) throw new UserError("Windows 예약 파일 이름입니다.");
        }
    }
    public static string TreeHash(List<FileEntry> files, List<string> directories) => Hashing.Text(JsonSerializer.Serialize(new { files = files.OrderBy(f => f.Path, StringComparer.Ordinal), directories = directories.Order(StringComparer.Ordinal) }, JsonDefaults.Options));
    public static TreeInfo Inspect(string root, string[]? excludedRootFiles = null)
    {
        if (!Directory.Exists(root)) throw new UserError("세이브 경로가 없습니다."); NoLink(root);
        var files = new List<FileEntry>(); var dirs = new List<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long size = 0;
        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                NoLink(entry); var path = Path.GetRelativePath(root, entry).Replace('\\', '/'); SafeRelative(path);
                if (excludedRootFiles?.Contains(path, StringComparer.OrdinalIgnoreCase) == true)
                {
                    if (Directory.Exists(entry) || !excludedRootFiles.Contains(path, StringComparer.Ordinal)) throw new UserError("관리 파일의 이름 또는 종류가 올바르지 않습니다.");
                    continue;
                }
                if (!names.Add(path)) throw new UserError("대소문자만 다른 파일 이름이 있습니다.");
                if (names.Count > MaxEntries) throw new UserError("세이브 파일 수가 제한을 초과합니다.");
                if (Directory.Exists(entry)) { dirs.Add(path); Walk(entry); }
                else
                {
                    var length = new FileInfo(entry).Length; size = checked(size + length); if (size > MaxBytes) throw new UserError("세이브가 1 GiB 제한을 초과합니다.");
                    files.Add(new(path, length, Hashing.FileHash(entry)));
                }
            }
        }
        Walk(root); return new(TreeHash(files, dirs), files, dirs);
    }
    public Snapshot Create(string content, string[]? excludedRootFiles = null)
    {
        var before = Inspect(content, excludedRootFiles); if (before.Files.Count == 0) throw new UserError("빈 세이브의 자동 업로드는 보류합니다.");
        var temp = Path.Combine(db.Root, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var archive = Path.Combine(temp, "save.zip");
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var dir in before.Directories) { zip.CreateEntry(dir + "/"); }
                foreach (var file in before.Files) zip.CreateEntryFromFile(Path.Combine(content, file.Path), file.Path, CompressionLevel.Optimal);
            }
            if (Inspect(content, excludedRootFiles).Hash != before.Hash) throw new UserError("스냅샷 생성 중 세이브가 변경되었습니다.");
            var verification = Path.Combine(temp, "verify"); Extract(archive, before, verification); Directory.Delete(verification, true);
            return new(archive, before, Hashing.FileHash(archive));
        }
        catch { Directory.Delete(temp, true); throw; }
    }
    public static void CleanSnapshot(Snapshot s) { var dir = Path.GetDirectoryName(s.ArchivePath)!; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    public static void ValidateTree(TreeInfo expected)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        if (expected.Files.Count + expected.Directories.Count > MaxEntries) throw new UserError("Manifest 항목 수 제한 초과입니다.");
        foreach (var d in expected.Directories) { SafeRelative(d); if (!names.Add(d)) throw new UserError("중복 경로가 있습니다."); }
        foreach (var f in expected.Files)
        {
            SafeRelative(f.Path); if (f.Size < 0 || !IsHash(f.Sha256) || !names.Add(f.Path)) throw new UserError("잘못된 파일 Manifest입니다.");
            total = checked(total + f.Size); if (total > MaxBytes) throw new UserError("압축 해제 크기 제한을 초과합니다.");
        }
        if (!IsHash(expected.Hash) || TreeHash(expected.Files, expected.Directories) != expected.Hash) throw new UserError("트리 해시가 일치하지 않습니다.");
    }
    public static bool IsHash(string s) => s.Length == 64 && s.All(c => char.IsAsciiHexDigit(c)) && s == s.ToLowerInvariant();
    public static void Extract(string archive, TreeInfo expected, string output)
    {
        ValidateTree(expected); Directory.CreateDirectory(output);
        using var zip = ZipFile.OpenRead(archive); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedFiles = expected.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var expectedDirs = expected.Directories.ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Count > MaxEntries) throw new UserError("압축 파일 항목 수 제한 초과입니다.");
        foreach (var entry in zip.Entries)
        {
            var dir = entry.FullName.EndsWith('/'); var relative = dir ? entry.FullName[..^1] : entry.FullName; SafeRelative(relative);
            if (!seen.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new UserError("중복 또는 링크 ZIP 항목입니다.");
            var path = Path.Combine(output, relative);
            if (dir) { if (!expectedDirs.Contains(relative)) throw new UserError("Manifest에 없는 디렉터리입니다."); Directory.CreateDirectory(path); continue; }
            if (!expectedFiles.TryGetValue(relative, out var f) || entry.Length != f.Size) throw new UserError("ZIP 파일 목록이 Manifest와 다릅니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var input = entry.Open()) using (var target = new FileStream(path, FileMode.CreateNew))
            {
                var buffer = new byte[81920]; long written = 0; int read;
                while ((read = input.Read(buffer)) > 0) { written += read; if (written > f.Size) throw new UserError("압축 해제 크기가 Manifest와 다릅니다."); target.Write(buffer, 0, read); }
                target.Flush(true); if (written != f.Size) throw new UserError("압축 파일이 잘렸습니다.");
            }
            if (Hashing.FileHash(path) != f.Sha256) throw new UserError("파일 해시가 일치하지 않습니다.");
        }
        if (Inspect(output).Hash != expected.Hash) throw new UserError("압축 해제 결과의 해시가 일치하지 않습니다.");
    }
    private static void CopyTree(string source, string destination)
    {
        NoLink(source); Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            NoLink(path); var dest = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, dest); else File.Copy(path, dest, false);
        }
    }
    public Backup BackupRoot(Binding binding)
    {
        var content = Emulators.ContentPath(binding); var hash = Emulators.Inspect(binding).Hash;
        var fullHash = Inspect(binding.SaveRoot).Hash; var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(db.Root, "backups", id); CopyTree(binding.SaveRoot, path);
        if (Inspect(path).Hash != fullHash || Inspect(binding.SaveRoot).Hash != fullHash) throw new UserError("백업 중 세이브가 변경되었습니다.");
        var active = Path.Combine(path, Path.GetRelativePath(binding.SaveRoot, content));
        if (Inspect(active, Emulators.Metadata(binding)).Hash != hash) throw new UserError("백업 콘텐츠가 일치하지 않습니다.");
        var b = new Backup(id, binding.Id, binding.Title, path, hash, DateTimeOffset.UtcNow); db.Put("backup", id, b); return b;
    }
    public static void ValidatePayload(TreeInfo tree)
    {
        if (tree.Files.Select(f => f.Path).Concat(tree.Directories).Any(p => p.Split('/')[0].Equals(".yuzu_save_size", StringComparison.OrdinalIgnoreCase)))
            throw new UserError("공통 페이로드에 에뮬레이터 관리 파일이 포함되어 있습니다.");
    }
    public void Apply(Binding binding, Snapshot snapshot, string expectedLocalHash, Action ensureIdle)
    {
        ensureIdle(); var content = Emulators.ContentPath(binding);
        if (Emulators.Inspect(binding).Hash != expectedLocalHash) throw new UserError("로컬 세이브가 변경되었습니다. 다시 비교하세요.");
        if (binding.EmulatorKind == "yuzu") ValidatePayload(snapshot.Tree);
        NoLink(binding.SaveRoot); var originalFullHash = Inspect(binding.SaveRoot).Hash;
        var backup = BackupRoot(binding); var id = Guid.NewGuid().ToString("N");
        var staged = binding.SaveRoot + ".ryusync-new-" + id; var previous = binding.SaveRoot + ".ryusync-old-" + id;
        var journalCreated = false;
        try
        {
            if (binding.EmulatorKind == "yuzu")
            {
                Extract(snapshot.ArchivePath, snapshot.Tree, staged);
                foreach (var name in Emulators.Metadata(binding))
                {
                    var metadata = Path.Combine(binding.SaveRoot, name);
                    if (File.Exists(metadata)) File.Copy(metadata, Path.Combine(staged, name), false);
                }
            }
            else
            {
                CopyTree(binding.SaveRoot, staged); var stagedContent = Path.Combine(staged, Path.GetRelativePath(binding.SaveRoot, content));
                Directory.Delete(stagedContent, true); Extract(snapshot.ArchivePath, snapshot.Tree, stagedContent);
            }
            ensureIdle(); if (Inspect(binding.SaveRoot).Hash != originalFullHash) throw new UserError("적용 직전에 세이브가 변경되었습니다.");
            var journal = new ApplyJournal(id, binding.SaveRoot, staged, previous, backup.Path, snapshot.Tree.Hash); db.Put("journal", id, journal); journalCreated = true;
            Directory.Move(binding.SaveRoot, previous); Directory.Move(staged, binding.SaveRoot);
            if (Emulators.Inspect(binding).Hash != snapshot.Tree.Hash) throw new UserError("적용 결과 검증에 실패했습니다.");
            db.Delete("journal", id); journalCreated = false; Directory.Delete(previous, true);
        }
        catch
        {
            if (!Directory.Exists(binding.SaveRoot) && Directory.Exists(previous)) Directory.Move(previous, binding.SaveRoot);
            throw;
        }
        finally { if (!journalCreated && Directory.Exists(staged)) Directory.Delete(staged, true); }
    }
    public List<string> Recover()
    {
        var notes = new List<string>();
        foreach (var j in db.List<ApplyJournal>("journal"))
        {
            if (!Directory.Exists(j.Target) && Directory.Exists(j.Previous)) { Directory.Move(j.Previous, j.Target); notes.Add("중단된 적용에서 기존 세이브를 복구했습니다."); }
            if (!Directory.Exists(j.Target)) { notes.Add("복구할 세이브가 없습니다. 수동 복구가 필요합니다: " + j.Id); continue; }
            // Never remove unknown target content or remaining copies during startup recovery.
            notes.Add("미완료 적용을 발견했습니다. 백업과 임시 복사본을 보존했습니다: " + j.Id);
        }
        return notes;
    }
}

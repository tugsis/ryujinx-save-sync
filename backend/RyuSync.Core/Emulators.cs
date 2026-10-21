using System.Text.RegularExpressions;
namespace RyuSync;

public interface IEmulatorAdapter
{
    ScanResult Scan(EmulatorInstallation installation);
    string ContentPath(Binding binding);
    string[] LocalMetadata { get; }
}

public sealed class RyujinxAdapter : IEmulatorAdapter
{
    public string[] LocalMetadata => [];
    public ScanResult Scan(EmulatorInstallation installation) => Discovery.Scan(Emulators.CanonicalDirectory(installation.DataRoot));
    public string ContentPath(Binding binding) => Discovery.ContentPath(binding);
}

public sealed class YuzuAdapter : IEmulatorAdapter
{
    public string[] LocalMetadata => [".yuzu_save_size"];
    public string ContentPath(Binding binding)
    {
        if (!Directory.Exists(binding.SaveRoot)) throw new UserError("Yuzu에서 이 프로필로 게임을 실행하고 저장한 뒤 종료하세요.");
        Snapshots.NoLink(binding.SaveRoot); return binding.SaveRoot;
    }
    public ScanResult Scan(EmulatorInstallation installation)
    {
        var root = Emulators.CanonicalDirectory(string.IsNullOrWhiteSpace(installation.NandRoot) ? Path.Combine(installation.DataRoot, "nand") : installation.NandRoot);
        var save = Path.Combine(root, "user", "save"); var accountRoot = Path.Combine(save, "0000000000000000");
        var warnings = new List<string>(); var saves = new List<DiscoveredSave>();
        if (Directory.Exists(Path.Combine(save, "account"))) warnings.Add("account 레이아웃은 이 빌드에서 검증되지 않아 제외했습니다. 기존 기본 레이아웃만 지원합니다.");
        if (!Directory.Exists(accountRoot)) return new(root, saves, [.. warnings, "Yuzu 사용자 세이브가 없습니다. NAND 경로를 확인하고 게임을 한 번 실행·저장·종료하세요."]);
        Emulators.EnsureInside(root, accountRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var visited = 0;
        foreach (var user in Directory.EnumerateDirectories(accountRoot))
        {
            if (++visited > Snapshots.MaxEntries) throw new UserError("세이브 탐색 항목 제한을 초과했습니다.");
            Snapshots.NoLink(user); var uid = Path.GetFileName(user);
            if (!Regex.IsMatch(uid, "^[a-fA-F0-9]{32}$") || uid.All(c => c == '0')) { warnings.Add("사용자 ID를 확인할 수 없는 저장소는 제외했습니다."); continue; }
            foreach (var game in Directory.EnumerateDirectories(user))
            {
                if (++visited > Snapshots.MaxEntries) throw new UserError("세이브 탐색 항목 제한을 초과했습니다.");
                Snapshots.NoLink(game); var title = Path.GetFileName(game);
                if (!Regex.IsMatch(title, "^[a-fA-F0-9]{16}$") || title.All(c => c == '0')) continue;
                if (saves.Count >= Snapshots.MaxEntries) throw new UserError("세이브 탐색 항목 제한을 초과했습니다.");
                if (!seen.Add(uid.ToLowerInvariant() + ":" + title.ToUpperInvariant())) throw new UserError("대소문자만 다른 Yuzu 세이브가 있습니다.");
                var tree = Snapshots.Inspect(game, LocalMetadata);
                var marker = Path.Combine(game, ".yuzu_save_size");
                if (File.Exists(marker) && new FileInfo(marker).Length != 16) { warnings.Add($"{title}: .yuzu_save_size 형식이 달라 제외했습니다."); continue; }
                var status = tree.Files.Count > 0 || File.Exists(marker) ? "ready" : "initialization-required";
                saves.Add(new(Hashing.Text(game)[..32], title.ToUpperInvariant(), title.ToUpperInvariant(), uid.ToLowerInvariant(), uid, "account", game, game, status));
            }
        }
        return new(root, saves, warnings);
    }
}

public static class Emulators
{
    public static IEmulatorAdapter Adapter(string kind) => kind switch
    {
        "ryujinx" => new RyujinxAdapter(), "yuzu" => new YuzuAdapter(),
        _ => throw new UserError("지원하지 않는 에뮬레이터입니다.")
    };
    public static string ContentPath(Binding binding) => Adapter(binding.EmulatorKind).ContentPath(binding);
    public static string[] Metadata(Binding binding) => Adapter(binding.EmulatorKind).LocalMetadata;
    public static TreeInfo Inspect(Binding binding) => Snapshots.Inspect(ContentPath(binding), Metadata(binding));
    public static EmulatorInstallation[] Installations(AppSettings settings) =>
        [new() { Id = "legacy", Name = "기본 Ryujinx", Kind = "ryujinx", DataRoot = settings.DataRoot, Executable = settings.Executable, WorkingDirectory = settings.WorkingDirectory, Arguments = settings.Arguments }, .. settings.Installations];
    public static EmulatorInstallation Installation(AppSettings settings, string id) => Installations(settings).SingleOrDefault(x => x.Id == id) ?? throw new UserError("에뮬레이터 설치본이 없습니다.");
    public static ScanResult Scan(EmulatorInstallation installation)
    {
        var result = Adapter(installation.Kind).Scan(installation);
        return result with { Saves = result.Saves.Select(s => s with
        {
            Id = installation.Id == "legacy" ? s.Id : Hashing.Text(installation.Id + ":" + s.Id)[..32],
            InstallationId = installation.Id, EmulatorKind = installation.Kind
        }).ToList() };
    }
    public static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new UserError("데이터/NAND 폴더가 없습니다.");
        var full = Path.GetFullPath(path); var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static void EnsureInside(string root, string path, bool allowMissing = false)
    {
        var relative = Path.GetRelativePath(root, path);
        Snapshots.SafeRelative(relative.Replace('\\', '/')); var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (allowMissing && !File.Exists(current) && !Directory.Exists(current)) break;
            Snapshots.NoLink(current);
        }
    }
    public static string[] Candidates(string kind, string executable = "")
    {
        if (kind == "ryujinx") return Discovery.Candidates();
        _ = Adapter(kind); var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var portable = string.IsNullOrWhiteSpace(executable) ? "" : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "user");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuzu"), Path.Combine(xdg ?? Path.Combine(home, ".local", "share"), "yuzu"), Path.Combine(home, "Emulation", "storage", "yuzu"), portable,
            Path.Combine(home, ".var", "app", "org.yuzu_emu.yuzu", "data", "yuzu") }.Where(Directory.Exists).Distinct().ToArray();
    }
}

public static class Compatibility
{
    public static bool Required(Binding binding, IEnumerable<Revision> rows) => rows.Any(r => r.SourceEmulator != binding.EmulatorKind);
    public static string Signature(Binding binding, IEnumerable<Revision> rows) => Hashing.Text(binding.Id + "|" + binding.EmulatorKind + "|" + binding.EmulatorBuild + "|" + binding.GameVersion + "|" +
        string.Join(";", rows.Select(r => r.SourceEmulator + ":" + r.SourceBuild + ":" + r.GameVersion)
            .Append(binding.EmulatorKind + ":" + binding.EmulatorBuild + ":" + binding.GameVersion).Distinct().Order(StringComparer.Ordinal)));
    public static string State(StateDatabase db, Binding binding, IEnumerable<Revision> rows)
    {
        if (!Required(binding, rows)) return "same-emulator";
        var approval = db.Get<CompatibilityApproval>("compatibility", binding.Id);
        return approval?.Signature == Signature(binding, rows) ? approval.State : "unconfirmed";
    }
}

using System.Text;
using System.Text.RegularExpressions;
namespace RyuSync;

public record GameTitle(string TitleId, string Title, string Source)
{
    public string? ThumbnailUrl { get; init; }
}
public static class GameTitles
{
    public static string NormalizeId(string id)
    {
        if (!Regex.IsMatch(id, "^[a-fA-F0-9]{16}$")) throw new UserError("16자리 게임 Title ID를 지정하세요.");
        return id.ToUpperInvariant();
    }
    public static string ValidateName(string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 200 || title.Any(char.IsControl)) throw new UserError("게임 이름은 제어 문자 없이 1~200자로 입력하세요.");
        return title;
    }
    private static int Priority(string source) => source switch { "web" => 95, "yuzu-cache" => 90, "emulator" => 80, "server" => 50, _ => 0 };
    public static void Remember(StateDatabase db, string id, string title, string source)
    {
        id = NormalizeId(id);
        if (title.Equals(id, StringComparison.OrdinalIgnoreCase)) return;
        try { title = ValidateName(title); } catch (UserError) { return; }
        var previous = db.Get<GameTitle>("game-title-auto", id);
        if (previous is null || Priority(source) >= Priority(previous.Source))
            if (previous is null || previous.Title != title || previous.Source != source) db.Put("game-title-auto", id, new GameTitle(id, title, source));
    }
    public static GameTitle Resolve(StateDatabase db, string id, string fallback, string source = "emulator")
    {
        var result = ResolveName(db, id, fallback, source);
        var image = db.Get<WebGameInfo>("game-web", result.TitleId);
        return result with { ThumbnailUrl = image?.IconBase64 is null ? null : $"/api/game-titles/{result.TitleId}/thumbnail?v={image.FetchedAt.UtcTicks}" };
    }
    private static GameTitle ResolveName(StateDatabase db, string id, string fallback, string source)
    {
        id = NormalizeId(id);
        var manual = db.Get<GameTitle>("game-title", id);
        if (manual is not null) return manual;
        var automatic = db.Get<GameTitle>("game-title-auto", id);
        if (!string.IsNullOrWhiteSpace(fallback) && !fallback.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                fallback = ValidateName(fallback);
                if (automatic is null || Priority(source) > Priority(automatic.Source)) return new(id, fallback, source);
            }
            catch (UserError) { }
        }
        return automatic ?? new(id, id, "title-id");
    }
    public static string? YuzuCacheName(EmulatorInstallation installation, string titleId)
    {
        titleId = NormalizeId(titleId);
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(installation.GameListCacheRoot)) folders.Add(installation.GameListCacheRoot);
        if (!string.IsNullOrWhiteSpace(installation.DataRoot)) folders.Add(Path.Combine(installation.DataRoot, "cache", "game_list"));
        if (!OperatingSystem.IsWindows() && installation.DataRoot.Length > 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var defaultData = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "yuzu");
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.DataRoot)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultData)))
                folders.Add(Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache"), "yuzu", "game_list"));
        }
        foreach (var folder in folders.Distinct())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                var root = Emulators.CanonicalDirectory(folder);
                // Probe only the exact title, not ROMs, keys or unrelated cache files.
                foreach (var id in new[] { titleId, titleId.ToLowerInvariant() }.Distinct())
                {
                    var file = Path.Combine(root, id + ".appname.txt");
                    if (!File.Exists(file)) continue;
                    Snapshots.NoLink(file);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (input.Length is < 1 or > 4096) continue;
                    var bytes = new byte[input.Length]; input.ReadExactly(bytes);
                    var name = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Trim();
                    try { return ValidateName(name); } catch (UserError) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserError or DecoderFallbackException) { }
        }
        return null;
    }
}

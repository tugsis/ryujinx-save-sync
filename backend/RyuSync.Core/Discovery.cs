using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibHac.Fs;
using LibHac.FsSrv;
using Path = System.IO.Path;
namespace RyuSync;

public static class Discovery
{
    public static string[] Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Ryujinx"),
            Path.Combine(home,".config","Ryujinx"),Path.Combine(AppContext.BaseDirectory,"portable"),
            Path.Combine(home,"Applications","publish","portable"),Path.Combine(home,"Emulation","storage","ryujinx")}
            .Where(Directory.Exists).Distinct().ToArray();
    }
    // IMKV envelope is parsed explicitly; payload layouts use the pinned LibHac structs.
    // The emulator's filesystem is never mounted or initialized by this reader.
    public static List<(SaveDataAttribute Attribute, SaveDataIndexerValue Value)> ReadIndex(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        if (bytes.Length < 12 || reader.ReadUInt32() != 0x564B4D49 || reader.ReadUInt32() != 0) throw new UserError("지원하지 않거나 손상된 IMKV 헤더입니다.");
        var count = reader.ReadUInt32(); if (count > 100000) throw new UserError("세이브 인덱스 항목 수가 비정상입니다.");
        var result = new List<(SaveDataAttribute, SaveDataIndexerValue)>();
        for (uint i = 0; i < count; i++)
        {
            if (stream.Length - stream.Position < 12 || reader.ReadUInt32() != 0x4E454D49) throw new UserError("손상된 IMEN 항목입니다.");
            var keySize = reader.ReadUInt32(); var valSize = reader.ReadUInt32();
            if (keySize != 64 || valSize != 64 || stream.Length - stream.Position < 128) throw new UserError("지원하지 않는 세이브 인덱스 형식입니다.");
            var attr = MemoryMarshal.Read<SaveDataAttribute>(reader.ReadBytes(64));
            var val = MemoryMarshal.Read<SaveDataIndexerValue>(reader.ReadBytes(64));
            result.Add((attr, val));
        }
        if (stream.Position != stream.Length) throw new UserError("세이브 인덱스 끝에 알 수 없는 데이터가 있습니다.");
        return result;
    }
    public static ScanResult Scan(string root)
    {
        root = Path.GetFullPath(root); if (!Directory.Exists(root)) throw new UserError("Ryujinx 데이터 폴더가 없습니다.");
        // A user-selected data root may resolve to an EmuDeck symlink. Never follow links inside save contents.
        var warnings = new List<string>(); var saves = new List<DiscoveredSave>();
        var indexRoot = Path.Combine(root, "bis", "system", "save", "8000000000000000");
        var index = new[] { "0", "1" }.Select(x => Path.Combine(indexRoot, x, "imkvdb.arc")).FirstOrDefault(File.Exists);
        if (index is null) return new(root, saves, ["세이브 인덱스를 찾지 못했습니다. 설치본의 데이터 폴더를 확인하거나 게임을 한 번 실행하세요."]);
        Emulators.EnsureInside(root, index);
        var before = File.ReadAllBytes(index); var entries = ReadIndex(before);
        foreach (var (attr, val) in entries)
        {
            if (attr.Type != SaveDataType.Account) continue;
            if (val.State != SaveDataState.Normal) { warnings.Add($"{val.SaveDataId:x16}: 세이브 상태가 정상이 아니어서 제외했습니다."); continue; }
            if (val.SpaceId != SaveDataSpaceId.User) continue;
            var title = attr.ProgramId.Value.ToString("X16"); var saveId = val.SaveDataId.ToString("x16");
            var userBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in attr.UserId, 1));
            var userId = BinaryPrimitives.ReadUInt64LittleEndian(userBytes[8..]).ToString("x16") + BinaryPrimitives.ReadUInt64LittleEndian(userBytes).ToString("x16");
            var saveRoot = Path.Combine(root, "bis", "user", "save", saveId);
            Emulators.EnsureInside(root, saveRoot, true);
            var content = Directory.Exists(Path.Combine(saveRoot, "0")) ? Path.Combine(saveRoot, "0") : Path.Combine(saveRoot, "1");
            var status = Directory.Exists(content) ? "ready" : "initialization-required";
            var name = title;
            var gameDir = Path.Combine(root, "games");
            var game = Directory.Exists(gameDir) ? Directory.EnumerateDirectories(gameDir).FirstOrDefault(x => Path.GetFileName(x).Equals(title, StringComparison.OrdinalIgnoreCase)) : null;
            var metadata = game is null ? null : Path.Combine(game, "gui", "metadata.json");
            if (metadata is not null && File.Exists(metadata)) try
                { using var doc = JsonDocument.Parse(File.ReadAllText(metadata)); name = Property(doc.RootElement, "title")?.GetString() ?? title; }
                catch (JsonException) { warnings.Add($"{title}: 게임 이름 메타데이터를 읽지 못했습니다."); }
            var profile = ProfileName(root, userId);
            saves.Add(new(Hashing.Text(saveRoot)[..32], title, name, userId, profile, saveId, saveRoot, content, status));
        }
        if (!before.SequenceEqual(File.ReadAllBytes(index))) throw new UserError("탐색 중 인덱스가 변경되었습니다. Ryujinx를 종료하고 다시 탐색하세요.");
        return new(root, saves, warnings);
    }
    private static JsonElement? Property(JsonElement e, string name) => e.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
    private static string ProfileName(string root, string user)
    {
        foreach (var filename in new[] { "Profiles.json", "profiles.json" })
        {
            var path = Path.Combine(root, "system", filename); if (!File.Exists(path)) continue;
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(path)); var profiles = Property(d.RootElement, "profiles");
                if (profiles is null || profiles.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in profiles.Value.EnumerateArray())
                {
                    var id = Property(p, "user_id")?.ToString().Replace("-", "");
                    if (id?.Equals(user, StringComparison.OrdinalIgnoreCase) == true) return Property(p, "name")?.GetString() ?? user;
                }
            }
            catch (JsonException) { }
        }
        return user;
    }
    public static string ContentPath(Binding b)
    {
        var path = Path.Combine(b.SaveRoot, "0"); if (!Directory.Exists(path)) path = Path.Combine(b.SaveRoot, "1");
        if (!Directory.Exists(path)) throw new UserError("로컬 세이브가 없습니다. 게임을 한 번 실행하고 다시 탐색하세요.");
        return path;
    }
}

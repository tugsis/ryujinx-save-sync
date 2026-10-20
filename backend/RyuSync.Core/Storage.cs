using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
namespace RyuSync;

public interface IObjectStore : IDisposable
{
    Task EnsureDirectory(string path, CancellationToken ct = default);
    Task<string[]> List(string path, CancellationToken ct = default);
    Task<byte[]> Read(string path, int maxBytes, CancellationToken ct = default);
    Task Download(string path, string destination, long maxBytes, CancellationToken ct = default);
    Task Upload(string path, string source, CancellationToken ct = default);
    Task Write(string path, byte[] bytes, CancellationToken ct = default);
    Task Delete(string path, CancellationToken ct = default);
}
public sealed class WebDavStore : IObjectStore
{
    private readonly HttpClient client;
    private readonly Uri root;
    public WebDavStore(AppSettings settings, string password)
    {
        if (!Uri.TryCreate(settings.WebDavUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
           uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new UserError("유효한 WebDAV URL을 입력하세요. 계정은 주소에 포함하지 마세요.");
        if (uri.Scheme == "http" && !settings.AllowInsecureHttp) throw new UserError("HTTP 연결은 허용 설정이 필요합니다. HTTPS 사용을 권장합니다.");
        root = uri; client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.WebDavUser + ":" + password)));
    }
    private Uri Url(string path) => new(root, string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + (path.EndsWith('/') ? "/" : ""));
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UserError("WebDAV 인증 또는 권한을 확인하세요.");
        if (!response.IsSuccessStatusCode) throw new UserError($"WebDAV 요청 실패: HTTP {(int)response.StatusCode}. 리디렉션 주소라면 최종 WebDAV 주소를 입력하세요.");
    }
    public async Task EnsureDirectory(string path, CancellationToken ct = default)
    {
        var current = ""; foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += part + "/"; using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Url(current));
            using var response = await client.SendAsync(request, ct); if (response.StatusCode != HttpStatusCode.MethodNotAllowed) Check(response);
        }
    }
    public async Task<string[]> List(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), Url(path.TrimEnd('/') + "/")); request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return []; Check(response);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var text = new MemoryStream();
        await CopyLimited(input, text, 16 * 1024 * 1024, ct); text.Position = 0;
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader); XNamespace d = "DAV:"; var prefix = Url(path.TrimEnd('/') + "/"); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in document.Descendants(d + "response"))
        {
            var href = r.Element(d + "href")?.Value; if (href is null || !Uri.TryCreate(prefix, href, out var target) || target.Authority != prefix.Authority) continue;
            if (!target.AbsolutePath.StartsWith(prefix.AbsolutePath, StringComparison.Ordinal)) continue;
            var tail = target.AbsolutePath[prefix.AbsolutePath.Length..].TrimEnd('/'); if (tail.Length == 0 || tail.Contains('/')) continue;
            var name = Uri.UnescapeDataString(tail); if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new UserError("WebDAV 목록에 잘못된 경로가 있습니다.");
            names.Add(name); if (names.Count > 10000) throw new UserError("원격 항목 수가 제한을 초과합니다.");
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    public async Task<byte[]> Read(string path, int maxBytes, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, ct); Check(response);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); await CopyLimited(input, output, maxBytes, ct); return output.ToArray();
    }
    public async Task Download(string path, string destination, long maxBytes, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, ct); Check(response);
        await using var input = await response.Content.ReadAsStreamAsync(ct); await using var output = new FileStream(destination, FileMode.CreateNew);
        await CopyLimited(input, output, maxBytes, ct); output.Flush(true);
    }
    public async Task Upload(string path, string source, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(source); using var request = new HttpRequestMessage(HttpMethod.Put, Url(path)); request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); request.Content = new StreamContent(stream);
        using var response = await client.SendAsync(request, ct); Check(response);
    }
    public async Task Write(string path, byte[] bytes, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(path)); request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any); request.Content = new ByteArrayContent(bytes);
        using var response = await client.SendAsync(request, ct); Check(response);
    }
    public async Task Delete(string path, CancellationToken ct = default) { using var response = await client.DeleteAsync(Url(path), ct); if (response.StatusCode != HttpStatusCode.NotFound) Check(response); }
    internal static async Task CopyLimited(Stream input, Stream output, long max, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int size; while ((size = await input.ReadAsync(buffer, ct)) > 0) { total += size; if (total > max) throw new UserError("원격 파일의 크기 제한을 초과했습니다."); await output.WriteAsync(buffer.AsMemory(0, size), ct); }
    }
    public void Dispose() => client.Dispose();
}
public sealed class RevisionRepository(IObjectStore store, AppSettings settings, string tempRoot)
{
    public string Prefix => "ryusync/" + settings.Workspace;
    private string Base(string key) => Prefix + "/saves/" + key;
    public string Scope => Hashing.Text(settings.WebDavUrl.TrimEnd('/') + "|" + settings.WebDavUser + "|" + settings.Workspace);
    public static bool IsId(string value) => Guid.TryParseExact(value, "N", out _);
    public async Task<List<Revision>> ReadAll(string key, CancellationToken ct)
    {
        if (key.Length != 32 || !key.All(char.IsAsciiHexDigit)) throw new UserError("잘못된 세이브 키입니다.");
        var names = await store.List(Base(key) + "/commits", ct); var results = new List<Revision>();
        foreach (var name in names)
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal) || !IsId(name[..^5])) throw new UserError("원격 이력에 알 수 없는 파일이 있습니다.");
            var bytes = await store.Read(Base(key) + "/commits/" + name, 8 * 1024 * 1024, ct);
            Revision r; try { r = JsonSerializer.Deserialize<Revision>(bytes, JsonDefaults.Options) ?? throw new JsonException(); } catch (JsonException) { throw new UserError("미완료 또는 손상된 원격 버전입니다. 재시도 전에 서버를 확인하세요."); }
            if (r.SchemaVersion != 1 || r.Id != name[..^5] || r.SaveKey != key || !IsId(r.Id) || r.Parents.Distinct().Count() != r.Parents.Length || r.Parents.Any(x => !IsId(x)) || !Snapshots.IsHash(r.ArchiveHash)) throw new UserError("원격 버전 형식이 올바르지 않습니다.");
            if (r.TitleId.Length != 16 || !r.TitleId.All(char.IsAsciiHexDigit) || Hashing.Text(r.TitleId.ToLowerInvariant() + ":" + r.Slot)[..32] != key) throw new UserError("게임 식별 정보가 일치하지 않습니다.");
            Snapshots.ValidateTree(new(r.ContentHash, r.Files, r.Directories)); results.Add(r);
        }
        ValidateGraph(results); return results;
    }
    public static void ValidateGraph(List<Revision> rows)
    {
        var map = rows.ToDictionary(x => x.Id);
        var remaining = rows.ToDictionary(x => x.Id, x => x.Parents.Length);
        var children = rows.ToDictionary(x => x.Id, _ => new List<string>());
        foreach (var row in rows) foreach (var parent in row.Parents)
        {
            if (!map.ContainsKey(parent)) throw new UserError("원격 이력의 부모 버전이 없습니다. 다시 조회하세요.");
            children[parent].Add(row.Id);
        }
        var queue = new Queue<string>(remaining.Where(x => x.Value == 0).Select(x => x.Key)); var visited = 0;
        while (queue.TryDequeue(out var id)) { visited++; foreach (var child in children[id]) if (--remaining[child] == 0) queue.Enqueue(child); }
        if (visited != rows.Count) throw new UserError("원격 버전 이력에 순환이 있습니다.");
    }
    public static Revision[] Heads(List<Revision> rows) { var parents = rows.SelectMany(x => x.Parents).ToHashSet(); return rows.Where(x => !parents.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(); }
    public async Task<Revision> Publish(Binding binding, Snapshot snapshot, string[] parents, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N"); var path = Base(binding.RemoteKey); await store.EnsureDirectory(path + "/objects", ct); await store.EnsureDirectory(path + "/commits", ct);
        await store.Upload(path + "/objects/" + id + ".zip", snapshot.ArchivePath, ct);
        Directory.CreateDirectory(tempRoot); var check = Path.Combine(tempRoot, id + ".verify");
        try { await store.Download(path + "/objects/" + id + ".zip", check, Snapshots.MaxBytes + 64 * 1024 * 1024, ct); if (Hashing.FileHash(check) != snapshot.ArchiveHash) throw new UserError("업로드한 스냅샷의 해시가 다릅니다."); } finally { if (File.Exists(check)) File.Delete(check); }
        var row = new Revision { Id = id, Parents = parents.Distinct().ToArray(), SaveKey = binding.RemoteKey, TitleId = binding.TitleId, Title = binding.Title, Slot = binding.Slot, DeviceId = settings.DeviceId, DeviceName = settings.DeviceName, CreatedAt = DateTimeOffset.UtcNow, ContentHash = snapshot.Tree.Hash, ArchiveHash = snapshot.ArchiveHash, Files = snapshot.Tree.Files, Directories = snapshot.Tree.Directories };
        await store.Write(path + "/commits/" + id + ".json", JsonSerializer.SerializeToUtf8Bytes(row, JsonDefaults.Options), ct); return row;
    }
    public async Task<Snapshot> Fetch(Revision r, CancellationToken ct)
    {
        var temp = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); var zip = Path.Combine(temp, "save.zip");
        try
        {
            await store.Download(Base(r.SaveKey) + "/objects/" + r.Id + ".zip", zip, Snapshots.MaxBytes + 64 * 1024 * 1024, ct);
            if (Hashing.FileHash(zip) != r.ArchiveHash) throw new UserError("다운로드한 아카이브의 해시가 다릅니다.");
            var tree = new TreeInfo(r.ContentHash, r.Files, r.Directories); var verify = Path.Combine(temp, "verify"); Snapshots.Extract(zip, tree, verify); Directory.Delete(verify, true); return new(zip, tree, r.ArchiveHash);
        }
        catch { Directory.Delete(temp, true); throw; }
    }
    public async Task<Revision[]> Catalog(CancellationToken ct)
    {
        var all = new List<Revision>(); foreach (var key in await store.List(Prefix + "/saves", ct)) all.AddRange(Heads(await ReadAll(key, ct))); return all.ToArray();
    }
}

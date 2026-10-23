using System.Text;
using System.Text.RegularExpressions;
namespace RyuSync;

// These records contain references and status only. Never add a secret to persisted state.
public record CredentialEntry(string Id, string ConnectionId, string Description, string Phase, string Provider);
public record CredentialDocument(string Namespace, CredentialEntry[] Entries)
{
    public int Version { get; init; } = 1;
}
public record CredentialStatus(string ConnectionId, string Provider, string StoreState, bool Saved, bool Ready,
    string Source, string Verification, bool SessionLocked, bool EnvironmentConfigured, bool CleanupPending);
public record RetiredCredential(string Id, string Description, bool CleanupPending);

public sealed class Credentials
{
    private readonly StateDatabase db;
    private readonly ICredentialStore store;
    private readonly ICredentialStore fileStore;
    private bool Supported(string provider) => provider == store.Provider || provider == fileStore.Provider;
    private ICredentialStore StoreFor(CredentialEntry entry) => entry.Provider == fileStore.Provider ? fileStore : store;
    private string SourceFor(CredentialEntry entry) => entry.Provider == fileStore.Provider ? "file" : "os-store";
    private readonly bool environmentConfigured;
    private string storeState = "unknown";
    private CredentialDocument document;
    private sealed record Runtime(string ConnectionId, string? Password, string Source, string Verification, bool Locked)
    {
        public Guid Token { get; } = Guid.NewGuid();
        public override string ToString() => Source;
    }
    private Runtime runtime;
    public Credentials(StateDatabase db, ICredentialStore store, string? environmentPassword = null)
    {
        this.db = db; this.store = store; fileStore = new PlaintextCredentialStore(db.Root);
        document = db.Get<CredentialDocument>("credentials", "local") ?? new(Guid.NewGuid().ToString("N"), []);
        ValidateDocument(document);
        db.Put("credentials", "local", document);
        environmentConfigured = !string.IsNullOrEmpty(environmentPassword);
        string connection;
        try { connection = ConnectionId(db.Settings); } catch (UserError) { connection = ""; }
        runtime = new(connection, environmentConfigured && connection.Length > 0 ? environmentPassword : null,
            environmentConfigured && connection.Length > 0 ? "environment" : "none", "unknown", false);
        if (environmentConfigured) ValidatePassword(environmentPassword!);
    }
    private static void ValidateDocument(CredentialDocument value)
    {
        if (value.Version != 1 || !Regex.IsMatch(value.Namespace, "^[a-f0-9]{32}$") || value.Entries.Any(e => !Regex.IsMatch(e.Id, "^[a-f0-9]{32}$") || e.Phase is not ("active" or "pending" or "deleting")) ||
            value.Entries.Select(e => e.Id).Distinct().Count() != value.Entries.Length ||
            value.Entries.Where(e => e.Phase == "active").GroupBy(e => (e.ConnectionId, e.Provider)).Any(g => g.Count() > 1))
            throw new UserError("비밀번호 보관 참조 정보가 손상되었습니다. 상태 폴더 백업을 확인하세요.");
    }
    public static string ConnectionId(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WebDavUrl)) return "";
        if (!Uri.TryCreate(settings.WebDavUrl.TrimEnd('/') + "/", UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") ||
            url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
            throw new UserError("유효한 WebDAV 주소를 지정하세요. 주소에 계정·쿼리·fragment를 포함하지 마세요.");
        // JSON framing avoids ambiguous separator characters in usernames or URLs.
        return Hashing.Text(System.Text.Json.JsonSerializer.Serialize(new[] { url.AbsoluteUri, settings.WebDavUser }));
    }
    public static void ValidatePassword(string value)
    {
        const string message = "비밀번호는 비어 있지 않은 UTF-8 4 KiB 이하 문자열이어야 합니다. NUL 문자는 사용할 수 없습니다.";
        if (string.IsNullOrEmpty(value) || value.Contains('\0'))
            throw new UserError(message);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > 4096) throw new UserError(message);
        }
        catch (EncoderFallbackException) { throw new UserError(message); }
    }
    private string Key(CredentialEntry entry) => "RyuSync/WebDAV/" + document.Namespace + "/" + entry.Id;
    private CredentialEntry? Active(string connection) => document.Entries.SingleOrDefault(e => e.ConnectionId == connection && e.Phase == "active" && Supported(e.Provider));
    private void Save(CredentialDocument value) { db.Put("credentials", "local", value); document = value; }
    public CredentialStatus Status()
    {
        var current = Volatile.Read(ref runtime); var entries = document.Entries;
        return new(current.ConnectionId, Active(current.ConnectionId)?.Provider ?? store.Provider, storeState, Active(current.ConnectionId) is not null,
            current.Password is not null && !current.Locked, current.Source, current.Verification, current.Locked,
            environmentConfigured, entries.Any(e => e.Phase is "pending" or "deleting"));
    }
    public RetiredCredential[] Retired() => document.Entries
        .Where(e => e.ConnectionId != runtime.ConnectionId || e.Phase != "active" || !Supported(e.Provider))
        .Select(e => new RetiredCredential(e.Id, e.Description + " · " + e.Provider, e.Phase is "pending" or "deleting")).ToArray();
    public async Task Initialize()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            storeState = await (Active(runtime.ConnectionId) is { } active ? StoreFor(active) : store).Probe(timeout.Token);
            if (runtime.Password is null) await Load(false, timeout.Token);
            await Cleanup(false, timeout.Token);
        }
        catch (Exception e) when (e is CredentialStoreError or OperationCanceledException) { storeState = State(e); }
    }
    public async Task Probe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { storeState = await (Active(runtime.ConnectionId) is { } active ? StoreFor(active) : store).Probe(timeout.Token); }
        catch (Exception e) when (e is CredentialStoreError or OperationCanceledException) { storeState = State(e); }
    }
    public void ChangeConnection(AppSettings settings, bool force = false)
    {
        var id = ConnectionId(settings);
        if (force || id != runtime.ConnectionId) runtime = new(id, null, "none", "unknown", false);
        // Existing entries for old connections are retained for explicit deletion. No credentials
        // (including startup environment variables) are copied to a changed connection.
    }
    public void Expect(string connection, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrEmpty(connection)) || connection != runtime.ConnectionId || connection != ConnectionId(db.Settings))
            throw new UserError("저장소 설정이 바뀌었습니다. 설정을 저장하고 다시 시도하세요.");
    }
    public void UseSession(string? password)
    {
        if (password is null) { Lock(); return; }
        ValidatePassword(password);
        if (runtime.ConnectionId.Length == 0) throw new UserError("WebDAV 서버 주소를 먼저 저장하세요.");
        runtime = new(runtime.ConnectionId, password, "session", "unknown", false);
    }
    public void Lock() => runtime = new(runtime.ConnectionId, null, "none", "unknown", true);
    public async Task Unlock(string expected)
    {
        Expect(expected);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await Load(true, timeout.Token); }
        catch (Exception e) when (e is CredentialStoreError or OperationCanceledException) { storeState = State(e); throw StoreFailure(); }
    }
    private async Task Load(bool prompt, CancellationToken ct)
    {
        if (!prompt && runtime.Locked) return;
        var entry = Active(runtime.ConnectionId);
        if (entry is null)
        {
            if (prompt) throw new UserError("현재 연결에 저장된 비밀번호가 없습니다.");
            return;
        }
        var result = await StoreFor(entry).Read(Key(entry), prompt, ct); storeState = result.State;
        if (result.State == "missing") Save(document with { Entries = document.Entries.Where(e => e.Id != entry.Id).ToArray() });
        if (result.State != "available" || result.Secret is null)
        {
            if (prompt) throw StoreFailure();
            return;
        }
        try { ValidatePassword(result.Secret); }
        catch (UserError)
        {
            storeState = "error";
            if (prompt) throw StoreFailure();
            return;
        }
        runtime = new(runtime.ConnectionId, result.Secret, SourceFor(entry), "unknown", false);
    }
    public async Task Apply(string password, bool persist, string expected, string storage = "os")
    {
        Expect(expected); ValidatePassword(password);
        if (storage is not ("os" or "file")) throw new UserError("지원하지 않는 비밀번호 저장 방식입니다.");
        var targetStore = storage == "file" ? fileStore : store;
        if (!persist) { runtime = new(expected, password, "session", "verified", false); return; }
        var uri = new Uri(db.Settings.WebDavUrl);
        var description = uri.Host + " · " + (db.Settings.WebDavUser.Length > 0 ? db.Settings.WebDavUser[..1] + "***" : "계정 없음");
        var entry = new CredentialEntry(Guid.NewGuid().ToString("N"), expected, description, "pending", targetStore.Provider);
        Save(document with { Entries = [.. document.Entries, entry] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await targetStore.Write(Key(entry), password, timeout.Token);
            var read = await targetStore.Read(Key(entry), false, timeout.Token);
            if (read.State != "available" || read.Secret != password) throw new CredentialStoreError(read.State == "available" ? "error" : read.State);
            Expect(expected);
            // One SQLite record switches active references atomically, after OS write/readback.
            Save(document with { Entries = document.Entries.Select(e => e.Id == entry.Id ? e with { Phase = "active" } :
                e.ConnectionId == expected && e.Phase == "active" ? e with { Phase = "deleting" } : e).ToArray() });
            runtime = new(expected, password, SourceFor(entry), "verified", false); storeState = "available";
        }
        catch (Exception e) when (e is CredentialStoreError or OperationCanceledException)
        {
            storeState = State(e);
            // Pending reference is durable. Startup or the cleanup button retries deletion.
            throw new UserError("연결은 확인했지만 기기에 비밀번호를 저장하지 못했습니다. 기존 비밀번호는 유지됩니다. 보관함을 확인하거나 ‘세션에서만 사용’을 선택하세요.");
        }
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Cleanup(false, cleanupTimeout.Token);
    }
    public async Task Delete(string expected, string? retiredId = null)
    {
        Expect(expected, allowEmpty: retiredId is not null);
        var entries = retiredId is null
            ? document.Entries.Where(e => e.ConnectionId == expected && Supported(e.Provider)).ToArray()
            : document.Entries.Where(e => e.Id == retiredId && (e.ConnectionId != expected || e.Phase != "active" || !Supported(e.Provider))).ToArray();
        if (retiredId is not null && entries.Length == 0) throw new UserError("삭제할 비밀번호 보관 항목이 없습니다.");
        if (entries.Any(e => !Supported(e.Provider))) throw new UserError("이 항목은 다른 OS의 보관함에 있습니다. 원래 기기에서 삭제하세요.");
        if (retiredId is null) Lock();
        var ids = entries.Select(e => e.Id).ToHashSet();
        Save(document with { Entries = document.Entries.Select(e => ids.Contains(e.Id) ? e with { Phase = "deleting" } : e).ToArray() });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var entry in entries)
        {
            try { await DeleteEntry(entry, true, timeout.Token); }
            catch (Exception e) when (e is CredentialStoreError or OperationCanceledException) { storeState = State(e); throw StoreFailure(); }
        }
    }
    private async Task DeleteEntry(CredentialEntry entry, bool prompt, CancellationToken ct)
    {
        await StoreFor(entry).Delete(Key(entry), prompt, ct);
        var check = await StoreFor(entry).Read(Key(entry), false, ct);
        if (check.State != "missing") throw new CredentialStoreError(check.State == "available" ? "error" : check.State);
        Save(document with { Entries = document.Entries.Where(e => e.Id != entry.Id).ToArray() });
    }
    private async Task Cleanup(bool prompt, CancellationToken ct)
    {
        foreach (var entry in document.Entries.Where(e => (e.Phase is "pending" or "deleting") && Supported(e.Provider)).ToArray())
        {
            try { await DeleteEntry(entry, prompt, ct); }
            catch (Exception e) when (e is CredentialStoreError or OperationCanceledException) { break; }
        }
    }
    public (string Password, Action<int> Report) Acquire(AppSettings settings)
    {
        var current = Volatile.Read(ref runtime);
        if (current.ConnectionId.Length == 0 || current.ConnectionId != ConnectionId(settings) || current.Password is null || current.Locked)
            throw new UserError(current.Verification == "rejected" ? "WebDAV 인증이 거부되었습니다. 비밀번호를 다시 입력하거나 저장된 비밀번호를 명시적으로 다시 사용하세요." :
                "WebDAV 비밀번호를 입력하거나 설정에서 저장된 비밀번호를 사용하세요.");
        return (current.Password, code =>
        {
            if (code is not (401 or 403)) return;
            while (true)
            {
                var latest = runtime;
                if (latest.Token != current.Token) return;
                var updated = code == 401 ? latest with { Password = null, Verification = "rejected", Locked = true } : latest with { Verification = "forbidden" };
                if (ReferenceEquals(Interlocked.CompareExchange(ref runtime, updated, latest), latest)) return;
            }
        });
    }
    public void Verified() { var current = runtime; Interlocked.CompareExchange(ref runtime, current with { Verification = "verified" }, current); }
    private static string State(Exception e) => e is CredentialStoreError error ? error.State : "cancelled";
    private UserError StoreFailure() => new(Active(runtime.ConnectionId)?.Provider == fileStore.Provider
        ? "비밀번호 파일을 읽거나 변경하지 못했습니다. 상태 폴더의 파일 권한을 확인하고 다시 저장하세요." : storeState switch
    {
        "locked" => "OS 비밀번호 보관함이 잠겨 있습니다. Desktop Mode에서 잠금을 해제하거나 세션 비밀번호를 입력하세요.",
        "missing" => "OS 보관함에서 비밀번호를 찾지 못했습니다. 다시 입력해 저장하세요.",
        "unavailable" => "OS 비밀번호 보관함을 사용할 수 없습니다. Linux에서는 libsecret과 사용자 Secret Service를 확인하거나 세션 입력을 사용하세요.",
        "denied" => "OS 비밀번호 보관함 접근이 거부되었습니다.",
        "cancelled" => "OS 비밀번호 보관함 요청이 취소되었거나 시간이 초과되었습니다.",
        _ => "OS 비밀번호 보관함 작업에 실패했습니다. 저장·삭제 상태를 확인하고 다시 시도하세요."
    });
}

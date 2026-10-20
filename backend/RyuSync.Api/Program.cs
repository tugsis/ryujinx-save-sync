using RyuSync;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
var root = Option(args, "--data-dir") ?? Environment.GetEnvironmentVariable("RYUSYNC_DATA_DIR") ??
    (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RyuSync") :
    Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "ryusync"));
root = Path.GetFullPath(root);
var command = args.FirstOrDefault() ?? "serve";
if (command is "launch" or "open")
{
    return await LauncherClient.Run(command, root, args);
}
if (command is not "serve" and not "--data-dir" and not "--port")
{
    Console.Error.WriteLine("사용법: ryusync [serve|open|launch] [--data-dir PATH] [--port 17843]"); return 2;
}
var port = int.TryParse(Option(args, "--port"), out var customPort) ? customPort : 17843;
if (port is < 1024 or > 65535) throw new UserError("포트는 1024~65535여야 합니다.");
Directory.CreateDirectory(root);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
using var processLock = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
using var db = new StateDatabase(root); var snapshots = new Snapshots(db); var engine = new SyncEngine(db, snapshots); var coordinator = new Coordinator(db, snapshots, engine);
var instanceSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var clientSessions = new ConcurrentDictionary<string, byte>(); var tickets = new ConcurrentDictionary<string, DateTimeOffset>();
string Ticket() { var t = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); tickets[t] = DateTimeOffset.UtcNow.AddMinutes(5); return t; }
var url = $"http://127.0.0.1:{port}";
var instanceFile = Path.Combine(root, "instance.json"); var instance = new InstanceInfo(url, instanceSecret, Environment.ProcessId);
await File.WriteAllTextAsync(instanceFile, JsonSerializer.Serialize(instance, JsonDefaults.Options));
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(instanceFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host;
    if (host.Port != port || host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://{host}") { ctx.Response.StatusCode = 403; return; }
        var trusted = ctx.Request.Headers["X-RyuSync-Agent"] == instanceSecret;
        if (ctx.Request.Path != "/api/session")
        {
            var session = ctx.Request.Cookies["ryusync_session"];
            if (!trusted && (session is null || !clientSessions.ContainsKey(session))) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "서버 콘솔에 표시된 설정 링크로 접속하세요." }); return; }
            if (!HttpMethods.IsGet(ctx.Request.Method) && !trusted && origin.Length == 0) { ctx.Response.StatusCode = 403; return; }
        }
    }
    try { await next(ctx); }
    catch (Exception e) when (e is UserError or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or JsonException or ArgumentException or InvalidOperationException)
    {
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = e is UserError ? 409 : 400; await ctx.Response.WriteAsJsonAsync(new { error = Coordinator.Friendly(e) }); }
    }
});
app.MapPost("/api/session", (SessionRequest request, HttpContext ctx) =>
{
    if (!tickets.TryRemove(request.Token, out var expires) || expires < DateTimeOffset.UtcNow) return Results.Unauthorized();
    var session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); clientSessions[session] = 0;
    ctx.Response.Cookies.Append("ryusync_session", session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/" }); return Results.Ok(new { ok = true });
});
app.MapPost("/api/ui-ticket", (HttpContext ctx) => ctx.Request.Headers["X-RyuSync-Agent"] == instanceSecret ? Results.Ok(new { url = url + "/#token=" + Ticket() }) : Results.Unauthorized());
app.MapGet("/api/status", () => new { version = "0.1.0-dev", device = db.Settings.DeviceName, os = OperatingSystem.IsWindows() ? "Windows" : "Linux / SteamOS", credentialUnlocked = coordinator.HasCredential, launch = coordinator.ActiveLaunch, pendingUploads = db.List<PendingUpload>("pending").Count, recovery = db.List<ApplyJournal>("journal").Count });
app.MapGet("/api/settings", () => db.Settings);
app.MapPut("/api/settings", async (AppSettings settings) => await coordinator.SaveSettings(settings));
app.MapPost("/api/storage/unlock", (CredentialRequest request) => { coordinator.Unlock(request.Password); return Results.Ok(new { unlocked = coordinator.HasCredential }); });
app.MapPost("/api/storage/test", async () => { await coordinator.TestConnection(); return Results.Ok(new { message = "생성·업로드·다운로드·검증·삭제 테스트를 완료했습니다." }); });
app.MapGet("/api/ryujinx/candidates", () => Discovery.Candidates());
app.MapPost("/api/ryujinx/scan", () => { ProcessGuard.EnsureIdle(db.Settings); return Discovery.Scan(db.Settings.DataRoot); });
app.MapGet("/api/save-sets", () => db.List<Binding>("binding"));
app.MapPost("/api/save-sets", async (RegisterRequest request) => await coordinator.Register(request.DiscoveryId, request.Slot));
app.MapDelete("/api/save-sets/{id}", async (string id) => { await coordinator.RemoveBinding(id); return Results.NoContent(); });
app.MapGet("/api/catalog", async () => await coordinator.Catalog());
app.MapPost("/api/sync/jobs", (SyncRequest request) => Results.Accepted(value: coordinator.StartSync(request.BindingId)));
app.MapPost("/api/conflicts/{id}/resolve", (string id, Resolution request) => Results.Accepted(value: coordinator.StartSync(id, "web", request)));
app.MapGet("/api/jobs", () => db.List<Job>("job").OrderByDescending(x => x.CreatedAt).Take(100));
app.MapGet("/api/jobs/{id}", (string id) => db.Get<Job>("job", id) is Job job ? Results.Ok(job) : Results.NotFound());
app.MapGet("/api/backups", () => db.List<Backup>("backup").OrderByDescending(x => x.CreatedAt));
app.MapPost("/api/launch-sessions", () => Results.Accepted(value: coordinator.StartLaunch()));
app.MapGet("/api/launch-sessions", () => db.List<LaunchSession>("launch").OrderByDescending(x => x.CreatedAt).Take(20));
app.MapGet("/api/launch-sessions/{id}", (string id) => db.Get<LaunchSession>("launch", id) is LaunchSession s ? Results.Ok(s) : Results.NotFound());
app.UseDefaultFiles(); app.UseStaticFiles(); app.MapFallbackToFile("index.html");
coordinator.RecoverLaunches();
Console.WriteLine("RyuSync 설정: " + url + "/#token=" + Ticket());
Console.WriteLine("접속 링크는 로컬 설정 권한을 포함합니다. 공유하지 마세요. Ctrl+C로 종료합니다.");
await app.RunAsync();
return 0;

record SessionRequest(string Token);
record CredentialRequest(string? Password);
record RegisterRequest(string DiscoveryId, string Slot);
record SyncRequest(string? BindingId);
record InstanceInfo(string Url, string Token, int ProcessId);

static class LauncherClient
{
    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { Console.WriteLine("브라우저에서 열기: " + url); }
    }
    public static async Task<int> Run(string command, string root, string[] args)
    {
        try
        {
            InstanceInfo? info = null;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<bool> Connect()
            {
                try
                {
                    var path = Path.Combine(root, "instance.json"); if (!File.Exists(path)) return false;
                    var next = JsonSerializer.Deserialize<InstanceInfo>(await File.ReadAllTextAsync(path), JsonDefaults.Options);
                    if (next is null || !Uri.TryCreate(next.Url, UriKind.Absolute, out var u) || u.Scheme != "http" || u.Host != "127.0.0.1") return false;
                    using var request = new HttpRequestMessage(HttpMethod.Get, next.Url + "/api/status"); request.Headers.Add("X-RyuSync-Agent", next.Token);
                    using var response = await client.SendAsync(request); if (!response.IsSuccessStatusCode) return false; info = next; return true;
                }
                catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or JsonException) { return false; }
            }
            if (!await Connect())
            {
                var executable = Environment.ProcessPath!; var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(executable) == "dotnet") start.ArgumentList.Add(typeof(LauncherClient).Assembly.Location);
                start.ArgumentList.Add("serve"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(root);
                var portIndex = Array.IndexOf(args, "--port"); if (portIndex >= 0 && portIndex + 1 < args.Length) { start.ArgumentList.Add("--port"); start.ArgumentList.Add(args[portIndex + 1]); }
                Process.Start(start);
                for (var i = 0; i < 50 && !await Connect(); i++) await Task.Delay(200);
                if (info is null) throw new UserError("로컬 백엔드가 시작되지 않았습니다. serve 명령으로 오류를 확인하세요.");
            }
            client.DefaultRequestHeaders.Add("X-RyuSync-Agent", info!.Token);
            var ticket = await client.PostAsJsonAsync(info.Url + "/api/ui-ticket", new { }); ticket.EnsureSuccessStatusCode();
            var ui = await ticket.Content.ReadFromJsonAsync<JsonElement>(); var browserUrl = ui.GetProperty("url").GetString()!;
            if (command == "open") { OpenBrowser(browserUrl); return 0; }
            using var launched = await client.PostAsJsonAsync(info.Url + "/api/launch-sessions", new { });
            if (!launched.IsSuccessStatusCode) { Console.Error.WriteLine(await launched.Content.ReadAsStringAsync()); OpenBrowser(browserUrl); return 1; }
            var session = await launched.Content.ReadFromJsonAsync<LaunchSession>(JsonDefaults.Options) ?? throw new UserError("실행 세션 응답이 없습니다.");
            Console.WriteLine("실행 전 동기화 중…"); string previous = "";
            while (true)
            {
                session = await client.GetFromJsonAsync<LaunchSession>(info.Url + "/api/launch-sessions/" + session.Id, JsonDefaults.Options) ?? throw new UserError("실행 세션을 찾지 못했습니다.");
                if (session.Status != previous) { Console.WriteLine(session.Message); previous = session.Status; }
                if (session.Status == "completed") return 0;
                if (session.Status is "blocked" or "failed" or "needs-attention") { OpenBrowser(browserUrl); return 1; }
                await Task.Delay(500);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(Coordinator.Friendly(e)); return 1; }
    }
}

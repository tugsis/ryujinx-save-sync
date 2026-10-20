using System.Net;
using System.Net.Sockets;
using System.Text;
using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public class WebDavTests
{
    [Fact]
    public async Task HttpAdapterCreatesListsUploadsDownloadsAndDeletes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start(); using var stop = new CancellationTokenSource();
        var files = new Dictionary<string, byte[]>(); var dirs = new HashSet<string> { "/" };
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx; try { ctx = await server.GetContextAsync().WaitAsync(stop.Token); } catch (OperationCanceledException) { break; }
                try
                {
                    if (ctx.Request.Headers["Authorization"] != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test:password"))) { ctx.Response.StatusCode = 401; continue; }
                    var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/') + "/"; var file = path.TrimEnd('/');
                    switch (ctx.Request.HttpMethod)
                    {
                        case "MKCOL": ctx.Response.StatusCode = dirs.Add(path) ? 201 : 405; break;
                        case "PUT": using (var memory = new MemoryStream()) { await ctx.Request.InputStream.CopyToAsync(memory); files[file] = memory.ToArray(); } ctx.Response.StatusCode = 201; break;
                        case "GET": if (!files.TryGetValue(file, out var bytes)) { ctx.Response.StatusCode = 404; break; } await ctx.Response.OutputStream.WriteAsync(bytes); break;
                        case "DELETE": files.Remove(file); ctx.Response.StatusCode = 204; break;
                        case "PROPFIND":
                            ctx.Response.StatusCode = 207; ctx.Response.ContentType = "application/xml";
                            var keys = files.Keys.Concat(dirs).Where(x => x.StartsWith(path, StringComparison.Ordinal) && !x[path.Length..].TrimEnd('/').Contains('/'));
                            var xml = "<d:multistatus xmlns:d='DAV:'>" + string.Join("", keys.Select(x => "<d:response><d:href>" + System.Security.SecurityElement.Escape(x) + "</d:href></d:response>")) + "</d:multistatus>";
                            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(xml)); break;
                        default: ctx.Response.StatusCode = 405; break;
                    }
                }
                finally { ctx.Response.Close(); }
            }
        });
        try
        {
            using var store = new WebDavStore(new AppSettings { WebDavUrl = $"http://127.0.0.1:{port}", WebDavUser = "test", AllowInsecureHttp = true }, "password");
            await store.EnsureDirectory("ryusync/main"); await store.Write("ryusync/main/한글.json", Encoding.UTF8.GetBytes("hello"));
            Assert.Contains("한글.json", await store.List("ryusync/main")); Assert.Equal("hello", Encoding.UTF8.GetString(await store.Read("ryusync/main/한글.json", 1024)));
            using var f = new Fixture(); var source = Path.Combine(f.Root, "source"); var target = Path.Combine(f.Root, "target"); File.WriteAllText(source, "snapshot bytes");
            await store.Upload("ryusync/main/save.zip", source); await store.Download("ryusync/main/save.zip", target, 1024); Assert.Equal("snapshot bytes", File.ReadAllText(target));
            await Assert.ThrowsAsync<UserError>(() => store.Read("ryusync/main/save.zip", 1)); await store.Delete("ryusync/main/save.zip"); Assert.DoesNotContain("save.zip", await store.List("ryusync/main"));
        }
        finally { stop.Cancel(); await serving; server.Stop(); }
    }
    [Fact] public void CredentialsCannotBeEmbeddedInServerUrl() => Assert.Throws<UserError>(() => new WebDavStore(new AppSettings { WebDavUrl = "https://user:password@example.com/" }, "x"));
    [Fact] public void PlainHttpRequiresOptIn() => Assert.Throws<UserError>(() => new WebDavStore(new AppSettings { WebDavUrl = "http://example.com/" }, "x"));
}

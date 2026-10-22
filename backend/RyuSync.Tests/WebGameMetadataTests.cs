using System.Net;
using System.Net.Http.Json;
using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public class WebGameMetadataTests
{
    private const string Id = "01007EF00011E000";
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xd9];
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Json(string id = Id, string name = "웹 게임", string? icon = "http://untrusted.invalid/image") =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id, name, icon }) };
    private static HttpResponseMessage Image(byte[]? bytes = null, string type = "image/jpeg")
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Jpeg) };
        result.Content.Headers.ContentType = new(type); return result;
    }
    [Fact]
    public async Task RequestsOnlyFixedHttpsHostAndCachesImagesWithoutTouchingSave()
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc"); var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Equal("https", request.RequestUri.Scheme); Assert.Equal("api.nlib.cc", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            return requests.Count == 1 ? Json() : Image();
        }));
        var info = await new WebGameMetadata(http).Fetch(Id.ToLowerInvariant(), "ko");
        Assert.Equal(new[] { $"https://api.nlib.cc/nx/{Id}?lang=ko&fields=name,icon", $"https://api.nlib.cc/nx/{Id}/icon/120" }, requests);
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        var before = Snapshots.Inspect(device.Binding.SaveRoot).Hash;
        var title = await coordinator.SaveWebGameInfo(info);
        device.Db.Put("binding", device.Binding.Id, device.Binding with { TitleId = Id });
        var displayed = Assert.Single(coordinator.Bindings());
        Assert.Equal(title.ThumbnailUrl, displayed.ThumbnailUrl); Assert.Equal(title.Title, displayed.Title);
        Assert.Null(device.Db.Get<Binding>("binding", device.Binding.Id)!.ThumbnailUrl);
        Assert.Equal("웹 게임", title.Title); Assert.Equal("web", title.Source);
        Assert.StartsWith($"/api/game-titles/{Id}/thumbnail?v=", title.ThumbnailUrl);
        Assert.Equal(Jpeg, Convert.FromBase64String(device.Db.Get<WebGameInfo>("game-web", Id)!.IconBase64!));
        Assert.Equal(before, Snapshots.Inspect(device.Binding.SaveRoot).Hash);
        using var reopened = new StateDatabase(device.Db.Root);
        Assert.Equal(title, GameTitles.Resolve(reopened, Id, Id)); // offline, no client required
    }
    [Fact]
    public async Task ManualNameSurvivesWebRefreshAndResetUsesWebName()
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc");
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        GameTitles.Remember(device.Db, Id, "Cache", "yuzu-cache");
        await coordinator.SetGameTitle(Id, "My name");
        var result = await coordinator.SaveWebGameInfo(new(Id, "Online", "en", Convert.ToBase64String(Jpeg), DateTimeOffset.UtcNow));
        Assert.Equal("manual", result.Source); Assert.Equal("My name", result.Title); Assert.NotNull(result.ThumbnailUrl);
        result = await coordinator.SetGameTitle(Id, null);
        Assert.Equal("Online", result.Title); Assert.Equal("web", result.Source);
        GameTitles.Remember(device.Db, Id, "Old cache", "yuzu-cache");
        Assert.Equal("Online", GameTitles.Resolve(device.Db, Id, "Old cache", "yuzu-cache").Title);
    }
    [Fact]
    public async Task MissingImageStillSavesNameAndRetainsPreviousThumbnail()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/120") ? new(HttpStatusCode.NotFound) : Json()));
        var info = await new WebGameMetadata(http).Fetch(Id, "en"); Assert.Null(info.IconBase64);
        using var fixture = new Fixture(); var device = fixture.Device("pc");
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        await coordinator.SaveWebGameInfo(info with { IconBase64 = Convert.ToBase64String(Jpeg) });
        var result = await coordinator.SaveWebGameInfo(info);
        Assert.NotNull(result.ThumbnailUrl);
    }
    [Theory]
    [InlineData("different-id")]
    [InlineData("bad-json")]
    [InlineData("large-json")]
    [InlineData("html-image")]
    [InlineData("large-image")]
    [InlineData("not-found")]
    [InlineData("redirect")]
    public async Task InvalidResponsesNeverReplaceExistingInformation(string scenario)
    {
        using var fixture = new Fixture(); var device = fixture.Device("pc");
        var coordinator = new Coordinator(device.Db, device.Snapshots, device.Engine);
        var previous = new WebGameInfo(Id, "Preserved", "en", Convert.ToBase64String(Jpeg), DateTimeOffset.UtcNow);
        await coordinator.SaveWebGameInfo(previous);
        using var http = new HttpClient(new Handler(request => scenario switch
        {
            "different-id" => Json("0100000000000000"),
            "bad-json" => new(HttpStatusCode.OK) { Content = new StringContent("<html>") },
            "large-json" => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 33000)) },
            "not-found" => new(HttpStatusCode.NotFound),
            "redirect" => new(HttpStatusCode.Redirect),
            _ => request.RequestUri!.AbsolutePath.EndsWith("/120") ? scenario == "html-image" ? Image([60, 62], "text/html") : Image(new byte[524289]) : Json()
        }));
        await Assert.ThrowsAnyAsync<Exception>(async () => await coordinator.SaveWebGameInfo(await new WebGameMetadata(http).Fetch(Id, "en")));
        Assert.Equal(previous, device.Db.Get<WebGameInfo>("game-web", Id));
        Assert.Equal("Preserved", GameTitles.Resolve(device.Db, Id, Id).Title);
    }
    [Theory]
    [InlineData("../outside", "en")]
    [InlineData(Id, "en&redirect=evil")]
    public async Task InvalidInputsMakeNoNetworkRequest(string id, string language)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Network must not be called")));
        await Assert.ThrowsAsync<UserError>(() => new WebGameMetadata(http).Fetch(id, language));
    }
}

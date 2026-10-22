using System.Net;
using System.Text.Json;
namespace RyuSync;

// Metadata only: never send save contents, credentials or local paths to this service.
public record WebGameInfo(string TitleId, string Title, string Language, string? IconBase64, DateTimeOffset FetchedAt);
public sealed class WebGameMetadata(HttpClient client)
{
    public const string Provider = "https://github.com/ghost-land/Nlib-API";
    public async Task<WebGameInfo> Fetch(string titleId, string language, CancellationToken ct = default)
    {
        titleId = GameTitles.NormalizeId(titleId);
        if (language is not ("ko" or "en" or "ja")) throw new UserError("지원하는 이름 언어는 한국어·영어·일본어입니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var bytes = await Read($"{titleId}?lang={language}&fields=name,icon", 32 * 1024, false, timeout.Token);
            using var json = JsonDocument.Parse(bytes!);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), titleId, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new UserError("웹 게임 정보의 Title ID 또는 이름이 올바르지 않습니다.");
            var title = GameTitles.ValidateName(name.GetString()!);
            byte[]? icon = null;
            if (root.TryGetProperty("icon", out var available) && available.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(available.GetString()))
            {
                // Ignore the supplied URL; use only our fixed HTTPS metadata host and exact title.
                icon = await Read($"{titleId}/icon/120", 512 * 1024, true, timeout.Token);
            }
            return new(titleId, title, language, icon is null ? null : Convert.ToBase64String(icon), DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException) { throw new UserError("웹 게임 정보 서버에 연결하지 못했습니다. 기존 이름·썸네일은 유지됩니다. 잠시 후 다시 시도하세요."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UserError("웹 게임 정보 조회 시간이 초과되었습니다. 기존 정보는 유지됩니다."); }
        catch (JsonException) { throw new UserError("웹 게임 정보 응답을 읽을 수 없습니다. 기존 정보는 유지됩니다."); }
    }
    private async Task<byte[]?> Read(string path, int limit, bool image, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nlib.cc/nx/" + path);
        request.Headers.UserAgent.ParseAdd("RyuSync/0.2");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (image) return null;
            throw new UserError("이 Title ID의 웹 게임 정보를 찾지 못했습니다. 이름을 직접 지정할 수 있습니다.");
        }
        response.EnsureSuccessStatusCode();
        // Redirects are disabled on the production client; never follow provider-supplied URLs.
        if ((int)response.StatusCode is < 200 or >= 300) throw new UserError("웹 게임 정보 서버가 다른 주소로 이동했습니다. 기존 정보는 유지됩니다.");
        if (response.Content.Headers.ContentLength > limit) throw new UserError("웹 게임 정보 응답이 너무 큽니다.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new UserError("웹 게임 정보 응답이 너무 큽니다.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (image && (response.Content.Headers.ContentType?.MediaType != "image/jpeg" || bytes.Length < 4 ||
            bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9))
            throw new UserError("게임 썸네일이 올바른 JPEG 이미지가 아닙니다.");
        return bytes;
    }
}

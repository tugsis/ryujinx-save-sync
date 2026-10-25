using RyuSync;
using Xunit;
namespace RyuSync.Tests;

public class DeckBrowserTests
{
    [Fact]
    public void NativeAppUsesSeparateProfileAndStructuredArguments()
    {
        var start = DeckBrowser.Build(new("/tools/browser with spaces"), "/data/my profile", "http://127.0.0.1:17843/?ui=deck#token=test");
        Assert.False(start.UseShellExecute);
        Assert.Equal("/tools/browser with spaces", start.FileName);
        Assert.Contains("--user-data-dir=/data/my profile", start.ArgumentList);
        Assert.Contains("--app=http://127.0.0.1:17843/?ui=deck#token=test", start.ArgumentList);
        Assert.Contains("--disable-background-mode", start.ArgumentList);
        Assert.DoesNotContain("--no-sandbox", start.ArgumentList);
    }
    [Fact]
    public void FlatpakOnlySharesItsProfile()
    {
        var start = DeckBrowser.Build(new("/usr/bin/flatpak", "org.chromium.Chromium"), "/tmp/private profile", "http://127.0.0.1:17843/");
        Assert.Equal(new[] { "run", "--filesystem=/tmp/private profile", "org.chromium.Chromium" }, start.ArgumentList.Take(3));
        Assert.DoesNotContain("--filesystem=home", start.ArgumentList);
        Assert.DoesNotContain("--filesystem=host", start.ArgumentList);
    }
    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user@127.0.0.1/")]
    public void RemoteOrFileUrlsAreRejected(string url) => Assert.Throws<UserError>(() => DeckBrowser.Build(new("browser"), "/tmp/profile", url));
    [Fact]
    public void ExplicitBrowserMustExistAtAbsolutePath() => Assert.Throws<UserError>(() => DeckBrowser.Find("browser"));
}

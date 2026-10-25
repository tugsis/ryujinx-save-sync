using System.Diagnostics;
namespace RyuSync;

public record BrowserCommand(string Executable, string? FlatpakId = null);

public static class DeckBrowser
{
    public static BrowserCommand? Find(string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            if (!Path.IsPathFullyQualified(explicitPath) || !File.Exists(explicitPath))
                throw new UserError("--browser에는 Chromium/Chrome 실행 파일의 절대 경로를 지정하세요.");
            return new(explicitPath);
        }
        foreach (var name in new[] { "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "msedge" })
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (!Path.IsPathFullyQualified(directory)) continue;
                var path = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
                if (File.Exists(path)) return new(path);
            }
        if (OperatingSystem.IsWindows())
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                foreach (var relative in new[] { "Google/Chrome/Application/chrome.exe", "Microsoft/Edge/Application/msedge.exe" })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var path = Path.Combine(root, relative); if (File.Exists(path)) return new(path);
                }
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/flatpak"))
            foreach (var id in new[] { "org.chromium.Chromium", "com.google.Chrome" })
            {
                var start = new ProcessStartInfo("/usr/bin/flatpak") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("info"); start.ArgumentList.Add(id);
                using var p = Process.Start(start)!;
                _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000)) { p.Kill(true); continue; }
                if (p.ExitCode == 0) return new("/usr/bin/flatpak", id);
            }
        return null;
    }
    public static ProcessStartInfo Build(BrowserCommand browser, string profile, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.UserInfo.Length > 0)
            throw new UserError("Deck 창은 로컬 RyuSync 주소만 열 수 있습니다.");
        var start = new ProcessStartInfo(browser.Executable) { UseShellExecute = false };
        if (browser.FlatpakId is not null)
        {
            start.ArgumentList.Add("run");
            // Expose this private profile only, never the state folder or saved passwords.
            start.ArgumentList.Add("--filesystem=" + profile);
            start.ArgumentList.Add(browser.FlatpakId);
        }
        foreach (var argument in new[] { "--user-data-dir=" + profile, "--app=" + url, "--class=RyuSync", "--window-size=1280,800", "--start-maximized", "--no-first-run", "--no-default-browser-check", "--disable-background-mode" })
            start.ArgumentList.Add(argument);
        return start;
    }
    public static async Task<int> Open(string url, BrowserCommand browser)
    {
        // Each launch owns a browser process: an existing desktop browser cannot absorb it.
        var profile = Path.Combine(Path.GetTempPath(), "ryusync-deck-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(profile);
        else Directory.CreateDirectory(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var process = Process.Start(Build(browser, profile, url)) ?? throw new UserError("Deck 창을 시작하지 못했습니다.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new UserError("Deck 창이 정상적으로 열리지 않았습니다. Desktop Mode에서 Chromium 실행 여부와 그래픽 세션을 확인하세요.");
            return 0;
        }
        finally
        {
            // Normal close discards cookies/history. Forced termination may leave this private temp folder.
            try { Directory.Delete(profile, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

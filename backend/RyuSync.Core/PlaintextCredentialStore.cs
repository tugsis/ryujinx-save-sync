using System.Text.Json;
namespace RyuSync;

// Explicitly selected plaintext storage; never used as an automatic OS-store fallback.
public sealed class PlaintextCredentialStore(string stateRoot) : ICredentialStore
{
    public const string ProviderName = "Local plaintext file";
    public string Provider => ProviderName;
    private string DirectoryPath => Path.Combine(stateRoot, "credentials-plaintext");
    private string EntryPath(string key) => Path.Combine(DirectoryPath, Hashing.Text(key) + ".json");
    private sealed record Entry(string Password)
    {
        public override string ToString() => "Plaintext credential";
    }
    private void CheckDirectory(bool create)
    {
        if (new DirectoryInfo(DirectoryPath).LinkTarget is not null) throw new CredentialStoreError("error");
        if (create)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
            else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (Directory.Exists(DirectoryPath) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void CheckFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new CredentialStoreError("error");
    }
    private static async Task<T> Guard<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (UnauthorizedAccessException) { throw new CredentialStoreError("denied"); }
        catch (Exception e) when (e is IOException or JsonException) { throw new CredentialStoreError("error"); }
    }
    public Task<string> Probe(CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested(); CheckDirectory(false); return Task.FromResult("available");
    });
    public Task<SecretRead> Read(string key, bool prompt, CancellationToken ct) => Guard(async () =>
    {
        CheckDirectory(false); var path = EntryPath(key); CheckFile(path);
        if (!File.Exists(path)) return new SecretRead("missing");
        if (new FileInfo(path).Length > 32 * 1024) throw new CredentialStoreError("error");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, ct));
        return entry is null ? new SecretRead("error") : new SecretRead("available", entry.Password);
    });
    public async Task Write(string key, string secret, CancellationToken ct) => await Guard(async () =>
    {
        CheckDirectory(true); var path = EntryPath(key); CheckFile(path);
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(file, new Entry(secret), cancellationToken: ct);
                await file.FlushAsync(ct); file.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    });
    public async Task Delete(string key, bool prompt, CancellationToken ct) => await Guard(() =>
    {
        ct.ThrowIfCancellationRequested(); CheckDirectory(false); var path = EntryPath(key); CheckFile(path);
        File.Delete(path); return Task.FromResult(true);
    });
}

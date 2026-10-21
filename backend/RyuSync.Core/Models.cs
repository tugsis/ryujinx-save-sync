using System.Text.Json;
namespace RyuSync;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
public record AppSettings
{
    public int ProtocolVersion { get; init; } = 1;
    public EmulatorInstallation[] Installations { get; init; } = [];
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; init; } = Environment.MachineName;
    public string DataRoot { get; init; } = "";
    public string WebDavUrl { get; init; } = "";
    public string WebDavUser { get; init; } = "";
    public string Workspace { get; init; } = "main";
    public bool AllowInsecureHttp { get; init; }
    public string Executable { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public bool SyncAfterLaunch { get; init; } = true;
}
public record DiscoveredSave(string Id, string TitleId, string Title, string UserId, string ProfileName,
    string SaveDataId, string SaveRoot, string ContentPath, string Status)
{
    public string TitleSource { get; init; } = "emulator";
    public string InstallationId { get; init; } = "legacy";
    public string EmulatorKind { get; init; } = "ryujinx";
}
public record ScanResult(string DataRoot, List<DiscoveredSave> Saves, List<string> Warnings);
public record Binding(string Id, string TitleId, string Title, string UserId, string SaveDataId,
    string SaveRoot, string Slot = "main", bool Enabled = true)
{
    public string TitleSource { get; init; } = "emulator";
    public string InstallationId { get; init; } = "legacy";
    public string EmulatorKind { get; init; } = "ryujinx";
    public string EmulatorBuild { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string RemoteKey => Hashing.Text(TitleId.ToLowerInvariant() + ":" + Slot)[..32];
}
public record Baseline(string BindingId, string Scope, string RevisionId, string ContentHash);
public record FileEntry(string Path, long Size, string Sha256);
public record TreeInfo(string Hash, List<FileEntry> Files, List<string> Directories);
public record Revision
{
    public string? ImportedFromScope { get; init; }
    public string? ImportedFromRevisionId { get; init; }
    public string SourceEmulator { get; init; } = "ryujinx";
    public string SourceBuild { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public int PayloadVersion { get; init; } = 1;
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string[] Parents { get; init; } = [];
    public string SaveKey { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Slot { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string ContentHash { get; init; } = "";
    public string ArchiveHash { get; init; } = "";
    public List<FileEntry> Files { get; init; } = [];
    public List<string> Directories { get; init; } = [];
}
public record Snapshot(string ArchivePath, TreeInfo Tree, string ArchiveHash);
public record SyncOutcome(string BindingId, string Status, string Message, string? LocalHash = null,
    string[]? Heads = null, Revision[]? Versions = null);
public record Resolution(string Choice, string ExpectedLocalHash, string[] ExpectedHeads);
public record Job(string Id, string Kind, string Source, string Status, string Message,
    DateTimeOffset CreatedAt, SyncOutcome[] Results);
public record Backup(string Id, string BindingId, string Title, string Path, string ContentHash, DateTimeOffset CreatedAt);
public record ApplyJournal(string Id, string Target, string Staged, string Previous, string BackupPath, string ExpectedHash);
public sealed class UserError(string message) : Exception(message);
public record PendingUpload(string Id, Binding Binding, string Scope, Snapshot Snapshot, string[] Parents);
public record LaunchSession(string Id, string Status, string Message, int? ProcessId, DateTime? ProcessStartedAt, DateTimeOffset CreatedAt, string? JobId = null)
{
    public string InstallationId { get; init; } = "legacy";
    public string? LaunchProfileId { get; init; }
}
public record EmulatorInstallation
{
    public string GameListCacheRoot { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "ryujinx";
    public string Build { get; init; } = "";
    public string DataRoot { get; init; } = "";
    public string NandRoot { get; init; } = "";
    public string Executable { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string[] Arguments { get; init; } = [];
}
public record LaunchProfile(string Id, string Name, string InstallationId, string[] GroupKeys, string[] Arguments);
public record CompatibilityApproval(string BindingId, string Signature, string State);
public record QueuedSyncRequest(string JobId, string? BindingId, Resolution? Resolution);
public record RestoreReservation(string Id, string InstallationId, string TitleId, string Slot, string RevisionId, string Scope);
public record RestoreIntent(string BindingId, string RevisionId, string Scope);

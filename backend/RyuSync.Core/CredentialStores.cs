using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace RyuSync;

public sealed record SecretRead(string State, string? Secret = null)
{
    public override string ToString() => State;
}
public interface ICredentialStore
{
    string Provider { get; }
    Task<string> Probe(CancellationToken ct);
    Task<SecretRead> Read(string key, bool prompt, CancellationToken ct);
    Task Write(string key, string secret, CancellationToken ct);
    Task Delete(string key, bool prompt, CancellationToken ct);
}
public sealed class CredentialStoreError(string state) : Exception("OS 비밀번호 보관함 작업을 완료하지 못했습니다.")
{
    public string State { get; } = state;
}
public static class CredentialStores
{
    public static ICredentialStore Create() => OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new LinuxCredentialStore();
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    public string Provider => "Windows Credential Manager";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("Advapi32.dll")] private static extern void CredFree(IntPtr value);
    private static CredentialStoreError Error() => new(Marshal.GetLastWin32Error() switch { 5 => "denied", 1312 => "unavailable", _ => "error" });
    public async Task<string> Probe(CancellationToken ct)
    {
        // A read of a fresh, nonexistent key checks logon-session access without storing data.
        await Read("RyuSync/Capability/" + Guid.NewGuid().ToString("N"), false, ct);
        return "available";
    }
    public Task<SecretRead> Read(string key, bool prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CredReadW(key, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return Task.FromResult(new SecretRead("missing"));
            throw Error();
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 4096) throw new CredentialStoreError("error");
            var bytes = new byte[credential.BlobSize]; Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            try { return Task.FromResult(new SecretRead("available", new UTF8Encoding(false, true).GetString(bytes))); }
            catch (DecoderFallbackException) { throw new CredentialStoreError("error"); }
            finally { CryptographicOperations.ZeroMemory(bytes); if (bytes.Length > 0) Marshal.Copy(bytes, 0, credential.Blob, bytes.Length); }
        }
        finally { CredFree(pointer); }
    }
    public Task Write(string key, string secret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var bytes = Encoding.UTF8.GetBytes(secret); var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var value = new Credential { Type = 1, TargetName = key, Comment = "RyuSync WebDAV", BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2, UserName = "RyuSync" };
            if (!CredWriteW(ref value, 0)) throw Error();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.FreeHGlobal(pointer); }
        return Task.CompletedTask;
    }
    public Task Delete(string key, bool prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CredDeleteW(key, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw Error();
        return Task.CompletedTask;
    }
}

// libsecret is a system library, not a shell command. Search without UNLOCK never prompts.
public sealed class LinuxCredentialStore : ICredentialStore
{
    public string Provider => "Linux Secret Service (libsecret)";
    private const string Secret = "libsecret-1.so.0", Glib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0", Gobject = "libgobject-2.0.so.0";
    // Values from SecretSearchFlags (not sequential bit positions starting at 0).
    private const int SearchAll = 2, SearchUnlock = 4, SearchLoadSecrets = 8;
    [DllImport(Secret)] private static extern IntPtr secret_service_get_sync(int flags, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern IntPtr secret_service_search_sync(IntPtr service, IntPtr schema, IntPtr attributes, int flags, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern int secret_item_get_locked(IntPtr item);
    [DllImport(Secret)] private static extern IntPtr secret_item_get_secret(IntPtr item);
    [DllImport(Secret)] private static extern IntPtr secret_value_get_text(IntPtr value);
    [DllImport(Secret)] private static extern void secret_value_unref(IntPtr value);
    [DllImport(Secret)] private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancel, out IntPtr error);
    [DllImport(Secret)] private static extern int secret_item_delete_sync(IntPtr item, IntPtr cancel, out IntPtr error);
    [DllImport(Glib)] private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(Glib)] private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib)] private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Glib)] private static extern void g_error_free(IntPtr error);
    [DllImport(Glib)] private static extern void g_list_free(IntPtr list);
    [DllImport(Gobject)] private static extern void g_object_unref(IntPtr obj);
    [DllImport(Gio)] private static extern IntPtr g_cancellable_new();
    [DllImport(Gio)] private static extern void g_cancellable_cancel(IntPtr cancel);
    [StructLayout(LayoutKind.Sequential)] private struct ListNode { public IntPtr Data, Next, Previous; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeError { public uint Domain; public int Code; public IntPtr Message; }
    private static readonly Lazy<IntPtr> GlibHandle = new(() => NativeLibrary.Load(Glib));
    private static void Check(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        // Classify locally; never propagate a native message containing service/user details.
        var message = Marshal.PtrToStringUTF8(Marshal.PtrToStructure<NativeError>(error).Message) ?? "";
        g_error_free(error);
        throw new CredentialStoreError(message.Contains("locked", StringComparison.OrdinalIgnoreCase) ? "locked" :
            message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "cancelled" :
            message.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "denied" : "unavailable");
    }
    private static async Task<T> Run<T>(Func<IntPtr, T> action, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                var cancel = g_cancellable_new();
                try { using var registration = ct.Register(() => g_cancellable_cancel(cancel)); ct.ThrowIfCancellationRequested(); return action(cancel); }
                finally { g_object_unref(cancel); }
            }, ct);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { throw new CredentialStoreError("unavailable"); }
        catch (CredentialStoreError) when (ct.IsCancellationRequested) { throw new CredentialStoreError("cancelled"); }
    }
    private sealed class Attributes : IDisposable
    {
        public IntPtr Table { get; }
        private readonly List<IntPtr> strings = [];
        public Attributes(string key)
        {
            Table = g_hash_table_new(NativeLibrary.GetExport(GlibHandle.Value, "g_str_hash"), NativeLibrary.GetExport(GlibHandle.Value, "g_str_equal"));
            Add("xdg:schema", "org.ryusync.WebDAV"); Add("ryusync-key", key);
        }
        private void Add(string name, string value)
        {
            var a = Marshal.StringToCoTaskMemUTF8(name); var b = Marshal.StringToCoTaskMemUTF8(value); strings.Add(a); strings.Add(b); g_hash_table_insert(Table, a, b);
        }
        public void Dispose() { g_hash_table_unref(Table); foreach (var s in strings) Marshal.FreeCoTaskMem(s); }
    }
    private static T Search<T>(string key, bool prompt, IntPtr cancel, Func<IntPtr[], T> action)
    {
        using var attributes = new Attributes(key);
        var list = secret_service_search_sync(IntPtr.Zero, IntPtr.Zero, attributes.Table, SearchAll | SearchLoadSecrets | (prompt ? SearchUnlock : 0), cancel, out var error);
        var items = new List<IntPtr>();
        try
        {
            for (var node = list; node != IntPtr.Zero;) { var entry = Marshal.PtrToStructure<ListNode>(node); items.Add(entry.Data); node = entry.Next; }
            Check(error); return action(items.ToArray());
        }
        finally { foreach (var item in items) g_object_unref(item); if (list != IntPtr.Zero) g_list_free(list); }
    }
    public Task<string> Probe(CancellationToken ct) => Run(cancel =>
    {
        var service = secret_service_get_sync(0, cancel, out var error);
        try { Check(error); return service == IntPtr.Zero ? "unavailable" : "available"; }
        finally { if (service != IntPtr.Zero) g_object_unref(service); }
    }, ct);
    public Task<SecretRead> Read(string key, bool prompt, CancellationToken ct) => Run(cancel => Search(key, prompt, cancel, items =>
    {
        if (items.Length == 0) return new SecretRead("missing");
        if (items.Length != 1) throw new CredentialStoreError("error");
        if (secret_item_get_locked(items[0]) != 0) return new SecretRead("locked");
        var value = secret_item_get_secret(items[0]);
        if (value == IntPtr.Zero) return new SecretRead("error");
        try
        {
            var text = Marshal.PtrToStringUTF8(secret_value_get_text(value));
            return text is null ? new SecretRead("error") : new SecretRead("available", text);
        }
        finally { secret_value_unref(value); }
    }), ct);
    public Task Write(string key, string secret, CancellationToken ct) => Run(cancel =>
    {
        using var attributes = new Attributes(key);
        var success = secret_password_storev_sync(IntPtr.Zero, attributes.Table, null, "RyuSync WebDAV", secret, cancel, out var error);
        Check(error); if (success == 0) throw new CredentialStoreError("cancelled"); return true;
    }, ct);
    public Task Delete(string key, bool prompt, CancellationToken ct) => Run(cancel => Search(key, prompt, cancel, items =>
    {
        foreach (var item in items)
        {
            if (secret_item_get_locked(item) != 0) throw new CredentialStoreError("locked");
            var success = secret_item_delete_sync(item, cancel, out var error); Check(error);
            if (success == 0) throw new CredentialStoreError("cancelled");
        }
        return true;
    }), ct);
}

using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace RyuSync;

public sealed class StateDatabase
{
    public string Root { get; }
    private readonly string connectionString;
    public StateDatabase(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "state.db") }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public void Put<T>(string kind, string id, T value)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO records VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonDefaults.Options)); cmd.ExecuteNonQuery();
    }
    public T? Get<T>(string kind, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM records WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) : default;
    }
    public List<T> List<T>(string kind)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT json FROM records WHERE kind=$kind ORDER BY id";
        cmd.Parameters.AddWithValue("$kind", kind); using var reader = cmd.ExecuteReader(); var rows = new List<T>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonDefaults.Options)!);
        return rows;
    }
    public void Delete(string kind, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM records WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public AppSettings Settings
    {
        get { var s = Get<AppSettings>("settings", "app"); if (s is not null) return s; s = new AppSettings(); Put("settings", "app", s); return s; }
        set => Put("settings", "app", value);
    }
}

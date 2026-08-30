using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Backend.Persistence;

// ── Portable persisted key-value store (a "localStorage" — NOT Windows-gated) ────────────────────────────────────────
// Backed by a JSON file in the user's app-data dir, which resolves on Windows/macOS/Linux via Environment.SpecialFolder.
// The engine's AppDataStore is Windows-registry-gated; this is the cross-platform alternative the backend uses. The path
// is injectable so tests run against a temp file. AOT-safe JSON (source-gen context).
public interface ILocalStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

public sealed class FileLocalStore : ILocalStore
{
    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, string> _data;

    public FileLocalStore(string filePath)
    {
        _path = filePath;
        _data = LoadFile(filePath);
    }

    /// <summary>Default location: %LOCALAPPDATA%/Wavee/store.json on Windows; ~/.local/share or ~/Library equivalents elsewhere.</summary>
    public static FileLocalStore ForApp(string appName = "Wavee", string fileName = "store.json")
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        Directory.CreateDirectory(dir);
        return new FileLocalStore(Path.Combine(dir, fileName));
    }

    public string? Get(string key)
    {
        lock (_gate) return _data.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate) { _data[key] = value; Save(); }
    }

    public void Remove(string key)
    {
        lock (_gate) { if (_data.Remove(key)) Save(); }
    }

    static Dictionary<string, string> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize(File.ReadAllText(path), LocalStoreJson.Default.DictionaryStringString) ?? new();
        }
        catch { return new(); }   // corrupt/locked → start fresh, never crash the app over a settings file
    }

    void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, LocalStoreJson.Default.DictionaryStringString);
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);   // fsync — survive power loss / OS crash, not just a process crash
        }
        if (!OperatingSystem.IsWindows())
        {
            // 0600 — owner read/write only, so the credential file is never world-readable on POSIX.
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        File.Move(tmp, _path, overwrite: true);   // write-then-rename: a crash can't leave a half-written file
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalStoreJson : JsonSerializerContext { }

using System.Text.Json;

namespace FuWindows.Core;

/// <summary>The value something had before FuWindows first touched it.</summary>
public sealed class BackupEntry
{
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Data { get; set; }
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists original values to %ProgramData%\FuWindows\backup.json. Only the *first* original is
/// kept, so applying a tweak twice never overwrites the real original with our own value.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static BackupStore Instance { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FuWindows", "backup.json"));

    private readonly object _gate = new();
    private readonly Dictionary<string, BackupEntry> _entries;

    public string FilePath { get; }

    public BackupStore(string filePath)
    {
        FilePath = filePath;
        _entries = Load(filePath);
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    public bool Contains(string key) { lock (_gate) return _entries.ContainsKey(key); }

    public void RecordOnce(string key, Func<BackupEntry> capture)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return;
            _entries[key] = capture();
            Save();
        }
    }

    public bool TryGet(string key, out BackupEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var found)) { entry = found; return true; }
            entry = null!;
            return false;
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key)) Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static Dictionary<string, BackupEntry> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(path));
            return new(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Never silently discard a backup: keep the unreadable file for manual recovery.
            File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

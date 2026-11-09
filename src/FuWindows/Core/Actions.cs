using System.Globalization;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FuWindows.Core;

// ---------------------------------------------------------------------------------------------
// Registry value
// ---------------------------------------------------------------------------------------------

public sealed class RegistryAction : ITweakAction
{
    public RegistryHive Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public RegistryValueKind Kind { get; }
    public object Value { get; }

    private RegistryAction(RegistryHive hive, string path, string name, RegistryValueKind kind, object value)
    {
        Hive = hive; Path = path; Name = name; Kind = kind; Value = value;
    }

    public static RegistryAction Dword(RegistryHive hive, string path, string name, int value) =>
        new(hive, path, name, RegistryValueKind.DWord, value);

    public static RegistryAction String(RegistryHive hive, string path, string name, string value) =>
        new(hive, path, name, RegistryValueKind.String, value);

    private string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString(),
    };

    public string Key => $"reg:{HiveName}\\{Path}\\{Name}";

    public string Describe() => $"Registry  {HiveName}\\{Path}  →  {Name} = {Value} ({Kind})";

    private RegistryKey OpenBase() => RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);

    private object? ReadCurrent(out RegistryValueKind kind)
    {
        kind = RegistryValueKind.Unknown;
        using var root = OpenBase();
        using var key = root.OpenSubKey(Path);
        if (key is null) return null;
        var value = key.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not null) kind = key.GetValueKind(Name);
        return value;
    }

    private bool Matches(object? current) => Kind switch
    {
        RegistryValueKind.DWord => current is int i && i == (int)Value,
        RegistryValueKind.String => current is string s && string.Equals(s, (string)Value, StringComparison.Ordinal),
        _ => Equals(current, Value),
    };

    public ActionState GetState() => Matches(ReadCurrent(out _)) ? ActionState.Applied : ActionState.NotApplied;

    public bool IsOwnedBy(BackupStore backup) => backup.Contains(Key);

    public void Apply(TweakContext ctx)
    {
        ctx.Backup.RecordOnce(Key, () =>
        {
            var current = ReadCurrent(out var kind);
            return current is null
                ? new BackupEntry { Existed = false }
                : new BackupEntry { Existed = true, Kind = kind.ToString(), Data = Serialize(current, kind) };
        });

        using var root = OpenBase();
        using var key = root.CreateSubKey(Path, writable: true)
            ?? throw new InvalidOperationException($"Cannot open {HiveName}\\{Path}");
        key.SetValue(Name, Value, Kind);
    }

    public void Revert(TweakContext ctx)
    {
        using var root = OpenBase();

        if (ctx.Backup.TryGet(Key, out var entry))
        {
            if (!entry.Existed)
            {
                DeleteValue(root);
            }
            else
            {
                var kind = Enum.Parse<RegistryValueKind>(entry.Kind!);
                using var key = root.CreateSubKey(Path, writable: true)!;
                key.SetValue(Name, Deserialize(entry.Data, kind), kind);
            }
            ctx.Backup.Remove(Key);
            return;
        }

        // No backup (e.g. it was already set before FuWindows ran): only remove it if it is
        // still our value, which returns Windows to its built-in default.
        if (Matches(ReadCurrent(out _))) DeleteValue(root);
    }

    private void DeleteValue(RegistryKey root)
    {
        using var key = root.OpenSubKey(Path, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
    }

    private static string? Serialize(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ((int)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => string.Join('\0', (string[])value),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        _ => value.ToString(),
    };

    private static object Deserialize(string? data, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(data!, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(data!, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => (data ?? "").Split('\0'),
        RegistryValueKind.Binary => Convert.FromBase64String(data ?? ""),
        _ => data ?? "",
    };
}

// ---------------------------------------------------------------------------------------------
// Windows service startup type
// ---------------------------------------------------------------------------------------------

public enum ServiceStart { Automatic = 2, Manual = 3, Disabled = 4 }

public sealed class ServiceAction(string serviceName, ServiceStart target, ServiceStart windowsDefault) : ITweakAction
{
    private string RegPath => $@"SYSTEM\CurrentControlSet\Services\{serviceName}";

    public string Key => $"svc:{serviceName}";

    public string Describe() => $"Service   {serviceName}  →  startup {target}";

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private int? ReadStart()
    {
        using var root = Hklm();
        using var key = root.OpenSubKey(RegPath);
        return key?.GetValue("Start") as int?;
    }

    public ActionState GetState()
    {
        var start = ReadStart();
        if (start is null) return ActionState.NotApplicable;
        return start == (int)target ? ActionState.Applied : ActionState.NotApplied;
    }

    public bool IsOwnedBy(BackupStore backup) => backup.Contains(Key);

    public void Apply(TweakContext ctx)
    {
        var current = ReadStart();
        if (current is null) return; // Service not present on this edition.

        ctx.Backup.RecordOnce(Key, () => new BackupEntry
        {
            Existed = true,
            Kind = "DWord",
            Data = current.Value.ToString(CultureInfo.InvariantCulture),
        });

        SetStart((int)target);
        if (target == ServiceStart.Disabled)
            ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"stop \"{serviceName}\"");
    }

    public void Revert(TweakContext ctx)
    {
        if (ReadStart() is null) return;

        int restore = (int)windowsDefault;
        if (ctx.Backup.TryGet(Key, out var entry) && int.TryParse(entry.Data, out var original))
            restore = original;

        SetStart(restore);
        ctx.Backup.Remove(Key);

        if (restore == (int)ServiceStart.Automatic)
            ProcessRunner.Run(ProcessRunner.System32("sc.exe"), $"start \"{serviceName}\"");
    }

    private void SetStart(int value)
    {
        using var root = Hklm();
        using var key = root.OpenSubKey(RegPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot open service {serviceName}");
        key.SetValue("Start", value, RegistryValueKind.DWord);
    }
}

// ---------------------------------------------------------------------------------------------
// Scheduled task (disable)
// ---------------------------------------------------------------------------------------------

public sealed class ScheduledTaskAction(string taskPath) : ITweakAction
{
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public string Key => $"task:{taskPath}";

    public string Describe() => $"Task      {taskPath}  →  disabled";

    /// <returns>null if the task doesn't exist. Parses XML so it works on any display language.</returns>
    private bool? IsEnabled()
    {
        var result = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), $"/Query /TN \"{taskPath}\" /XML");
        if (!result.Success) return null;

        var xml = XDocument.Parse(result.Output);
        var enabled = xml.Root?.Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled");
        return enabled is null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    public ActionState GetState() => IsEnabled() switch
    {
        null => ActionState.NotApplicable,
        true => ActionState.NotApplied,
        false => ActionState.Applied,
    };

    public bool IsOwnedBy(BackupStore backup) => backup.Contains(Key);

    public void Apply(TweakContext ctx)
    {
        var enabled = IsEnabled();
        if (enabled is null) return;

        ctx.Backup.RecordOnce(Key, () => new BackupEntry { Existed = true, Data = enabled.Value ? "enabled" : "disabled" });
        if (enabled == true) Change("/Disable");
    }

    public void Revert(TweakContext ctx)
    {
        if (IsEnabled() is null) return;

        bool wasEnabled = !ctx.Backup.TryGet(Key, out var entry) || entry.Data == "enabled";
        if (wasEnabled) Change("/Enable");
        ctx.Backup.Remove(Key);
    }

    private void Change(string flag)
    {
        var result = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), $"/Change /TN \"{taskPath}\" {flag}");
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
}

// ---------------------------------------------------------------------------------------------
// Windows Firewall outbound block for a program
// ---------------------------------------------------------------------------------------------

public sealed class FirewallAction(string label, string programPath) : ITweakAction
{
    private string RuleName => $"FuWindows - Block {label}";
    private string ExpandedPath => Environment.ExpandEnvironmentVariables(programPath);

    public string Key => $"fw:{RuleName}";

    public string Describe() => $"Firewall  block outbound traffic for {programPath}";

    private static string Netsh => ProcessRunner.System32("netsh.exe");

    private bool RuleExists() =>
        ProcessRunner.Run(Netsh, $"advfirewall firewall show rule name=\"{RuleName}\"").Success;

    public ActionState GetState()
    {
        if (RuleExists()) return ActionState.Applied;
        return File.Exists(ExpandedPath) ? ActionState.NotApplied : ActionState.NotApplicable;
    }

    public bool IsOwnedBy(BackupStore backup) => RuleExists();

    public void Apply(TweakContext ctx)
    {
        if (!File.Exists(ExpandedPath)) return;
        ProcessRunner.Run(Netsh, $"advfirewall firewall delete rule name=\"{RuleName}\"");
        var result = ProcessRunner.Run(Netsh,
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=block program=\"{ExpandedPath}\" enable=yes profile=any");
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    public void Revert(TweakContext ctx)
    {
        if (RuleExists())
            ProcessRunner.Run(Netsh, $"advfirewall firewall delete rule name=\"{RuleName}\"");
    }
}

// ---------------------------------------------------------------------------------------------
// Hosts file block list
// ---------------------------------------------------------------------------------------------

public sealed class HostsAction(string tag, IReadOnlyList<string> domains) : ITweakAction
{
    public IReadOnlyList<string> Domains { get; } = domains;

    public string Key => $"hosts:{tag}";

    public string Describe() =>
        $"Hosts     0.0.0.0 for {Domains.Count} domains: {string.Join(", ", Domains.Take(4))}{(Domains.Count > 4 ? ", …" : "")}";

    public ActionState GetState() => HostsFile.HasBlock(tag) ? ActionState.Applied : ActionState.NotApplied;

    public bool IsOwnedBy(BackupStore backup) => HostsFile.HasBlock(tag);

    public void Apply(TweakContext ctx)
    {
        HostsFile.WriteBlock(tag, Domains);
        HostsFile.FlushDns();
    }

    public void Revert(TweakContext ctx)
    {
        HostsFile.RemoveBlock(tag);
        HostsFile.FlushDns();
    }
}

public static class HostsFile
{
    public static string FilePath { get; } =
        System.IO.Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");

    private static readonly object Gate = new();

    private static string Begin(string tag) => $"# >>> FuWindows [{tag}]";
    private static string End(string tag) => $"# <<< FuWindows [{tag}]";

    public static bool HasBlock(string tag)
    {
        if (!File.Exists(FilePath)) return false;
        return File.ReadLines(FilePath).Any(l => l.Trim() == Begin(tag));
    }

    public static void WriteBlock(string tag, IEnumerable<string> domains)
    {
        lock (Gate)
        {
            var lines = StripBlock(ReadLines(), tag);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

            lines.Add("");
            lines.Add(Begin(tag));
            lines.AddRange(domains.Select(d => $"0.0.0.0 {d}"));
            lines.Add(End(tag));
            Write(lines);
        }
    }

    public static void RemoveBlock(string tag)
    {
        lock (Gate)
        {
            var original = ReadLines();
            var stripped = StripBlock(original, tag);
            if (stripped.Count != original.Count) Write(stripped);
        }
    }

    public static void FlushDns() => ProcessRunner.Run(ProcessRunner.System32("ipconfig.exe"), "/flushdns");

    private static List<string> ReadLines() =>
        File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : [];

    private static List<string> StripBlock(List<string> lines, string tag)
    {
        var result = new List<string>(lines.Count);
        bool inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == Begin(tag)) { inside = true; continue; }
            if (trimmed == End(tag)) { inside = false; continue; }
            if (!inside) result.Add(line);
        }
        // Drop the blank separator line we add in front of our block.
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void Write(List<string> lines)
    {
        var info = new FileInfo(FilePath);
        if (info.Exists && info.IsReadOnly) info.IsReadOnly = false;

        // Back up the pristine hosts file the first time we ever modify it.
        var pristine = FilePath + ".fuwindows.bak";
        if (info.Exists && !File.Exists(pristine)) File.Copy(FilePath, pristine);

        File.WriteAllText(FilePath, string.Join("\r\n", lines) + "\r\n");
    }
}

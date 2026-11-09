using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FuWindows.Core;

/// <summary>
/// Third-party repositories: winget sources, Chocolatey sources, Scoop buckets and Homebrew taps,
/// for whichever of those package managers are installed. Also each manager's own telemetry switch.
/// </summary>
public static class Repositories
{
    public sealed record Source(string Name, string Location, bool Enabled, bool CanDisable, bool IsDefault);

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ProcessResult Check(ProcessResult r, string what)
    {
        if (!r.Success)
        {
            var msg = Lines(r.Output + "\n" + r.Error).LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter)) ?? $"exit code {r.ExitCode}";
            throw new InvalidOperationException($"{what}: {msg}");
        }
        return r;
    }

    // -------------------------------------------------------------------------------------
    // winget
    // -------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (string Url, string Type)> WingetDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winget"] = ("https://cdn.winget.microsoft.com/cache", "Microsoft.PreIndexed.Package"),
        ["msstore"] = ("https://storeedgefd.dsx.mp.microsoft.com/v9.0", "Microsoft.Rest"),
    };

    private static string Winget => SystemTools.FindWinget() ?? throw new InvalidOperationException("winget isn't installed.");

    public static List<Source> WingetSources()
    {
        var r = ProcessRunner.Run(Winget, "source list --disable-interactivity", 60_000, Encoding.UTF8);
        var sources = new List<Source>();
        bool table = false;
        foreach (var line in Lines(r.Output))
        {
            if (line.StartsWith("---")) { table = true; continue; }
            if (!table) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].Contains("://")) continue;
            sources.Add(new Source(parts[0], parts[1], true, false, WingetDefaults.ContainsKey(parts[0])));
        }
        return sources;
    }

    /// <summary>Default sources that have been removed and can be added back.</summary>
    public static IEnumerable<string> MissingWingetDefaults(IEnumerable<Source> current) =>
        WingetDefaults.Keys.Where(k => !current.Any(s => s.Name.Equals(k, StringComparison.OrdinalIgnoreCase)));

    public static void WingetAdd(string name, string url, string? type)
    {
        if (WingetDefaults.TryGetValue(name, out var d) && string.IsNullOrWhiteSpace(url)) (url, type) = d;
        var typeArg = string.IsNullOrWhiteSpace(type) ? "" : $" --type {type}";
        Check(ProcessRunner.Run(Winget, $"source add --name \"{name}\" --arg \"{url}\"{typeArg} --accept-source-agreements --disable-interactivity", 120_000, Encoding.UTF8),
            $"Couldn't add {name}");
    }

    public static void WingetRemove(string name) =>
        Check(ProcessRunner.Run(Winget, $"source remove --name \"{name}\" --disable-interactivity", 60_000, Encoding.UTF8), $"Couldn't remove {name}");

    public static void WingetReset() =>
        Check(ProcessRunner.Run(Winget, "source reset --force --disable-interactivity", 120_000, Encoding.UTF8), "Couldn't reset sources");

    private static string WingetSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json");

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject ReadWingetSettings()
    {
        if (!File.Exists(WingetSettingsPath)) return [];
        try { return JsonNode.Parse(File.ReadAllText(WingetSettingsPath), documentOptions: Lenient) as JsonObject ?? []; }
        catch { return []; }
    }

    public static bool WingetTelemetryDisabled() =>
        ReadWingetSettings()["telemetry"]?["disable"]?.GetValue<bool>() == true;

    /// <summary>Sets "telemetry": { "disable": … } in winget's settings.json (backing it up first).</summary>
    public static void SetWingetTelemetryDisabled(bool disabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WingetSettingsPath)!);
        if (File.Exists(WingetSettingsPath) && !File.Exists(WingetSettingsPath + ".fuwindows.bak"))
            File.Copy(WingetSettingsPath, WingetSettingsPath + ".fuwindows.bak");

        var settings = ReadWingetSettings();
        settings["$schema"] ??= "https://aka.ms/winget-settings.schema.json";
        if (settings["telemetry"] is not JsonObject telemetry) settings["telemetry"] = telemetry = [];
        telemetry["disable"] = disabled;
        File.WriteAllText(WingetSettingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // -------------------------------------------------------------------------------------
    // Chocolatey
    // -------------------------------------------------------------------------------------

    private static string Choco => Installer.ChocoPath() ?? throw new InvalidOperationException("Chocolatey isn't installed.");

    public static List<Source> ChocoSources()
    {
        var r = ProcessRunner.Run(Choco, "source list --limit-output", 60_000);
        return Lines(r.Output)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 3)
            .Select(p => new Source(p[0], p[1], !p[2].Equals("true", StringComparison.OrdinalIgnoreCase), true,
                p[0].Equals("chocolatey", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static void ChocoAdd(string name, string url) =>
        Check(ProcessRunner.Run(Choco, $"source add -n=\"{name}\" -s=\"{url}\"", 60_000), $"Couldn't add {name}");
    public static void ChocoRemove(string name) =>
        Check(ProcessRunner.Run(Choco, $"source remove -n=\"{name}\"", 60_000), $"Couldn't remove {name}");
    public static void ChocoSetEnabled(string name, bool enabled) =>
        Check(ProcessRunner.Run(Choco, $"source {(enabled ? "enable" : "disable")} -n=\"{name}\"", 60_000), $"Couldn't change {name}");

    // -------------------------------------------------------------------------------------
    // Scoop
    // -------------------------------------------------------------------------------------

    /// <summary>Scoop's well-known buckets (`scoop bucket known`).</summary>
    public static IReadOnlyList<(string Name, string Description)> KnownBuckets { get; } =
    [
        ("extras", "Popular apps that don't fit the main bucket (GUI apps)"),
        ("versions", "Older and alternative versions of apps"),
        ("nerd-fonts", "Nerd Fonts and other fonts"),
        ("games", "Open-source and freeware games and launchers"),
        ("java", "JDKs and JREs"),
        ("nonportable", "Apps that need a regular (non-portable) install"),
        ("sysinternals", "Microsoft Sysinternals tools"),
        ("php", "PHP versions"),
        ("nirsoft", "NirSoft utilities"),
    ];

    private static ProcessResult Scoop(string args, int timeoutMs = 300_000)
    {
        var scoop = Installer.ScoopPath() ?? throw new InvalidOperationException("Scoop isn't installed.");
        return ProcessRunner.PowerShell($"& '{scoop}' {args}", timeoutMs);
    }

    public static List<Source> ScoopBuckets()
    {
        var r = ProcessRunner.PowerShell(
            $"& '{Installer.ScoopPath()}' bucket list | ForEach-Object {{ \"$($_.Name)|$($_.Source)\" }}", 120_000);
        return Lines(r.Output)
            .Select(l => l.Split('|', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => new Source(p[0], p[1], true, false, p[0] == "main"))
            .ToList();
    }

    public static void ScoopAdd(string name, string? url) =>
        Check(Scoop(string.IsNullOrWhiteSpace(url) ? $"bucket add {name}" : $"bucket add {name} '{url}'"),
            $"Couldn't add bucket {name} (Scoop needs Git for buckets: scoop install git)");
    public static void ScoopRemove(string name) => Check(Scoop($"bucket rm {name}"), $"Couldn't remove bucket {name}");

    // -------------------------------------------------------------------------------------
    // Homebrew (inside WSL)
    // -------------------------------------------------------------------------------------

    private const string Brew = "/home/linuxbrew/.linuxbrew/bin/brew";

    private static ProcessResult BrewRun(string args, int timeoutMs = 300_000) =>
        ProcessRunner.Run(ProcessRunner.System32("wsl.exe"), $"-e {Brew} {args}", timeoutMs, Encoding.UTF8);

    public static List<Source> BrewTaps() =>
        Lines(BrewRun("tap", 120_000).Output)
            .Select(t => new Source(t, $"https://github.com/{t.Replace("/", "/homebrew-")}", true, false, t.StartsWith("homebrew/")))
            .ToList();

    public static void BrewTap(string tap) => Check(BrewRun($"tap {tap}"), $"Couldn't tap {tap}");
    public static void BrewUntap(string tap) => Check(BrewRun($"untap {tap}"), $"Couldn't untap {tap}");

    public static bool BrewAnalyticsOff() =>
        BrewRun("analytics state", 60_000).Output.Contains("disabled", StringComparison.OrdinalIgnoreCase);

    public static void SetBrewAnalytics(bool on) =>
        Check(BrewRun($"analytics {(on ? "on" : "off")}", 60_000), "Couldn't change Homebrew analytics");
}

/// <summary>Removes OneDrive's File Explorer overlay icons (sync status badges), with a .reg backup.</summary>
public static class OneDriveOverlays
{
    private const string Overlays = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    private static string BackupFile => AppData.File("onedrive-overlays.reg");

    private static List<string> Present()
    {
        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = root.OpenSubKey(Overlays);
        return key?.GetSubKeyNames().Where(n => n.TrimStart().StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
    }

    public static CommandAction Action() => new(
        "onedrive:overlays",
        $@"Registry  remove HKLM\{Overlays}\ OneDrive1-7 (backed up to %ProgramData%\FuWindows\onedrive-overlays.reg)",
        () => Present().Count > 0 ? ActionState.NotApplied : File.Exists(BackupFile) ? ActionState.Applied : ActionState.NotApplicable,
        ctx =>
        {
            var keys = Present();
            if (keys.Count == 0) return;
            // Export the whole overlay list once, so revert restores exactly what was there.
            if (!File.Exists(BackupFile))
            {
                var r = ProcessRunner.Run(ProcessRunner.System32("reg.exe"), $"export \"HKLM\\{Overlays}\" \"{BackupFile}\" /y /reg:64");
                if (!r.Success) throw new InvalidOperationException($"Backup failed, nothing changed: {r.Message}");
            }
            ctx.Backup.RecordOnce("onedrive:overlays", () => new BackupEntry { Existed = true, Data = BackupFile });
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = root.OpenSubKey(Overlays, writable: true)!;
            foreach (var name in keys) key.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            ctx.Log($"  Removed {keys.Count} OneDrive overlay handler(s). Restart Explorer to see the change.");
        },
        ctx =>
        {
            if (File.Exists(BackupFile))
            {
                var r = ProcessRunner.Run(ProcessRunner.System32("reg.exe"), $"import \"{BackupFile}\" /reg:64");
                if (!r.Success) throw new InvalidOperationException(r.Message);
                File.Delete(BackupFile);
            }
            ctx.Backup.Remove("onedrive:overlays");
        });
}

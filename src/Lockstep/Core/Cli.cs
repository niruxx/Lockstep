using System.Runtime.InteropServices;

namespace Lockstep.Core;

/// <summary>
/// Headless mode. Any command-line argument runs Lockstep without a window:
/// <code>
///   Lockstep.exe --preset recommended [--restore-point]
///   Lockstep.exe --config my-profile.json [--restore-point]     (or just: Lockstep.exe my-profile.json)
///   Lockstep.exe --apply telemetry.level,privacy.adid
///   Lockstep.exe --revert telemetry.level   |   --revert-all
///   Lockstep.exe --list
///   Lockstep.exe --enforce      (used by the "Keep settings applied" task)
/// </code>
/// Output goes to the console (when started from an elevated terminal) and to
/// %ProgramData%\Lockstep\cli.log. Exit code: 0 = success, 1 = some changes failed, 2 = bad arguments.
/// </summary>
public static class Cli
{
    public const string Usage = """
        Lockstep command line (run from an elevated terminal)

          Lockstep.exe --preset recommended|limited [--restore-point]
          Lockstep.exe --config <profile.json> [--restore-point]
          Lockstep.exe <profile.json>                      same as --config
          Lockstep.exe --apply <id>[,<id>...] [--restore-point]
          Lockstep.exe --revert <id>[,<id>...]
          Lockstep.exe --revert-all
          Lockstep.exe --list                              tweak IDs, risk and status
          Lockstep.exe --enforce                           re-apply "Keep settings applied" list

        Exit codes: 0 success, 1 some changes failed, 2 bad arguments.
        Log: %ProgramData%\Lockstep\cli.log
        """;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        if (AttachConsole(-1))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.WriteLine();
        }

        using var logFile = new StreamWriter(AppData.File("cli.log"), append: true) { AutoFlush = true };
        void Log(string line)
        {
            Console.WriteLine(line);
            logFile.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");
        }

        Log($"Lockstep {string.Join(' ', args)}");
        try
        {
            int code = Execute(args, Log);
            Log($"Exit code {code}");
            return code;
        }
        catch (Exception ex)
        {
            Log($"✗ {ex.Message}");
            return 1;
        }
    }

    private static int Execute(string[] args, Action<string> log)
    {
        var options = args.Select(a => a.Trim()).ToList();
        bool restorePoint = options.RemoveAll(a => Is(a, "restore-point")) > 0;
        options.RemoveAll(a => Is(a, "quiet") || Is(a, "silent"));

        if (options.Count == 0 || options.Any(a => Is(a, "help") || a is "-h" or "/?"))
        {
            log(Usage);
            return options.Count == 0 ? 2 : 0;
        }

        if (!SystemTools.IsElevated())
        {
            log("Lockstep must run as administrator.");
            return 2;
        }

        string command = options[0];
        string? value = options.Count > 1 ? options[1] : null;

        if (Is(command, "list"))
        {
            foreach (var t in TweakCatalog.All)
                log($"{t.Id,-28} {t.Risk,-12} {t.GetStatus(),-14} {t.Title}");
            return 0;
        }

        if (Is(command, "enforce")) return Enforcement.Run(log) == 0 ? 0 : 1;

        if (Is(command, "revert-all"))
        {
            var owned = TweakCatalog.All.Where(t => t.IsOwnedBy(BackupStore.Instance)).ToList();
            return Revert(owned, log);
        }

        if (Is(command, "preset"))
        {
            var maxRisk = value?.ToLowerInvariant() switch
            {
                "recommended" => RiskLevel.Recommended,
                "limited" => RiskLevel.Limited,
                _ => (RiskLevel?)null,
            };
            if (maxRisk is null) { log("--preset needs 'recommended' or 'limited'."); return 2; }
            return Apply(TweakCatalog.All.Where(t => t.Risk <= maxRisk).ToList(), restorePoint, log);
        }

        if (Is(command, "apply") || Is(command, "revert"))
        {
            if (value is null) { log($"{command} needs a comma-separated list of tweak IDs (see --list)."); return 2; }
            var tweaks = Resolve(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), log);
            if (tweaks is null) return 2;
            return Is(command, "apply") ? Apply(tweaks, restorePoint, log) : Revert(tweaks, log);
        }

        string? profilePath = Is(command, "config") ? value : command;
        if (profilePath is not null && File.Exists(profilePath))
        {
            var profile = FuProfile.Load(profilePath);
            log($"Profile: {profile.Name ?? Path.GetFileName(profilePath)} ({profile.Tweaks.Count} tweaks, {profile.RemoveApps.Count} apps to remove)");
            var tweaks = Resolve(profile.Tweaks, log, ignoreUnknown: true);
            int code = Apply(tweaks!, restorePoint, log);
            if (profile.RemoveApps.Count > 0 && RemoveApps(profile, log) > 0) code = 1;
            return code;
        }

        log($"Unknown command or file not found: {command}\n");
        log(Usage);
        return 2;
    }

    private static bool Is(string arg, string name) =>
        arg.Equals("--" + name, StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/" + name, StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-" + name, StringComparison.OrdinalIgnoreCase);

    private static List<Tweak>? Resolve(IEnumerable<string> ids, Action<string> log, bool ignoreUnknown = false)
    {
        var byId = TweakCatalog.All.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<Tweak>();
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var t)) result.Add(t);
            else
            {
                log($"Unknown tweak ID: {id}{(ignoreUnknown ? " (skipped)" : "")}");
                if (!ignoreUnknown) return null;
            }
        }
        return result;
    }

    private static int Apply(List<Tweak> tweaks, bool restorePoint, Action<string> log)
    {
        foreach (var blocked in tweaks.Where(t => t.GetBlockReason() is not null).ToList())
        {
            log($"Skipped: {blocked.Title}: {blocked.GetBlockReason()}");
            tweaks.Remove(blocked);
        }

        var pending = tweaks.Where(t => t.GetStatus() is not (TweakStatus.Applied or TweakStatus.NotApplicable)).ToList();
        log($"{pending.Count} of {tweaks.Count} tweak(s) need applying.");
        if (pending.Count == 0) return 0;

        if (restorePoint)
        {
            log("Creating system restore point…");
            var r = SystemTools.CreateRestorePoint();
            if (!r.Success) { log($"✗ Restore point failed, nothing changed: {r.Message}"); return 1; }
        }

        var ctx = new TweakContext(BackupStore.Instance, log);
        int failures = 0;
        foreach (var t in pending)
        {
            log($"Applying: {t.Title}");
            failures += t.Apply(ctx);
        }
        Enforcement.Track(pending.Select(t => t.Id), []);
        log(failures == 0 ? "Done. Restart Windows so every change takes effect." : $"Finished with {failures} error(s).");
        return failures == 0 ? 0 : 1;
    }

    /// <returns>Number of apps that failed to uninstall.</returns>
    private static int RemoveApps(FuProfile profile, Action<string> log)
    {
        var installed = AppPackages.Load().Where(a => a.CanUninstall).ToList();
        var targets = installed.Where(a => profile.RemoveApps.Any(p => FuProfile.MatchesApp(p, a.Name))).ToList();
        log($"{targets.Count} app(s) in the profile are installed and removable.");

        int failures = 0;
        foreach (var app in targets)
        {
            log($"Uninstalling: {app.DisplayName} ({app.Name})");
            try
            {
                AppPackages.UninstallAsync(app, profile.RemoveAppsForAllUsers, profile.DeprovisionApps, log).GetAwaiter().GetResult();
                log("  ✓ removed");
            }
            catch (Exception ex)
            {
                failures++;
                log($"  ✗ {ex.Message}");
            }
        }
        return failures;
    }

    private static int Revert(List<Tweak> tweaks, Action<string> log)
    {
        var ctx = new TweakContext(BackupStore.Instance, log);
        int failures = 0;
        foreach (var t in tweaks)
        {
            log($"Reverting: {t.Title}");
            failures += t.Revert(ctx);
        }
        Enforcement.Track([], tweaks.Select(t => t.Id));
        log(failures == 0 ? $"Reverted {tweaks.Count} tweak(s)." : $"Finished with {failures} error(s).");
        return failures == 0 ? 0 : 1;
    }
}

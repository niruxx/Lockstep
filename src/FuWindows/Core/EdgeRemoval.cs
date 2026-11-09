using System.Diagnostics;
using Microsoft.Win32;

namespace FuWindows.Core;

/// <summary>
/// Uninstalls the Microsoft Edge browser using Edge's own system-level uninstaller, the same
/// approach WinUtil uses. Outside the EEA the uninstaller refuses to run unless the legacy
/// (pre-Chromium) Edge executable exists, so a placeholder is created for the duration of the
/// uninstall and removed afterwards. Nothing else is deleted by hand.
///
/// The WebView2 Runtime is a separate product used by many apps (Teams, new Outlook, Widgets,
/// Office add-ins…). It is kept by default, and so is Edge Update, which keeps WebView2 patched.
/// </summary>
public static class EdgeRemoval
{
    private const string EdgeAppGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";
    private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string EdgeUpdatePolicy = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";

    private static string ProgramFilesX86 =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string EdgeAppDir => Path.Combine(ProgramFilesX86, @"Microsoft\Edge\Application");
    private static string WebViewAppDir => Path.Combine(ProgramFilesX86, @"Microsoft\EdgeWebView\Application");

    private static string LegacyEdgeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe");

    /// <summary>Policies that stop Edge Update reinstalling the browser. WebView2 is not affected.</summary>
    private static readonly RegistryAction[] BlockReinstall =
    [
        RegistryAction.Dword(RegistryHive.LocalMachine, EdgeUpdatePolicy, $"Install{EdgeAppGuid}", 0),
        RegistryAction.Dword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1),
    ];

    public sealed record Status(string? EdgeVersion, string? WebView2Version, bool ReinstallBlocked, IReadOnlyList<string> OtherBrowsers)
    {
        public bool EdgeInstalled => EdgeVersion is not null;
    }

    public static Status GetStatus() => new(
        InstalledVersion(EdgeAppDir, "msedge.exe", EdgeAppGuid),
        InstalledVersion(WebViewAppDir, "msedgewebview2.exe", WebView2Guid),
        BlockReinstall.All(a => a.GetState() == ActionState.Applied),
        OtherBrowsers());

    /// <summary>Highest version folder that actually contains the executable.</summary>
    private static string? InstalledVersion(string appDir, string exe, string clientGuid)
    {
        if (Directory.Exists(appDir))
        {
            var best = Directory.GetDirectories(appDir)
                .Select(d => (Dir: d, Ok: Version.TryParse(Path.GetFileName(d), out var v), Ver: v))
                .Where(x => x.Ok && File.Exists(Path.Combine(x.Dir, exe)))
                .OrderByDescending(x => x.Ver)
                .FirstOrDefault();
            if (best.Ver is not null) return best.Ver.ToString();
        }

        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{clientGuid}");
        var pv = key?.GetValue("pv") as string;
        return string.IsNullOrWhiteSpace(pv) || pv == "0.0.0.0" ? null : pv;
    }

    private static string? FindSetup(string appDir)
    {
        if (!Directory.Exists(appDir)) return null;
        return Directory.GetDirectories(appDir)
            .Where(d => Version.TryParse(Path.GetFileName(d), out _))
            .OrderByDescending(d => Version.Parse(Path.GetFileName(d)))
            .Select(d => Path.Combine(d, "Installer", "setup.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Registered web browsers other than Edge (HKLM and HKCU StartMenuInternet).</summary>
    public static List<string> OtherBrowsers()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (sub.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                        sub.Contains("IEXPLORE", StringComparison.OrdinalIgnoreCase)) continue;

                    using var browser = key.OpenSubKey(sub);
                    using var command = browser?.OpenSubKey(@"shell\open\command");
                    var target = (command?.GetValue(null) as string)?.Trim('"', ' ');
                    var exe = target is null ? null : target.Split('"')[0];
                    if (exe is not null && !File.Exists(Environment.ExpandEnvironmentVariables(exe))) continue;

                    names.Add(browser?.GetValue(null) as string ?? sub);
                }
            }
        }
        return names.ToList();
    }

    public sealed record Options(bool KeepWebView2, bool DeleteBrowsingData, bool BlockReinstall);

    /// <summary>
    /// Uninstalls Edge (and optionally WebView2). Throws if a safety check fails before anything
    /// has been changed.
    /// </summary>
    public static void Uninstall(Options options, Action<string> log)
    {
        // --- Safety checks: nothing has been touched yet ------------------------------------
        var browsers = OtherBrowsers();
        if (browsers.Count == 0)
            throw new InvalidOperationException(
                "No other web browser is installed. Install one (e.g. Firefox, Chrome or Brave) and make it your default before removing Edge.");

        var edgeSetup = FindSetup(EdgeAppDir)
            ?? throw new InvalidOperationException("Microsoft Edge isn't installed (its uninstaller wasn't found).");

        log($"  Other browsers found: {string.Join(", ", browsers)}");

        // --- Close the browser, but never WebView2 hosts ------------------------------------
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }

        // --- Unlock the official uninstaller --------------------------------------------------
        var placeholder = Path.Combine(LegacyEdgeDir, "MicrosoftEdge.exe");
        bool createdDir = !Directory.Exists(LegacyEdgeDir);
        bool createdFile = !File.Exists(placeholder);
        try
        {
            if (createdDir) Directory.CreateDirectory(LegacyEdgeDir);
            if (createdFile) File.WriteAllBytes(placeholder, []);

            var args = "--uninstall --system-level --force-uninstall --verbose-logging" +
                       (options.DeleteBrowsingData ? " --delete-profile" : "");
            log($"  Running Edge's uninstaller ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(edgeSetup)))})…");
            var result = ProcessRunner.Run(edgeSetup, args, timeoutMs: 600_000);
            log($"  Uninstaller exit code {result.ExitCode}");
        }
        finally
        {
            // Leave SystemApps exactly as it was.
            try
            {
                if (createdFile && File.Exists(placeholder)) File.Delete(placeholder);
                if (createdDir && Directory.Exists(LegacyEdgeDir) && !Directory.EnumerateFileSystemEntries(LegacyEdgeDir).Any())
                    Directory.Delete(LegacyEdgeDir);
            }
            catch (Exception ex) { log($"  ! couldn't remove the temporary placeholder {placeholder}: {ex.Message}"); }
        }

        if (InstalledVersion(EdgeAppDir, "msedge.exe", EdgeAppGuid) is { } still && File.Exists(Path.Combine(EdgeAppDir, still, "msedge.exe")))
            throw new InvalidOperationException($"Edge {still} is still installed. See %TEMP%\\msedge_installer.log or %ProgramData%\\Microsoft\\EdgeUpdate\\Log for details.");
        log("  ✓ Microsoft Edge removed");

        // --- Optional extras -------------------------------------------------------------------
        if (options.BlockReinstall)
        {
            var ctx = new TweakContext(BackupStore.Instance, log);
            foreach (var a in BlockReinstall) a.Apply(ctx);
            log("  ✓ Edge Update will not reinstall the browser (WebView2 still updates)");
        }

        if (!options.KeepWebView2)
        {
            var webViewSetup = FindSetup(WebViewAppDir);
            if (webViewSetup is null)
            {
                log("  WebView2 Runtime isn't installed; nothing to remove.");
            }
            else
            {
                log("  Removing WebView2 Runtime…");
                var r = ProcessRunner.Run(webViewSetup, "--uninstall --msedgewebview --system-level --force-uninstall --verbose-logging", 600_000);
                log(InstalledVersion(WebViewAppDir, "msedgewebview2.exe", WebView2Guid) is null
                    ? "  ✓ WebView2 Runtime removed"
                    : $"  ! WebView2 Runtime is still installed (exit code {r.ExitCode}). Windows may be protecting it.");
            }
        }
        else
        {
            log("  WebView2 Runtime and Edge Update kept, so apps that embed web content keep working and stay patched.");
        }
    }

    /// <summary>Lifts the reinstall block and installs Edge again with winget.</summary>
    public static void Reinstall(Action<string> log)
    {
        var ctx = new TweakContext(BackupStore.Instance, log);
        foreach (var a in BlockReinstall) a.Revert(ctx);
        log("  ✓ reinstall block lifted");

        var winget = SystemTools.FindWinget();
        if (winget is null)
        {
            Process.Start(new ProcessStartInfo("https://www.microsoft.com/edge/download") { UseShellExecute = true });
            log("  winget isn't available; opened the Edge download page instead.");
            return;
        }

        log("  Installing Microsoft Edge with winget (this can take a few minutes)…");
        var r = ProcessRunner.Run(winget,
            "install --id Microsoft.Edge -e --source winget --silent --accept-package-agreements --accept-source-agreements",
            900_000);
        if (InstalledVersion(EdgeAppDir, "msedge.exe", EdgeAppGuid) is { } v)
            log($"  ✓ Microsoft Edge {v} installed");
        else
            throw new InvalidOperationException($"winget couldn't install Edge (exit code {r.ExitCode}): {r.Message}");
    }
}

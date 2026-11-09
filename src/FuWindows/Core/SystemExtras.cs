using System.Diagnostics;

namespace FuWindows.Core;

/// <summary>DNS provider picker for every connected network adapter.</summary>
public static class DnsTools
{
    public sealed record Provider(string Name, string Description, string[] Servers);

    public static IReadOnlyList<Provider> Providers { get; } =
    [
        new("Automatic", "Whatever your router or ISP hands out (Windows default).", []),
        new("Quad9", "Non-profit, no logging of IPs, blocks known malicious domains.",
            ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"]),
        new("Cloudflare", "Fast, logs purged within 24 hours, no filtering.",
            ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"]),
        new("Cloudflare (malware blocking)", "Cloudflare with malware domains blocked.",
            ["1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002"]),
        new("AdGuard", "Blocks ads and trackers at the DNS level.",
            ["94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"]),
        new("Google", "Fast and reliable, but Google keeps some logs.",
            ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"]),
    ];

    /// <summary>Adapters with a default gateway, i.e. the ones actually used for the internet.</summary>
    private const string ConnectedAdapters =
        "$ifs = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -or $_.IPv6DefaultGateway } | Select-Object -ExpandProperty InterfaceIndex -Unique\n";

    public static string Apply(Provider provider)
    {
        string set = provider.Servers.Length == 0
            ? "Set-DnsClientServerAddress -InterfaceIndex $i -ResetServerAddresses"
            : $"Set-DnsClientServerAddress -InterfaceIndex $i -ServerAddresses @({string.Join(",", provider.Servers.Select(s => $"'{s}'"))})";

        var r = ProcessRunner.PowerShell(ConnectedAdapters + $$"""
            if (-not $ifs) { throw 'No connected network adapter found.' }
            foreach ($i in $ifs) {
              {{set}}
              $a = Get-NetAdapter -InterfaceIndex $i
              "  $($a.Name): $(((Get-DnsClientServerAddress -InterfaceIndex $i).ServerAddresses | Select-Object -Unique) -join ', ')"
            }
            Clear-DnsClientCache
            """, 120_000);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        return r.Output.TrimEnd();
    }

    public static string Current()
    {
        var r = ProcessRunner.PowerShell(ConnectedAdapters + """
            foreach ($i in $ifs) {
              $a = Get-NetAdapter -InterfaceIndex $i
              "$($a.Name): $(((Get-DnsClientServerAddress -InterfaceIndex $i).ServerAddresses | Select-Object -Unique) -join ', ')"
            }
            """, 60_000);
        return r.Success && r.Output.Trim().Length > 0 ? r.Output.Trim() : "No connected adapter found.";
    }
}

/// <summary>OneDrive (a Win32 app) uninstall and reinstall, using its own uninstaller or winget.</summary>
public static class OneDriveRemoval
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static IEnumerable<string> ExeCandidates =>
    [
        Path.Combine(LocalAppData, @"Microsoft\OneDrive\OneDrive.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft OneDrive\OneDrive.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft OneDrive\OneDrive.exe"),
    ];

    public static string? InstalledPath() => ExeCandidates.FirstOrDefault(File.Exists);

    private static string? FindSetup()
    {
        var perUser = Path.Combine(LocalAppData, @"Microsoft\OneDrive");
        var candidates = new List<string>();
        if (Directory.Exists(perUser))
            candidates.AddRange(Directory.GetFiles(perUser, "OneDriveSetup.exe", SearchOption.AllDirectories));
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var dir = Path.Combine(root, "Microsoft OneDrive");
            if (Directory.Exists(dir)) candidates.AddRange(Directory.GetFiles(dir, "OneDriveSetup.exe", SearchOption.AllDirectories));
        }
        candidates.Add(ProcessRunner.System32("OneDriveSetup.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SysWOW64\OneDriveSetup.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void Uninstall(Action<string> log)
    {
        if (InstalledPath() is null) throw new InvalidOperationException("OneDrive isn't installed.");

        foreach (var p in Process.GetProcessesByName("OneDrive"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }

        bool machineWide = InstalledPath()!.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                        || InstalledPath()!.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);

        if (FindSetup() is { } setup)
        {
            log($"  Running {setup} /uninstall…");
            ProcessRunner.Run(setup, machineWide ? "/uninstall /allusers" : "/uninstall", 600_000);
        }

        if (InstalledPath() is not null && SystemTools.FindWinget() is { } winget)
        {
            log("  Trying winget…");
            ProcessRunner.Run(winget, "uninstall --id Microsoft.OneDrive -e --silent --accept-source-agreements", 600_000);
        }

        if (InstalledPath() is { } still) throw new InvalidOperationException($"OneDrive is still installed ({still}).");
        log("  ✓ OneDrive uninstalled. Your OneDrive folder and its downloaded files were left in place.");
    }

    public static void Reinstall(Action<string> log)
    {
        var winget = SystemTools.FindWinget();
        if (winget is null)
        {
            Process.Start(new ProcessStartInfo("https://www.microsoft.com/microsoft-365/onedrive/download") { UseShellExecute = true });
            log("  winget isn't available; opened the OneDrive download page.");
            return;
        }
        log("  Installing OneDrive with winget…");
        var r = ProcessRunner.Run(winget,
            "install --id Microsoft.OneDrive -e --source winget --silent --accept-package-agreements --accept-source-agreements", 900_000);
        if (InstalledPath() is null)
            throw new InvalidOperationException($"winget couldn't install OneDrive (exit code {r.ExitCode}): {r.Message}");
        log("  ✓ OneDrive installed. Start it from the Start menu to sign in.");
    }
}

/// <summary>Repair tools, Windows optional features and classic control panels (Tools page).</summary>
public static class RepairTools
{
    public static string ResetWindowsUpdate()
    {
        var r = ProcessRunner.PowerShell("""
            $svcs = 'wuauserv','bits','cryptsvc','usosvc','msiserver'
            foreach ($s in $svcs) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }
            $stamp = Get-Date -Format yyyyMMdd-HHmmss
            $renamed = @()
            foreach ($p in @("$env:SystemRoot\SoftwareDistribution", "$env:SystemRoot\System32\catroot2")) {
              if (Test-Path $p) {
                try { Rename-Item $p "$(Split-Path $p -Leaf).bak-$stamp" -ErrorAction Stop; $renamed += $p }
                catch { "  Could not rename $p (in use): $($_.Exception.Message)" }
              }
            }
            foreach ($s in $svcs) { Start-Service -Name $s -ErrorAction SilentlyContinue }
            "  Reset: $($renamed -join ', '). Old folders kept as *.bak-$stamp; delete them once updates work."
            """, 300_000);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        return r.Output.TrimEnd();
    }

    public static string ResetNetwork()
    {
        var output = new List<string>();
        foreach (var (exe, args) in new[]
        {
            ("netsh.exe", "winsock reset"), ("netsh.exe", "int ip reset"), ("ipconfig.exe", "/flushdns"),
        })
        {
            var r = ProcessRunner.Run(ProcessRunner.System32(exe), args);
            output.Add($"  {(r.Success ? "✓" : "!")} {exe} {args}");
        }
        output.Add("  Restart Windows to finish the network reset.");
        return string.Join("\n", output);
    }

    /// <summary>Opens a console running DISM RestoreHealth then SFC; both take a while.</summary>
    public static void RepairSystemFiles() =>
        Process.Start(new ProcessStartInfo(ProcessRunner.System32("cmd.exe"),
            "/k title FuWindows - repair system files & DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow")
        { UseShellExecute = true });

    public static void ReinstallWinget() =>
        Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1") { UseShellExecute = true });

    // --- Windows optional features ------------------------------------------------------

    public sealed record Feature(string Title, string Description, string[] Names, bool Recommended = false);

    public static IReadOnlyList<Feature> Features { get; } =
    [
        new("SMB 1.0 (turn OFF)", "Obsolete file-sharing protocol used by WannaCry. Should be off unless an ancient NAS needs it.", ["SMB1Protocol"]),
        new("Windows Subsystem for Linux", "Run Linux distributions (also turns on Virtual Machine Platform).", ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"]),
        new("Windows Sandbox", "Disposable, isolated desktop for running untrusted programs. Pro and above.", ["Containers-DisposableClientVM"]),
        new("Hyper-V", "Microsoft's virtual machine platform. Pro and above.", ["Microsoft-Hyper-V-All"]),
        new(".NET Framework 3.5", "Needed by some older programs and games (includes .NET 2.0 and 3.0).", ["NetFx3"]),
        new("Legacy components (DirectPlay)", "Needed by some older games.", ["LegacyComponents", "DirectPlay"]),
        new("Windows Media Player (legacy)", "The classic Windows Media Player and media playback components.", ["MediaPlayback", "WindowsMediaPlayer"]),
        new("NFS client", "Mount Network File System shares from Linux servers and NAS devices.", ["ServicesForNFS-ClientOnly", "ClientForNFS-Infrastructure"]),
        new("Telnet client", "Command-line Telnet client for testing network ports.", ["TelnetClient"]),
    ];

    /// <summary>Feature name to enabled state (missing = not available on this edition).</summary>
    public static Dictionary<string, bool> FeatureStates()
    {
        var names = string.Join(",", Features.SelectMany(f => f.Names).Select(n => $"'{n}'"));
        var r = ProcessRunner.PowerShell($$"""
            foreach ($n in @({{names}})) {
              $f = Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue
              if ($f) { "$n=$($f.State)" }
            }
            """, 180_000);
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2) states[parts[0]] = parts[1].StartsWith("Enabled", StringComparison.OrdinalIgnoreCase);
        }
        return states;
    }

    /// <returns>True if a restart is needed.</returns>
    public static bool SetFeature(Feature feature, bool enable, Dictionary<string, bool> states, Action<string> log)
    {
        bool restart = false;
        foreach (var name in feature.Names.Where(states.ContainsKey))
        {
            var args = enable
                ? $"/Online /Enable-Feature /FeatureName:{name} /All /NoRestart"
                : $"/Online /Disable-Feature /FeatureName:{name} /NoRestart";
            var r = ProcessRunner.Run(ProcessRunner.System32("dism.exe"), args, 1_800_000);
            // 3010 = success, restart required.
            if (r.ExitCode == 3010) restart = true;
            else if (!r.Success) throw new InvalidOperationException($"{name}: DISM exit code {r.ExitCode}. {r.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
            log($"  ✓ {name} {(enable ? "enabled" : "disabled")}");
        }
        return restart;
    }

    // --- Classic panels -------------------------------------------------------------------

    public static IReadOnlyList<(string Title, string File, string Args)> Panels { get; } =
    [
        ("Control Panel", "control.exe", ""),
        ("Programs and Features", "appwiz.cpl", ""),
        ("System Properties", "sysdm.cpl", ""),
        ("Network Connections", "ncpa.cpl", ""),
        ("Windows Defender Firewall", "firewall.cpl", ""),
        ("Power Options", "powercfg.cpl", ""),
        ("Sound", "mmsys.cpl", ""),
        ("Mouse", "main.cpl", ""),
        ("Region", "intl.cpl", ""),
        ("Date and Time", "timedate.cpl", ""),
        ("Devices and Printers", "control.exe", "printers"),
        ("Security and Maintenance", "wscui.cpl", ""),
        ("System Restore", "rstrui.exe", ""),
        ("Computer Management", "compmgmt.msc", ""),
        ("Device Manager", "devmgmt.msc", ""),
        ("Services", "services.msc", ""),
        ("Event Viewer", "eventvwr.msc", ""),
        ("Task Scheduler", "taskschd.msc", ""),
        ("Registry Editor", "regedit.exe", ""),
    ];

    public static void OpenPanel(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
}

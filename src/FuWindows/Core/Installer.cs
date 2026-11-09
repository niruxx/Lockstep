using System.Diagnostics;
using System.Text;

namespace FuWindows.Core;

/// <summary>
/// The Install section: package managers (winget, Chocolatey, Scoop, Homebrew via WSL) and a
/// curated app catalog installed silently with winget or Chocolatey, in the spirit of WinUtil.
/// Anything that downloads and runs an installer script opens in a visible window.
/// </summary>
public static class Installer
{
    // -------------------------------------------------------------------------------------
    // Package managers
    // -------------------------------------------------------------------------------------

    public sealed record ManagerStatus(string Id, string Name, string Description, string? Version, string? Note);

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static string? ChocoPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine);
        foreach (var root in new[] { fromEnv, Path.Combine(ProgramData, "chocolatey") })
        {
            if (root is null) continue;
            var exe = Path.Combine(root, "bin", "choco.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static string? ScoopPath()
    {
        var root = Environment.GetEnvironmentVariable("SCOOP", EnvironmentVariableTarget.User) ?? Path.Combine(UserProfile, "scoop");
        var shim = Path.Combine(root, "shims", "scoop.ps1");
        return File.Exists(shim) ? shim : null;
    }

    /// <summary>Installed WSL distributions (empty if WSL isn't set up).</summary>
    public static List<string> WslDistros()
    {
        var wsl = ProcessRunner.System32("wsl.exe");
        if (!File.Exists(wsl)) return [];
        // wsl.exe writes UTF-16 to redirected output.
        var r = ProcessRunner.Run(wsl, "--list --quiet", 30_000, Encoding.Unicode);
        if (!r.Success) return [];
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(d => d.Length > 0 && !d.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                       .ToList();
    }

    private const string BrewPath = "/home/linuxbrew/.linuxbrew/bin/brew";

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    public static List<ManagerStatus> GetManagers()
    {
        var list = new List<ManagerStatus>();

        var winget = SystemTools.FindWinget();
        list.Add(new("winget", "winget", "Microsoft's package manager, built into Windows 11 (App Installer). Used for the app catalog by default.",
            winget is null ? null : FirstLine(ProcessRunner.Run(winget, "--version", 30_000).Output), null));

        var choco = ChocoPath();
        list.Add(new("choco", "Chocolatey", "The long-standing community package manager for Windows, with 10,000+ packages.",
            choco is null ? null : FirstLine(ProcessRunner.Run(choco, "--version", 30_000).Output), null));

        var scoop = ScoopPath();
        list.Add(new("scoop", "Scoop", "Homebrew-style command-line installer for Windows: per-user, no admin prompts, portable apps.",
            scoop is null ? null : "installed", null));

        var distros = WslDistros();
        string? brew = null;
        if (distros.Count > 0)
        {
            var r = ProcessRunner.Run(ProcessRunner.System32("wsl.exe"), $"-e sh -c \"test -x {BrewPath} && {BrewPath} --version\"", 60_000);
            if (r.Success) brew = FirstLine(r.Output);
        }
        list.Add(new("brew", "Homebrew (in WSL)", "Homebrew only runs on macOS and Linux, so it's installed inside WSL, the Linux environment built into Windows.",
            brew,
            distros.Count == 0 ? "WSL isn't set up yet. Install sets up WSL with Ubuntu first (needs a restart), then install Homebrew."
                               : $"Uses your WSL distribution: {distros[0]}."));
        return list;
    }

    /// <summary>Starts a visible PowerShell window running a script, so you can watch what it does.</summary>
    private static void VisiblePowerShell(string title, string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes($"$Host.UI.RawUI.WindowTitle = '{title}'\n" + script));
        Process.Start(new ProcessStartInfo(ProcessRunner.System32(@"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NoExit -ExecutionPolicy Bypass -EncodedCommand {encoded}") { UseShellExecute = true });
    }

    /// <summary>Starts the installer for a package manager. Returns what was started, for the log.</summary>
    public static string InstallManager(string id)
    {
        switch (id)
        {
            case "winget":
                Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1") { UseShellExecute = true });
                return "Opened App Installer (winget) in the Microsoft Store. Press Get or Update there.";

            case "choco":
                if (SystemTools.FindWinget() is { } winget)
                {
                    VisiblePowerShell("FuWindows - install Chocolatey", $"""
                        Write-Host 'Installing Chocolatey with winget (package Chocolatey.Chocolatey)...' -ForegroundColor Cyan
                        & '{winget}' install --id Chocolatey.Chocolatey -e --source winget --accept-package-agreements --accept-source-agreements
                        Write-Host "`nDone. Close this window and press Refresh in FuWindows." -ForegroundColor Green
                        """);
                    return "Installing Chocolatey with winget in a separate window.";
                }
                VisiblePowerShell("FuWindows - install Chocolatey", """
                    Write-Host 'Running the official installer from https://community.chocolatey.org/install.ps1' -ForegroundColor Cyan
                    Set-ExecutionPolicy Bypass -Scope Process -Force
                    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
                    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
                    Write-Host "`nDone. Close this window and press Refresh in FuWindows." -ForegroundColor Green
                    """);
                return "Running Chocolatey's official installer in a separate window.";

            case "scoop":
                VisiblePowerShell("FuWindows - install Scoop", """
                    Write-Host 'Running the official installer from https://get.scoop.sh' -ForegroundColor Cyan
                    Write-Host 'Scoop needs the CurrentUser execution policy set to RemoteSigned.' -ForegroundColor Yellow
                    Set-ExecutionPolicy -Scope CurrentUser RemoteSigned -Force
                    $installer = Join-Path $env:TEMP 'install-scoop.ps1'
                    Invoke-RestMethod -Uri 'https://get.scoop.sh' -OutFile $installer
                    # FuWindows runs elevated, so Scoop must be told that's intended.
                    & $installer -RunAsAdmin
                    Remove-Item $installer -ErrorAction SilentlyContinue
                    Write-Host "`nDone. Open a new terminal and run: scoop help" -ForegroundColor Green
                    """);
                return "Running Scoop's official installer in a separate window.";

            case "brew":
                if (WslDistros().Count == 0)
                {
                    Process.Start(new ProcessStartInfo(ProcessRunner.System32("cmd.exe"),
                        "/k title FuWindows - install WSL & wsl.exe --install & echo. & echo Restart Windows, finish the Ubuntu setup (pick a username and password), then press Install for Homebrew again.")
                    { UseShellExecute = true });
                    return "Setting up WSL with Ubuntu in a separate window. Restart when it finishes, then install Homebrew.";
                }

                // Visible WSL window: the installer asks for your Linux (sudo) password.
                const string script =
                    "echo 'Running the official installer from https://brew.sh'; " +
                    "if /bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"; then " +
                    "grep -q linuxbrew ~/.bashrc || echo 'eval \"$(" + BrewPath + " shellenv)\"' >> ~/.bashrc; " +
                    "echo; echo 'Homebrew installed. Open a new WSL terminal and run: brew --version'; fi; exec bash";
                var psi = new ProcessStartInfo(ProcessRunner.System32("wsl.exe")) { UseShellExecute = false, CreateNoWindow = false };
                foreach (var arg in new[] { "--", "bash", "-lc", script }) psi.ArgumentList.Add(arg);
                Process.Start(psi);
                return "Running Homebrew's official installer inside WSL in a separate window (it asks for your Linux password).";

            default:
                throw new ArgumentException($"Unknown package manager: {id}");
        }
    }

    /// <summary>Upgrades everything installed by a package manager, in a visible window.</summary>
    public static void UpgradeAll(string id)
    {
        switch (id)
        {
            case "winget" when SystemTools.FindWinget() is { } winget:
                VisiblePowerShell("FuWindows - winget upgrade", $"& '{winget}' upgrade --all --include-unknown --accept-package-agreements --accept-source-agreements");
                break;
            case "choco" when ChocoPath() is { } choco:
                VisiblePowerShell("FuWindows - choco upgrade", $"& '{choco}' upgrade all -y");
                break;
            case "scoop" when ScoopPath() is { } scoop:
                VisiblePowerShell("FuWindows - scoop update", $"& '{scoop}' update; & '{scoop}' update *");
                break;
            case "brew":
                var psi = new ProcessStartInfo(ProcessRunner.System32("wsl.exe")) { UseShellExecute = false, CreateNoWindow = false };
                foreach (var arg in new[] { "--", "bash", "-lc", $"{BrewPath} update && {BrewPath} upgrade; exec bash" }) psi.ArgumentList.Add(arg);
                Process.Start(psi);
                break;
            default:
                throw new InvalidOperationException("That package manager isn't installed.");
        }
    }

    // -------------------------------------------------------------------------------------
    // App catalog
    // -------------------------------------------------------------------------------------

    public sealed record CatalogApp(
        string Name, string Category, string Description, string WingetId, string? ChocoId,
        string WingetSource = "winget");

    public static IReadOnlyList<CatalogApp> Catalog { get; } =
    [
        // Browsers
        new("Firefox", "Browsers", "Mozilla's independent, privacy-focused browser.", "Mozilla.Firefox", "firefox"),
        new("Brave", "Browsers", "Chromium-based browser with built-in ad and tracker blocking.", "Brave.Brave", "brave"),
        new("LibreWolf", "Browsers", "Firefox fork with telemetry removed and hardened privacy defaults.", "LibreWolf.LibreWolf", "librewolf"),
        new("Tor Browser", "Browsers", "Browses through the Tor network for anonymity.", "TorProject.TorBrowser", "tor-browser"),
        new("Vivaldi", "Browsers", "Highly customisable Chromium-based browser.", "Vivaldi.Vivaldi", "vivaldi"),
        new("Google Chrome", "Browsers", "Google's browser.", "Google.Chrome", "googlechrome"),

        // Privacy & security
        new("Bitwarden", "Privacy & security", "Open-source password manager with sync.", "Bitwarden.Bitwarden", "bitwarden"),
        new("KeePassXC", "Privacy & security", "Offline, open-source password manager.", "KeePassXCTeam.KeePassXC", "keepassxc"),
        new("Mullvad VPN", "Privacy & security", "No-account, no-logs VPN.", "MullvadVPN.MullvadVPN", null),
        new("Proton VPN", "Privacy & security", "Swiss VPN with a free tier.", "Proton.ProtonVPN", "protonvpn"),
        new("WireGuard", "Privacy & security", "Fast, modern VPN client.", "WireGuard.WireGuard", "wireguard"),
        new("VeraCrypt", "Privacy & security", "Encrypted volumes and full-disk encryption.", "IDRIX.VeraCrypt", "veracrypt"),
        new("simplewall", "Privacy & security", "Simple firewall front-end to block apps from going online.", "Henry++.simplewall", "simplewall"),
        new("Malwarebytes", "Privacy & security", "On-demand malware scanner.", "Malwarebytes.Malwarebytes", "malwarebytes"),
        new("Signal", "Privacy & security", "End-to-end encrypted messenger.", "OpenWhisperSystems.Signal", "signal"),

        // Communication
        new("Discord", "Communication", "Voice, video and text chat for communities.", "Discord.Discord", "discord"),
        new("Telegram", "Communication", "Cloud-based messenger.", "Telegram.TelegramDesktop", "telegram"),
        new("Thunderbird", "Communication", "Mozilla's email, calendar and contacts client.", "Mozilla.Thunderbird", "thunderbird"),
        new("Zoom", "Communication", "Video meetings.", "Zoom.Zoom", "zoom"),

        // Development
        new("Visual Studio Code", "Development", "Lightweight code editor.", "Microsoft.VisualStudioCode", "vscode"),
        new("Git", "Development", "Distributed version control.", "Git.Git", "git"),
        new("GitHub Desktop", "Development", "Git GUI for GitHub.", "GitHub.GitHubDesktop", "github-desktop"),
        new("PowerShell 7", "Development", "Modern cross-platform PowerShell.", "Microsoft.PowerShell", "powershell-core"),
        new("Windows Terminal", "Development", "Tabbed terminal for PowerShell, cmd and WSL.", "Microsoft.WindowsTerminal", "microsoft-windows-terminal"),
        new("Node.js LTS", "Development", "JavaScript runtime (long-term support).", "OpenJS.NodeJS.LTS", "nodejs-lts"),
        new("Python 3.12", "Development", "Python interpreter.", "Python.Python.3.12", "python312"),
        new("Notepad++", "Development", "Fast text and code editor.", "Notepad++.Notepad++", "notepadplusplus"),
        new("Docker Desktop", "Development", "Containers on Windows (needs WSL 2).", "Docker.DockerDesktop", "docker-desktop"),
        new("JetBrains Toolbox", "Development", "Installs and updates JetBrains IDEs.", "JetBrains.Toolbox", "jetbrainstoolbox"),

        // Utilities
        new("7-Zip", "Utilities", "Archive manager for zip, 7z, rar and more.", "7zip.7zip", "7zip"),
        new("PowerToys", "Utilities", "Microsoft's power-user utilities (FancyZones, PowerRename, Run…).", "Microsoft.PowerToys", "powertoys"),
        new("Everything", "Utilities", "Instant file-name search.", "voidtools.Everything", "everything"),
        new("ShareX", "Utilities", "Screenshots, screen recording and uploads.", "ShareX.ShareX", "sharex"),
        new("WinDirStat", "Utilities", "Shows what's using your disk space.", "WinDirStat.WinDirStat", "windirstat"),
        new("Rufus", "Utilities", "Creates bootable USB drives.", "Rufus.Rufus", "rufus"),
        new("AutoHotkey", "Utilities", "Keyboard and mouse automation scripts.", "AutoHotkey.AutoHotkey", "autohotkey"),
        new("CPU-Z", "Utilities", "CPU, memory and motherboard details.", "CPUID.CPU-Z", "cpu-z"),
        new("HWiNFO", "Utilities", "Detailed hardware info and sensor monitoring.", "REALiX.HWiNFO", "hwinfo"),

        // Media
        new("VLC", "Media", "Plays almost any audio or video file.", "VideoLAN.VLC", "vlc"),
        new("Spotify", "Media", "Music streaming. Installed from the Microsoft Store (the desktop installer refuses to run as admin).", "9NCBCSZSJRSB", null, "msstore"),
        new("OBS Studio", "Media", "Screen recording and live streaming.", "OBSProject.OBSStudio", "obs-studio"),
        new("Audacity", "Media", "Audio editor and recorder.", "Audacity.Audacity", "audacity"),
        new("GIMP", "Media", "Image editor.", "GIMP.GIMP", "gimp"),
        new("HandBrake", "Media", "Video converter.", "HandBrake.HandBrake", "handbrake"),
        new("foobar2000", "Media", "Lightweight, customisable music player.", "PeterPawlowski.foobar2000", "foobar2000"),

        // Gaming
        new("Steam", "Gaming", "Valve's game store and launcher.", "Valve.Steam", "steam"),
        new("Epic Games Launcher", "Gaming", "Epic Games store and launcher.", "EpicGames.EpicGamesLauncher", "epicgameslauncher"),
        new("GOG Galaxy", "Gaming", "GOG's DRM-free game launcher.", "GOG.Galaxy", "goggalaxy"),
        new("EA app", "Gaming", "Electronic Arts' launcher.", "ElectronicArts.EADesktop", "ea-app"),
        new("Ubisoft Connect", "Gaming", "Ubisoft's launcher.", "Ubisoft.Connect", "ubisoft-connect"),
        new("Battle.net", "Gaming", "Blizzard's launcher.", "Blizzard.BattleNet", null),
        new("Playnite", "Gaming", "One library for all your game launchers.", "Playnite.Playnite", "playnite"),
        new("MSI Afterburner", "Gaming", "GPU overclocking and on-screen FPS overlay.", "Guru3D.Afterburner", "msiafterburner"),

        // Office & documents
        new("LibreOffice", "Office & documents", "Free office suite (Writer, Calc, Impress).", "TheDocumentFoundation.LibreOffice", "libreoffice-fresh"),
        new("Obsidian", "Office & documents", "Markdown notes stored as local files.", "Obsidian.Obsidian", "obsidian"),
        new("SumatraPDF", "Office & documents", "Tiny, fast PDF and e-book reader.", "SumatraPDF.SumatraPDF", "sumatrapdf"),
        new("Adobe Acrobat Reader", "Office & documents", "Adobe's PDF reader.", "Adobe.Acrobat.Reader.64-bit", "adobereader"),
    ];

    /// <summary>IDs of installed apps per manager, from one `winget list` / `choco list` each.</summary>
    public static HashSet<string> InstalledIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (SystemTools.FindWinget() is { } winget)
        {
            var r = ProcessRunner.Run(winget, "list --accept-source-agreements --disable-interactivity", 180_000, Encoding.UTF8);
            foreach (var app in Catalog)
                if (r.Output.Contains(app.WingetId, StringComparison.OrdinalIgnoreCase)) ids.Add(app.WingetId);
        }

        if (ChocoPath() is { } choco)
        {
            var r = ProcessRunner.Run(choco, "list --limit-output", 120_000);
            var local = new HashSet<string>(
                r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|')[0]),
                StringComparer.OrdinalIgnoreCase);
            foreach (var app in Catalog)
                if (app.ChocoId is { } c && local.Contains(c)) ids.Add(app.WingetId);
        }
        return ids;
    }

    /// <summary>Installs one app silently. Throws with the package manager's message on failure.</summary>
    public static void InstallApp(CatalogApp app, string manager, Action<string> log)
    {
        if (manager == "choco" && app.ChocoId is { } chocoId)
        {
            var choco = ChocoPath() ?? throw new InvalidOperationException("Chocolatey isn't installed.");
            var r = ProcessRunner.Run(choco, $"install {chocoId} -y --no-progress", 1_800_000);
            if (r.ExitCode is not (0 or 3010 or 1641)) throw new InvalidOperationException(LastLine(r) ?? $"choco exit code {r.ExitCode}");
            log($"  ✓ installed with Chocolatey ({chocoId})");
            return;
        }

        var winget = SystemTools.FindWinget()
            ?? throw new InvalidOperationException("winget isn't installed. Install App Installer from the Package managers card.");
        var result = ProcessRunner.Run(winget,
            $"install --id {app.WingetId} -e --source {app.WingetSource} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            1_800_000, Encoding.UTF8);
        // -1978335189 = already installed / no newer version.
        if (result.ExitCode is not (0 or -1978335189))
            throw new InvalidOperationException(LastLine(result) ?? $"winget exit code {result.ExitCode}");
        log(manager == "choco"
            ? $"  ✓ installed with winget (no Chocolatey package) ({app.WingetId})"
            : $"  ✓ installed with winget ({app.WingetId})");
    }

    private static string? LastLine(ProcessResult r) =>
        (r.Output + "\n" + r.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 3 && !l.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒'));
}

# FuWindows roadmap

This file lists what FuWindows is missing compared to other tools, as a basis for planning. For what it does today, see [README.md](README.md).

Compared on 2026-11-08 against:

- [O&O ShutUp10++ features](https://www.oo-software.com/en/shutup10/features)
- [O&O AppBuster features](https://www.oo-software.com/en/ooappbuster/features)
- [WinUtil](https://github.com/christitustech/winutil), using its [`config/tweaks.json`](https://github.com/ChrisTitusTech/winutil/blob/main/config/tweaks.json), [`config/feature.json`](https://github.com/ChrisTitusTech/winutil/blob/main/config/feature.json) and [documentation](https://winutil.christitus.com/)

The O&O pages list categories rather than individual settings, so the "missing" column for them is our own reading of what each category covers.

Legend: ✅ has it · 🟡 partial · ❌ missing · ➖ out of scope for a privacy tool (listed for completeness)

## O&O ShutUp10++

**Program features**

| ShutUp10++ feature | FuWindows | Gap |
|---|---|---|
| All settings in one place | ✅ | |
| Recommendation level per option | ✅ | Four levels instead of three. |
| Quick presets | ✅ | *Recommended* and *Recommended + Limited*. |
| Explanation of each option | ✅ | Also lists the exact registry/service/task/host changes. |
| Undo a single option / reset all | ✅ | |
| Create a restore point | ✅ | |
| Export / import your own configuration | ✅ | Tools → *Profiles*: `.json` profile with tweak IDs. Import ticks them for review before applying. |
| Back up proven settings for reuse | ✅ | Same profile files; also usable on other PCs. |
| Apply a configuration silently (`OOSU10.exe file.cfg /quiet`) | ✅ | `FuWindows.exe profile.json`, `--config`, `--preset`, `--apply`, `--revert`, `--revert-all`, `--list`. See the README. |
| Portable, no installation | ✅ | `dotnet publish -c Release -r win-x64 -p:Portable=true` produces one self-contained `FuWindows.exe` (~90 MB). |
| Automatic re-enforcement after Windows updates (Premium) | ✅ | Tools → *Keep settings applied*: a sign-in task runs `FuWindows.exe --enforce` and re-applies tracked tweaks that were reset. 🟡 It checks at sign-in rather than watching continuously. |

**Setting categories**

| ShutUp10++ category | FuWindows | Missing settings |
|---|---|---|
| Privacy & telemetry | ✅ | |
| Windows apps & background activity | 🟡 | Have: account info, contacts, calendar, call history, email, tasks, messages, app diagnostics, camera, mic, motion, eye tracking, notifications, radios & unpaired devices, documents/pictures/videos/music/downloads/file system, screen capture, background apps. Plus a new **App permissions** page: per-app allow/deny for 23 permissions, with when each app last used it. **Missing:** Wi-Fi/cellular data permissions (no documented consent key to verify against). |
| Location, sensors & device access | 🟡 | Have: location, sensors, location in search, device companion apps/metadata, per-app location permission. Clearing location history links to Settings > Location (Windows exposes no API to clear it directly). |
| Search, cloud & personalization | ✅ | **Missing:** Store app suggestions in Start search (WinUtil has this too). |
| Cortana, voice & online services | ✅ | Have: Cortana (including above lock), online speech, voice activation for apps, speech-model updates. |
| Updates, reboots & recommendations | ✅ | Own *Windows Update* section: active hours (your own hours, up to 18), no auto-restart while signed in, security-first deferral, stay on your current version, Store auto-updates, P2P delivery, drivers, auto-update, feature experiments, Insider builds, re-applying settings after updates. |
| Security & network | ✅ | Have: no auto-connect to open/suggested hotspots, hotspot reporting off, automatic DNS-over-HTTPS, connectivity-check (NCSI) opt-out. |
| OneDrive & Microsoft account | ✅ | Have: sync disable, sync-provider ads, no traffic before sign-in, hide from File Explorer, block adding Microsoft accounts. Also: OneDrive uninstall/reinstall on the Apps page. Plus OneDrive notifications off and sync-status overlay icons removed (with a .reg backup). |

## O&O AppBuster

| AppBuster feature | FuWindows | Gap |
|---|---|---|
| Remove preinstalled Microsoft and third-party apps | ✅ | |
| Remove many apps at once | ✅ | |
| Mark non-critical and system-critical apps | ✅ | *Suggested to remove* label, protected apps locked. |
| Uses official Windows interfaces | ✅ | Windows `PackageManager` API. |
| Prevent removed apps coming back | ✅ | Deprovisions for new users. |
| Remove for all users on multi-user PCs | ✅ | Remove for every account; apps installed only for other accounts are listed under *Hidden apps*. |
| Show hidden apps | ✅ | *Hidden apps* filter: installed for other accounts only, or staged for new users but not installed. |
| Show available / previously removed apps | ✅ | *Previously removed* and *Optional Windows apps to install* filters; every row shows its install state. |
| Reinstall removed apps | ✅ | Re-registers the on-disk copy, otherwise installs from the Store with winget, otherwise opens the Store page. |
| Install optional Windows apps | ✅ | Curated list of 27 Microsoft apps and media codecs (Calculator, Photos, Terminal, HEIF/AV1/WebP…). |
| Automatic restore point | ✅ | Offered before tweaks, app uninstalls and Edge removal, and on first run. |
| Undo all changes | ✅ | Apps → *Undo: reinstall everything FuWindows removed*; tweaks have *Revert all*. |
| Portable | ✅ | Single-exe build, see ShutUp10++ above. |

## WinUtil

**Tabs and program features**

| WinUtil feature | FuWindows | Gap |
|---|---|---|
| Install tab: app catalog installed with winget / Chocolatey | ✅ | *Install*: 58 curated apps in 8 categories, silent install with winget or Chocolatey, installed-status detection. *Third-party repositories*: install winget, Chocolatey, Scoop and Homebrew (via WSL); manage winget sources, Chocolatey sources, Scoop buckets and Homebrew taps; turn off winget telemetry and Homebrew analytics; upgrade everything. |
| Tweaks with presets (Minimal / Standard / Advanced) | ✅ | *Recommended* / *Recommended + Limited*, in the UI and with `--preset`. |
| Automation: config files and command-line parameters for unattended rollout | ✅ | `--config profile.json` applies tweaks **and removes apps**; `--preset`, `--apply`, `--revert-all`. |
| Updates tab: default / security-only / paused, without Group Policy | ✅ | *Windows Update* section: security-first deferral, stay on version, active hours, auto-update off, drivers, Store updates. |
| Config → Fixes: reset Windows Update, reset network, SFC/DISM scan, reinstall winget, NTP server, AutoLogon | ✅ | Tools → *Repair*; NTP is a network tweak (pool.ntp.org). AutoLogon deliberately left out (stores your password). |
| Config → Windows features: .NET 3.5, Hyper-V, legacy media, WSL, NFS, Windows Sandbox, legacy F8 boot menu, daily registry backup | 🟡 | Tools → *Windows features*: SMB1 off, WSL, Sandbox, Hyper-V, .NET 3.5, DirectPlay, legacy Media Player, NFS, Telnet. Not yet: F8 boot menu, daily registry backup. |
| Config → Legacy panels | ✅ | Tools → *Classic panels*: 19 shortcuts. |
| **Windows 11 ISO creator** (debloated, preconfigured install image) | ❌ | |
| PowerShell profile, OpenSSH server | ➖ | |

**Tweaks in WinUtil that FuWindows doesn't have**

| WinUtil tweak | Group in WinUtil | FuWindows |
|---|---|---|
| Activity history, telemetry, consumer features, location, Delivery Optimization, background apps, Edge debloat, restore point | Essential / Advanced | ✅ already covered |
| Microsoft Store results in Start search: disable | Essential | ❌ privacy-relevant, should add |
| Services: set to Manual (DiagTrack, MapsBroker, StorSvc, CscService, SharedAccess…) | Essential | 🟡 only DiagTrack, dmwappushservice and lfsvc |
| Windows Platform Binary Table (WPBT): disable | Essential | ✅ `system.wpbt` |
| Prevent device companion apps (driver metadata from the network) | Essential | ✅ `system.devicemetadata` |
| Widgets: remove packages | Essential | ✅ policy, plus the Apps page removes the package |
| Windows AI: disable and remove Copilot/AI packages | Advanced | ✅ policies, plus the Apps page removes Copilot |
| Microsoft Edge: remove | Advanced | ✅ Apps → *Microsoft Edge*: official uninstaller, keeps WebView2 by default, optional reinstall block, reinstall with winget |
| Microsoft OneDrive: remove | Advanced | ✅ Apps → *Microsoft OneDrive*: its own uninstaller or winget, keeps your files, reinstall with winget |
| Adobe telemetry/activation hosts block | Advanced | ❌ easy to add as a hosts tweak |
| Razer / Logitech auto-install: block | Advanced | ❌ |
| Brave browser debloat | Advanced | ✅ `browser.brave`, plus Firefox and Chrome privacy policies |
| DNS provider selection (Cloudflare, Quad9, AdGuard…) | Advanced | ✅ Network page *DNS provider* card |
| IPv6 disable / prefer IPv4 / Teredo disable | Advanced | ✅ `network.ipv6` (prefer IPv4, tunnels off; full IPv6 disable deliberately not offered) |
| System tray notifications & calendar: disable | Advanced | ❌ |
| Hibernation, BitLocker disable, disk cleanup, temp files, Storage Sense, reserved storage, search indexing, svchost splitting, visual effects, Ultimate Performance plan | Essential / Advanced / Performance | ➖ performance and cleanup, not privacy |
| End task on right-click, classic context menu, Explorer Home/Gallery, folder discovery, UTC clock, unsigned RDP warning | Essential / Advanced | ➖ UI and convenience |
| Customize preferences: dark mode, file extensions, hidden files, taskbar alignment/search/Task View, Bing in Start, Start recommendations, lock screen, sticky keys, Num Lock, mouse acceleration, long paths, verbose BSoD and logon, Game Mode, snapping, MPO, S3 sleep, Settings home, battery %, new Outlook | Customize | ➖ mostly UI. Bing in Start and Start recommendations are already covered as privacy tweaks |

## Suggested priorities

Ordered by value for a privacy tool:

Done:

- ~~Export / import selections and a command line~~
- ~~Re-apply after updates~~ ("Keep settings applied")
- ~~More app permissions~~
- ~~Hotspot auto-connect, OneDrive pre-sign-in traffic, Microsoft-account restrictions~~
- ~~Portable single exe~~
- ~~Install section (58-app catalog) and Third-party repositories (winget, Chocolatey, Scoop, Homebrew in WSL), OneDrive notifications and overlay icons~~
- ~~Profiles for apps, App permissions page, DNS picker, OneDrive removal, browser policies, WPBT, IPv6 tunnels, NTP, repair tools, Windows features, classic panels~~
- ~~AppBuster parity: hidden apps, previously removed, reinstall, optional apps, restore point before uninstalling, undo all~~

Next:

1. **Windows 11 ISO builder** (MicroWin): debloated, preconfigured install media. Large, and the least related to privacy.
2. **Smaller gaps**: Store results in Start search (needs a verified method), Razer/Logitech auto-install blocks, tray notification and calendar toggles, legacy F8 boot menu, daily registry backup, more services on demand, Wi-Fi/cellular data permissions.
3. **Left out on purpose**: Adobe's hosts list (it also blocks license activation), AutoLogon (stores your password in the registry), fully disabling IPv6 (Microsoft doesn't support it).
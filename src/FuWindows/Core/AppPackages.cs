using System.Diagnostics;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace FuWindows.Core;

public enum AppGroup
{
    /// <summary>Preinstalled promotional or rarely-used apps that are safe to remove.</summary>
    Suggested,
    Microsoft,
    ThirdParty,
    /// <summary>Part of Windows itself; protected ones can't be selected.</summary>
    System,
}

public enum AppInstallState
{
    /// <summary>Installed for the current user.</summary>
    Installed,
    /// <summary>Hidden: installed for other user accounts on this PC, not for you.</summary>
    OtherUsersOnly,
    /// <summary>Hidden: staged for new user accounts, not installed for anyone yet.</summary>
    ProvisionedOnly,
    /// <summary>Uninstalled by FuWindows; can be reinstalled.</summary>
    Removed,
    /// <summary>Optional Microsoft app or codec that isn't installed.</summary>
    Available,
}

public sealed class AppPackageInfo
{
    public string DisplayName { get; init; } = "";
    public string Name { get; init; } = "";
    public string FamilyName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string? LogoPath { get; init; }
    public AppGroup Group { get; init; }
    public bool IsProtected { get; init; }
    public bool IsProvisioned { get; init; }
    public string? Note { get; init; }
    public AppInstallState State { get; init; } = AppInstallState.Installed;
    public DateTime? RemovedUtc { get; init; }
    /// <summary>Microsoft Store product ID, when known (used to reinstall with winget).</summary>
    public string? StoreId { get; init; }

    public bool CanUninstall => !IsProtected && State is AppInstallState.Installed or AppInstallState.OtherUsersOnly or AppInstallState.ProvisionedOnly;
    public bool CanInstall => State is not AppInstallState.Installed;
}


/// <summary>Lists and removes Store/UWP (Appx/MSIX) apps, in the spirit of Cleanup Win10.</summary>
public static class AppPackages
{
    /// <summary>Preinstalled apps that are safe to remove. Matched by package-name prefix.</summary>
    private static readonly string[] SuggestedPrefixes =
    [
        // Microsoft promotional / rarely used
        "Microsoft.3DBuilder", "Microsoft.Microsoft3DViewer", "Microsoft.Print3D", "Microsoft.MixedReality.Portal",
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports",
        "Microsoft.BingTravel", "Microsoft.BingFoodAndDrink", "Microsoft.BingHealthAndFitness", "Microsoft.BingTranslator",
        "Microsoft.BingSearch", "Microsoft.Copilot", "Microsoft.Windows.Ai.Copilot.Provider",
        "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub", "Microsoft.Office.OneNote",
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.PowerAutomateDesktop",
        "Microsoft.Todos", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps", "Microsoft.ZuneVideo",
        "Microsoft.549981C3F5F10", "Microsoft.SkypeApp", "Microsoft.Messaging", "Microsoft.OneConnect",
        "Microsoft.Wallet", "Microsoft.YourPhone", "Microsoft.Windows.DevHome", "Microsoft.OutlookForWindows",
        "microsoft.windowscommunicationsapps", "MicrosoftTeams", "MSTeams", "Clipchamp.Clipchamp",
        "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.MicrosoftJournal", "Microsoft.News",
        "Microsoft.XboxApp", "Microsoft.GamingApp", "Microsoft.XboxGameOverlay", "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxSpeechToTextOverlay", "Microsoft.Xbox.TCUI", "Microsoft.XboxIdentityProvider",

        // Third-party apps Windows installs as "suggestions"
        "king.com.", "SpotifyAB.SpotifyMusic", "Disney.", "Facebook.", "BytedancePte.Ltd.TikTok",
        "AmazonVideo.PrimeVideo", "4DF9E0F8.Netflix", "Flipboard.Flipboard", "ShazamEntertainmentLtd.Shazam",
        "PandoraMediaInc.", "AdobeSystemsIncorporated.AdobePhotoshopExpress", "Duolingo-LearnLanguagesforFree",
        "EclipseManager", "ActiproSoftwareLLC.", "D5EA27B7.Duolingo", "46928bounde.EclipseManager",
        "2FE3CB00.PicsArt-PhotoStudio", "Microsoft.MinecraftEducationEdition", "Microsoft.MinecraftUWP",
        "7EE7776C.LinkedInforWindows", "5A894077.McAfeeSecurity", "B9ECED6F.ASUSPCAssistant",
    ];

    /// <summary>Removing these breaks Windows, the Store, winget or sign-in, so they are locked.</summary>
    private static readonly string[] EssentialPrefixes =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Winget.", "Microsoft.SecHealthUI", "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.Windows.CloudExperienceHost", "windows.immersivecontrolpanel", "MicrosoftWindows.Client.",
        "Microsoft.WindowsAppRuntime", "Microsoft.LockApp", "Microsoft.Windows.Search", "Microsoft.Windows.OOBE",
        "Microsoft.CredDialogHost", "Microsoft.Win32WebViewHost", "Microsoft.Windows.XGpuEjectDialog",
        "Microsoft.ECApp", "Microsoft.Windows.AssignedAccessLockApp", "Microsoft.Windows.PeopleExperienceHost",
        "MicrosoftWindows.UndockedDevKit", "Microsoft.Windows.Apprep.ChxApp", "windows.", "Windows.",
        "Microsoft.Windows.CapturePicker", "Microsoft.Windows.PinningConfirmationDialog", "Microsoft.Windows.SecureAssessmentBrowser",
        "Microsoft.Windows.ParentalControls", "Microsoft.BioEnrollment", "Microsoft.AsyncTextService",
        "Microsoft.Windows.NarratorQuickStart", "Microsoft.MicrosoftEdge", "Microsoft.MicrosoftEdgeDevToolsClient",
        "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.Services.Store.Engagement",
        "Microsoft.Windows.FileExplorer", "MicrosoftWindows.CrossDevice",
    ];

    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.XboxIdentityProvider"] = "Needed to sign in to Xbox / Game Pass PC games.",
        ["Microsoft.GamingApp"] = "The Xbox app. Needed for Game Pass on PC.",
        ["Microsoft.XboxGamingOverlay"] = "Game Bar (Win+G): screenshots, recording and FPS overlay.",
        ["Microsoft.YourPhone"] = "Phone Link: texts, calls and photos from your phone.",
        ["Microsoft.windowscommunicationsapps"] = "Legacy Mail and Calendar.",
        ["Microsoft.WindowsTerminal"] = "Default terminal on Windows 11; command prompts fall back to the old console.",
        ["Microsoft.Windows.Photos"] = "Default image viewer. Install another before removing.",
        ["Microsoft.ZuneMusic"] = "Media Player, the default audio and video player.",
        ["Microsoft.ScreenSketch"] = "Snipping Tool (Win+Shift+S).",
        ["Microsoft.WindowsCalculator"] = "Calculator.",
        ["Microsoft.WindowsNotepad"] = "Notepad.",
        ["Microsoft.Paint"] = "Paint.",
    };

    private static bool MatchesAny(string name, string[] prefixes) =>
        prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static AppGroup Classify(string name, string publisher, bool systemSigned, bool isProtected) =>
        isProtected ? AppGroup.System :
        MatchesAny(name, SuggestedPrefixes) ? AppGroup.Suggested :
        systemSigned ? AppGroup.System :
        publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? AppGroup.Microsoft :
        AppGroup.ThirdParty;

    private static List<Package> TryList(Func<IEnumerable<Package>> find)
    {
        try { return find().ToList(); }
        catch { return []; }
    }

    /// <summary>
    /// Everything AppBuster-style: apps installed for you, hidden apps (other users only, or staged
    /// for new users), apps FuWindows removed, and optional Microsoft apps that aren't installed.
    /// </summary>
    public static List<AppPackageInfo> Load()
    {
        var manager = new PackageManager();

        var provisionedPackages = TryList(() => manager.FindProvisionedPackages());
        var provisioned = new HashSet<string>(provisionedPackages.Select(p => p.Id.FamilyName), StringComparer.OrdinalIgnoreCase);
        var nonRemovable = LoadNonRemovable();
        var result = new Dictionary<string, AppPackageInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(Package package, AppInstallState state)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) return;

                var id = package.Id;
                if (result.ContainsKey(id.FamilyName)) return;

                bool essential = MatchesAny(id.Name, EssentialPrefixes) || nonRemovable.Contains(id.FullName);
                bool isProtected = essential && !MatchesAny(id.Name, SuggestedPrefixes);
                string publisher = SafeGet(() => package.PublisherDisplayName) ?? id.Publisher;
                bool systemSigned = SafeGet(() => package.SignatureKind) == PackageSignatureKind.System;

                result[id.FamilyName] = new AppPackageInfo
                {
                    DisplayName = CleanDisplayName(SafeGet(() => package.DisplayName), id.Name),
                    Name = id.Name,
                    FamilyName = id.FamilyName,
                    FullName = id.FullName,
                    Publisher = publisher,
                    Version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                    LogoPath = SafeGet(() => package.Logo?.LocalPath),
                    Group = Classify(id.Name, publisher, systemSigned, isProtected),
                    IsProtected = isProtected,
                    IsProvisioned = provisioned.Contains(id.FamilyName),
                    Note = Notes.FirstOrDefault(n => id.Name.Equals(n.Key, StringComparison.OrdinalIgnoreCase)).Value,
                    State = state,
                    StoreId = OptionalApps.StoreIdFor(id.Name),
                };
            }
            catch
            {
                // A broken registration shouldn't hide every other app.
            }
        }

        foreach (var p in TryList(() => manager.FindPackagesForUser(string.Empty))) Add(p, AppInstallState.Installed);
        foreach (var p in TryList(() => manager.FindPackages())) Add(p, AppInstallState.OtherUsersOnly);
        foreach (var p in provisionedPackages) Add(p, AppInstallState.ProvisionedOnly);

        foreach (var removed in RemovedApps.Load())
        {
            if (result.TryGetValue(removed.FamilyName, out var present))
            {
                if (present.State == AppInstallState.Installed) RemovedApps.Forget(removed.FamilyName);
                continue;
            }
            result[removed.FamilyName] = new AppPackageInfo
            {
                DisplayName = removed.DisplayName,
                Name = removed.Name,
                FamilyName = removed.FamilyName,
                Publisher = removed.Publisher,
                Group = Classify(removed.Name, removed.Publisher, false, false),
                State = AppInstallState.Removed,
                RemovedUtc = removed.RemovedUtc,
                StoreId = OptionalApps.StoreIdFor(removed.Name),
            };
        }

        foreach (var optional in OptionalApps.Catalog)
        {
            if (result.ContainsKey(optional.FamilyName)) continue;
            result[optional.FamilyName] = new AppPackageInfo
            {
                DisplayName = optional.DisplayName,
                Name = optional.PackageName,
                FamilyName = optional.FamilyName,
                Publisher = "Microsoft Corporation",
                Group = AppGroup.Microsoft,
                State = AppInstallState.Available,
                StoreId = optional.StoreId,
                Note = optional.Description,
            };
        }

        return result.Values
            .OrderBy(a => a.State)
            .ThenBy(a => a.Group)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Uninstalls an app. Optionally deprovisions it first, so Windows doesn't reinstall it for
    /// new user accounts (or after feature updates). Every removal is recorded for reinstall.
    /// </summary>
    public static async Task UninstallAsync(AppPackageInfo app, bool allUsers, bool deprovision, Action<string> log)
    {
        if (!app.CanUninstall) throw new InvalidOperationException("This app can't be uninstalled here.");
        var manager = new PackageManager();

        if (app.State == AppInstallState.ProvisionedOnly || (deprovision && app.IsProvisioned))
        {
            var dep = await manager.DeprovisionPackageForAllUsersAsync(app.FamilyName).AsTask();
            if (dep.ExtendedErrorCode is not null)
            {
                if (app.State == AppInstallState.ProvisionedOnly) throw new InvalidOperationException(dep.ErrorText.Trim());
                log($"  ! could not deprovision: {dep.ErrorText}");
            }
            else log("  ✓ won't be installed for new users");
        }

        if (app.State != AppInstallState.ProvisionedOnly)
        {
            // Hidden apps installed only for other accounts can only be removed for all users.
            var options = allUsers || app.State == AppInstallState.OtherUsersOnly
                ? RemovalOptions.RemoveForAllUsers
                : RemovalOptions.None;
            var result = await manager.RemovePackageAsync(app.FullName, options).AsTask();
            if (result.ExtendedErrorCode is not null)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorText)
                    ? result.ExtendedErrorCode.Message
                    : result.ErrorText.Trim());
        }

        RemovedApps.Record(app);
    }

    /// <summary>
    /// Installs or reinstalls an app for the current user. Tries, in order: re-registering the copy
    /// Windows still has on disk (instant, offline), installing from the Microsoft Store with winget,
    /// and finally opening the app's Store page.
    /// </summary>
    /// <returns>True if installed; false if the Store page was opened for you to finish.</returns>
    public static async Task<bool> InstallAsync(AppPackageInfo app, Action<string> log)
    {
        var manager = new PackageManager();

        {
            try
            {
                var r = await manager.RegisterPackageByFamilyNameAsync(app.FamilyName, null, DeploymentOptions.None, null, null).AsTask();
                if (r.ExtendedErrorCode is null && IsInstalledForMe(manager, app.FamilyName))
                {
                    log("  ✓ re-registered the copy Windows still had on disk");
                    RemovedApps.Forget(app.FamilyName);
                    return true;
                }
            }
            catch { /* Not staged on this PC any more: download it instead. */ }
        }

        var winget = SystemTools.FindWinget();
        if (app.StoreId is { } storeId && winget is not null)
        {
            log($"  Installing from the Microsoft Store ({storeId})…");
            var r = await Task.Run(() => ProcessRunner.Run(winget,
                $"install --id {storeId} --source msstore --silent --accept-package-agreements --accept-source-agreements",
                900_000));
            if (IsInstalledForMe(manager, app.FamilyName))
            {
                log("  ✓ installed");
                RemovedApps.Forget(app.FamilyName);
                return true;
            }
            log($"  ! winget couldn't install it (exit code {r.ExitCode})");
        }

        Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?PFN={app.FamilyName}") { UseShellExecute = true });
        log("  Opened its Microsoft Store page; press Get/Install there to finish.");
        return false;
    }

    private static bool IsInstalledForMe(PackageManager manager, string familyName)
    {
        try { return manager.FindPackagesForUser(string.Empty, familyName).Any(); }
        catch { return false; }
    }

    private static HashSet<string> LoadNonRemovable()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = ProcessRunner.PowerShell(
                "Get-AppxPackage | Where-Object { $_.NonRemovable } | ForEach-Object { $_.PackageFullName }", 60_000);
            foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(line);
        }
        catch { }
        return set;
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        // Unresolved resource strings look like "ms-resource:AppName".
        if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return fallback;
        return displayName;
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try { return getter(); } catch { return default; }
    }
}

/// <summary>History of apps FuWindows uninstalled, so they can be shown and reinstalled.</summary>
public static class RemovedApps
{
    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Publisher { get; set; } = "";
        public DateTime RemovedUtc { get; set; }
    }

    private static string FilePath => AppData.File("removed-apps.json");
    private static readonly object Gate = new();

    public static List<Entry> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), AppData.Json) ?? []; }
            catch { return []; }
        }
    }

    public static void Record(AppPackageInfo app)
    {
        lock (Gate)
        {
            var list = Load().Where(e => !e.FamilyName.Equals(app.FamilyName, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new Entry
            {
                Name = app.Name,
                FamilyName = app.FamilyName,
                DisplayName = app.DisplayName,
                Publisher = app.Publisher,
                RemovedUtc = DateTime.UtcNow,
            });
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, AppData.Json));
        }
    }

    public static void Forget(string familyName)
    {
        lock (Gate)
        {
            var list = Load();
            if (list.RemoveAll(e => e.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)) > 0)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, AppData.Json));
        }
    }
}

/// <summary>
/// Optional Microsoft apps and media codecs that can be installed from the Store (AppBuster's
/// "install optional Windows apps"). Store IDs are used with winget; if one is wrong or winget is
/// missing, FuWindows falls back to the app's Store page via its package family name.
/// </summary>
public static class OptionalApps
{
    public sealed record Entry(string DisplayName, string PackageName, string StoreId, string Description)
    {
        public string FamilyName => $"{PackageName}_8wekyb3d8bbwe";
    }

    public static IReadOnlyList<Entry> Catalog { get; } =
    [
        new("Windows Calculator", "Microsoft.WindowsCalculator", "9WZDNCRFHVN5", "Calculator with scientific, programmer and graphing modes."),
        new("Windows Notepad", "Microsoft.WindowsNotepad", "9MSMLRH6LZF3", "Plain-text editor."),
        new("Paint", "Microsoft.Paint", "9PCFS5B6T72H", "Simple image editor."),
        new("Snipping Tool", "Microsoft.ScreenSketch", "9MZ95KL8MR0L", "Screenshots and screen recording (Win+Shift+S)."),
        new("Microsoft Photos", "Microsoft.Windows.Photos", "9WZDNCRFJBH4", "Default image viewer and basic editor."),
        new("Windows Camera", "Microsoft.WindowsCamera", "9WZDNCRFJBBG", "Webcam photos and video."),
        new("Media Player", "Microsoft.ZuneMusic", "9WZDNCRFJ3PT", "Default music and video player."),
        new("Movies & TV", "Microsoft.ZuneVideo", "9WZDNCRFJ3P2", "Video player."),
        new("Windows Sound Recorder", "Microsoft.WindowsSoundRecorder", "9WZDNCRFHWKN", "Records audio from your microphone."),
        new("Windows Clock", "Microsoft.WindowsAlarms", "9WZDNCRFJ3PR", "Alarms, timers, stopwatch and Focus sessions."),
        new("Microsoft Sticky Notes", "Microsoft.MicrosoftStickyNotes", "9NBLGGH4QGHW", "Notes on your desktop."),
        new("Windows Terminal", "Microsoft.WindowsTerminal", "9N0DX20HK701", "Tabbed terminal for PowerShell, Command Prompt and WSL."),
        new("App Installer (winget)", "Microsoft.DesktopAppInstaller", "9NBLGGH4NNS1", "The winget package manager and .msix installer."),
        new("Quick Assist", "MicrosoftCorporationII.QuickAssist", "9P7BP5VNWKX5", "Give or get remote help."),
        new("Phone Link", "Microsoft.YourPhone", "9NMPJ99VJBWV", "Texts, calls and photos from your phone."),
        new("Xbox", "Microsoft.GamingApp", "9MV0B5HZVK9Z", "Xbox app and PC Game Pass."),
        new("Xbox Game Bar", "Microsoft.XboxGamingOverlay", "9NZKPSTSNW4P", "In-game overlay (Win+G): capture, performance, audio."),
        new("Xbox Identity Provider", "Microsoft.XboxIdentityProvider", "9WZDNCRD1HKW", "Lets PC games sign in to Xbox services."),
        new("Microsoft To Do", "Microsoft.Todos", "9NBLGGH5R558", "Task lists."),
        new("Microsoft Whiteboard", "Microsoft.Whiteboard", "9MSPC6MP8FM4", "Digital whiteboard."),
        new("HEIF Image Extensions", "Microsoft.HEIFImageExtension", "9PMMSR1CGPWG", "Opens .heic/.heif photos (iPhone). HEVC video needs the separate paid extension."),
        new("Webp Image Extensions", "Microsoft.WebpImageExtension", "9PG2DK419DRG", "Opens .webp images."),
        new("Raw Image Extension", "Microsoft.RawImageExtension", "9NCTDW2W1BH8", "Thumbnails and viewing for camera RAW files."),
        new("Web Media Extensions", "Microsoft.WebMediaExtensions", "9N5TDP8VCMHS", "Ogg, Vorbis and Theora playback."),
        new("AV1 Video Extension", "Microsoft.AV1VideoExtension", "9MVZQVXJBQ9V", "Hardware-accelerated AV1 video playback."),
        new("VP9 Video Extensions", "Microsoft.VP9VideoExtensions", "9N4D0MSMP0PT", "VP9 video playback."),
        new("MPEG-2 Video Extension", "Microsoft.MPEG2VideoExtension", "9N95Q1ZZPMH4", "MPEG-2 video playback (DVD rips, older camcorders)."),
    ];

    public static string? StoreIdFor(string packageName) =>
        Catalog.FirstOrDefault(e => e.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))?.StoreId;
}

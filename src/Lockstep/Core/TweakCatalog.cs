using Microsoft.Win32;

namespace Lockstep.Core;

public sealed record Category(string Id, string Title, string Glyph, string Description);

/// <summary>
/// Every tweak Lockstep knows about. Adding a tweak = adding an entry here; the UI, status
/// detection, backup and revert all come for free from the action types.
/// </summary>
public static class TweakCatalog
{
    public static IReadOnlyList<Category> Categories { get; } =
    [
        new("telemetry", "Telemetry & diagnostics", "",
            "Diagnostic data, the telemetry service, error reporting and the scheduled tasks that collect and upload usage data."),
        new("privacy", "Privacy & permissions", "",
            "Advertising ID, activity history, typing and inking data, and which apps may read your personal data."),
        new("ai", "AI, Copilot & Recall", "",
            "Windows Copilot, Recall snapshots, Click to Do and the generative AI features built into inbox apps."),
        new("search", "Start, search & ads", "",
            "Bing in the Start menu, Cortana, suggested apps, lock-screen ads, Widgets and other promotional content."),
        new("edge", "Browsers", "",
            "Diagnostic data, shopping, AI and background features in Microsoft Edge, Brave, Firefox and Chrome. Applied as browser policies, so the browser will say it is \"managed by your organization\"."),
        new("updates", "Windows Update", "",
            "When and how Windows updates: active hours, restarts, deferral, staying on your current version, driver updates and peer-to-peer delivery."),
        new("defender", "Defender & SmartScreen", "",
            "What Microsoft Defender, SmartScreen and the Malicious Software Removal Tool report back to Microsoft. Turning these off trades protection for privacy."),
        new("network", "Network blocking", "",
            "Hosts-file and firewall blocks for Microsoft endpoints. These stop traffic even when a setting is ignored."),
        new("performance", "Performance", "",
            "Make Windows, Start search and File Explorer respond faster: local-only search, fewer animations, quicker menus and startup, and a performance power plan."),
        new("gaming", "Gaming", "",
            "Game Mode, GPU scheduling, raw mouse input and fewer background interruptions while you play. Gains depend on your hardware; each tweak says what it trades off."),
        new("identity", "Device identity (GDID)", "",
            "The Global Device ID is a permanent identifier Microsoft's servers assign to this Windows installation. It can't be switched off, but you can cut the services that report it and stop it being created on local-account PCs."),
        new("system", "System & apps", "",
            "OneDrive, Remote Assistance, Game DVR, Phone Link and other built-in features that phone home."),
    ];

    // Common paths ----------------------------------------------------------------------------
    private const string PolWin = @"SOFTWARE\Policies\Microsoft\Windows";
    private const string DataCollection = PolWin + @"\DataCollection";
    private const string CloudContentLm = PolWin + @"\CloudContent";
    private const string CloudContentCu = @"Software\Policies\Microsoft\Windows\CloudContent";
    private const string SystemPol = PolWin + @"\System";
    private const string WinSearch = PolWin + @"\Windows Search";
    private const string WindowsAI = PolWin + @"\WindowsAI";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string BravePol = @"SOFTWARE\Policies\BraveSoftware\Brave";
    private const string FirefoxPol = @"SOFTWARE\Policies\Mozilla\Firefox";
    private const string ChromePol = @"SOFTWARE\Policies\Google\Chrome";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerAdv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string AppPrivacy = PolWin + @"\AppPrivacy";
    private const string ConsentStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string SessionEnv = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static RegistryAction Lm(string path, string name, int value) =>
        RegistryAction.Dword(RegistryHive.LocalMachine, path, name, value);
    private static RegistryAction Cu(string path, string name, int value) =>
        RegistryAction.Dword(RegistryHive.CurrentUser, path, name, value);
    private static RegistryAction LmStr(string path, string name, string value) =>
        RegistryAction.String(RegistryHive.LocalMachine, path, name, value);
    private static RegistryAction CuStr(string path, string name, string value) =>
        RegistryAction.String(RegistryHive.CurrentUser, path, name, value);
    private static ScheduledTaskAction Sched(string path) => new(path);
    private static ServiceAction Svc(string name, ServiceStart target, ServiceStart windowsDefault) => new(name, target, windowsDefault);
    private static FirewallAction Fw(string label, string program) => new(label, program);
    private static HostsAction Hosts(string tag, params string[] domains) => new(tag, domains);
    private static ITweakAction[] DenyCapabilities(params string[] capabilities) =>
        capabilities.Select(c => (ITweakAction)LmStr($@"{ConsentStore}\{c}", "Value", "Deny")).ToArray();

    private static Tweak T(string id, string category, RiskLevel risk, string title, string description,
        ITweakAction[] actions, string? warning = null, Func<string?>? blocker = null) => new()
    {
        Id = id, Category = category, Risk = risk, Title = title, Description = description,
        Actions = actions, Warning = warning, Blocker = blocker,
    };

    public static IReadOnlyList<Tweak> All { get; } = WithSharedCategories(Build());



    private static List<Tweak> WithSharedCategories(List<Tweak> tweaks)
    {
        // Existing tweaks that also belong in another section. A local, not a static field:
        // static fields initialise in declaration order and All is declared first.
        var shared = new Dictionary<string, string[]>()
        {
            ["search.bing"] = ["performance"],
            ["search.cloud"] = ["performance"],
            ["search.highlights"] = ["performance"],
            ["search.suggestions"] = ["performance"],
            ["search.widgets"] = ["performance"],
            ["privacy.background"] = ["performance", "gaming"],
            ["edge.background"] = ["performance"],
            ["system.gamedvr"] = ["gaming"],
            ["perf.powerplan"] = ["gaming"],
            ["updates.noreboot"] = ["gaming"],
            ["identity.deliveryoptimization"] = ["updates"],
        };

        foreach (var t in tweaks)
            if (shared.TryGetValue(t.Id, out var extra)) t.AlsoIn = extra;
        return tweaks;
    }

    private static List<Tweak> Build() =>
    [
        // ===================================================================================
        // Telemetry & diagnostics
        // ===================================================================================
        T("telemetry.level", "telemetry", RiskLevel.Recommended,
            "Set diagnostic data to the minimum level",
            "Sets the AllowTelemetry policy to 0. Enterprise/Education honour \"Security\" (off); Home/Pro clamp it to \"Required\", the lowest level they allow. Also blocks diagnostic log and memory-dump collection and the OneSettings download.",
            [
                Lm(DataCollection, "AllowTelemetry", 0),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
                Lm(DataCollection, "MaxTelemetryAllowed", 0),
                Lm(DataCollection, "LimitDiagnosticLogCollection", 1),
                Lm(DataCollection, "LimitDumpCollection", 1),
                Lm(DataCollection, "DisableOneSettingsDownloads", 1),
                Lm(DataCollection, "DisableTelemetryOptInChangeNotification", 1),
                Lm(DataCollection, "DisableTelemetryOptInSettingsUx", 1),
            ]),

        T("telemetry.service", "telemetry", RiskLevel.Recommended,
            "Disable the Connected User Experiences and Telemetry service",
            "Disables and stops DiagTrack, the service that uploads diagnostic data, and the WAP push routing service it uses.",
            [
                Svc("DiagTrack", ServiceStart.Disabled, ServiceStart.Automatic),
                Svc("dmwappushservice", ServiceStart.Disabled, ServiceStart.Manual),
            ]),

        T("telemetry.tasks", "telemetry", RiskLevel.Recommended,
            "Disable telemetry scheduled tasks",
            "Turns off the Compatibility Appraiser, CEIP, disk diagnostics, feedback and error-queue tasks that gather and upload usage data in the background.",
            [
                Sched(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                Sched(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp"),
                Sched(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                Sched(@"\Microsoft\Windows\Application Experience\MareBackup"),
                Sched(@"\Microsoft\Windows\Autochk\Proxy"),
                Sched(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                Sched(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                Sched(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
                Sched(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
                Sched(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"),
                Sched(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting"),
                Sched(@"\Microsoft\Windows\Device Information\Device"),
                Sched(@"\Microsoft\Windows\Device Information\Device User"),
            ]),

        T("telemetry.ceip", "telemetry", RiskLevel.Recommended,
            "Disable Customer Experience Improvement Program and inventory collection",
            "Opts out of CEIP/SQM and turns off the Application Telemetry engine, Inventory Collector and Steps Recorder.",
            [
                Lm(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
                Lm(@"SOFTWARE\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
                Lm(PolWin + @"\AppCompat", "AITEnable", 0),
                Lm(PolWin + @"\AppCompat", "DisableInventory", 1),
                Lm(PolWin + @"\AppCompat", "DisableUAR", 1),
            ]),

        T("telemetry.feedback", "telemetry", RiskLevel.Recommended,
            "Never ask for feedback",
            "Stops the \"How likely are you to recommend Windows?\" prompts and feedback notifications.",
            [
                Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                Lm(DataCollection, "DoNotShowFeedbackNotifications", 1),
            ]),

        T("telemetry.tailored", "telemetry", RiskLevel.Recommended,
            "Disable tailored experiences based on diagnostic data",
            "Stops Microsoft using your diagnostic data to personalise tips, ads and recommendations.",
            [
                Cu(CloudContentCu, "DisableTailoredExperiencesWithDiagnosticData", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            ]),

        T("telemetry.wer", "telemetry", RiskLevel.Limited,
            "Disable Windows Error Reporting",
            "Crash and hang reports are no longer sent to Microsoft. Crash dumps for your own debugging are unaffected.",
            [
                Lm(PolWin + @"\Windows Error Reporting", "Disabled", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
                Lm(@"SOFTWARE\Microsoft\PCHealth\ErrorReporting", "DoReport", 0),
            ],
            warning: "Microsoft can no longer offer you fixes for known crashes."),

        T("telemetry.devtools", "telemetry", RiskLevel.Recommended,
            "Opt out of .NET CLI and PowerShell 7 telemetry",
            "Sets the DOTNET_CLI_TELEMETRY_OPTOUT and POWERSHELL_TELEMETRY_OPTOUT system environment variables. Takes effect for newly started shells.",
            [
                LmStr(SessionEnv, "DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
                LmStr(SessionEnv, "POWERSHELL_TELEMETRY_OPTOUT", "1"),
            ]),

        T("telemetry.firewall", "telemetry", RiskLevel.Recommended,
            "Block telemetry executables in the firewall",
            "Adds outbound firewall rules for CompatTelRunner, DeviceCensus and the CEIP uploader, so they can't send anything even if re-enabled by an update.",
            [
                Fw("CompatTelRunner", @"%SystemRoot%\System32\CompatTelRunner.exe"),
                Fw("DeviceCensus", @"%SystemRoot%\System32\DeviceCensus.exe"),
                Fw("wsqmcons", @"%SystemRoot%\System32\wsqmcons.exe"),
            ]),

        // ===================================================================================
        // Privacy & permissions
        // ===================================================================================
        T("privacy.adid", "privacy", RiskLevel.Recommended,
            "Disable the advertising ID",
            "Apps can no longer use a per-user advertising identifier to track you across apps.",
            [
                Lm(PolWin + @"\AdvertisingInfo", "DisabledByGroupPolicy", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
            ]),

        T("privacy.activity", "privacy", RiskLevel.Recommended,
            "Disable activity history",
            "Windows stops recording the apps, files and sites you use and stops uploading them to your Microsoft account.",
            [
                Lm(SystemPol, "EnableActivityFeed", 0),
                Lm(SystemPol, "PublishUserActivities", 0),
                Lm(SystemPol, "UploadUserActivities", 0),
            ]),

        T("privacy.language", "privacy", RiskLevel.Recommended,
            "Don't let websites read your language list",
            "Websites can no longer fingerprint you using your installed language list.",
            [ Cu(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1) ]),

        T("privacy.launchtracking", "privacy", RiskLevel.Recommended,
            "Disable app launch tracking",
            "Windows stops tracking which apps you start to \"improve\" Start and search.",
            [ Cu(ExplorerAdv, "Start_TrackProgs", 0) ]),

        T("privacy.inking", "privacy", RiskLevel.Recommended,
            "Disable inking & typing personalization",
            "Stops Windows collecting your typing and handwriting to build a personal dictionary, and stops sharing handwriting data with Microsoft.",
            [
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                Cu(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
                Lm(@"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0),
                Lm(PolWin + @"\TabletPC", "PreventHandwritingDataSharing", 1),
                Lm(PolWin + @"\HandwritingErrorReports", "PreventHandwritingErrorReports", 1),
                Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0),
            ]),

        T("privacy.speech", "privacy", RiskLevel.Recommended,
            "Disable online speech recognition",
            "Voice input is no longer sent to Microsoft's cloud speech service. Offline speech still works.",
            [ Cu(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0) ]),

        T("privacy.clipboard", "privacy", RiskLevel.Limited,
            "Disable cloud clipboard sync",
            "Clipboard contents are no longer synced between your devices through Microsoft's servers.",
            [ Lm(SystemPol, "AllowCrossDeviceClipboard", 0) ]),

        T("privacy.settingsync", "privacy", RiskLevel.Limited,
            "Disable settings sync to your Microsoft account",
            "Themes, passwords, language and other settings are no longer uploaded to your Microsoft account.",
            [
                Lm(PolWin + @"\SettingSync", "DisableSettingSync", 2),
                Lm(PolWin + @"\SettingSync", "DisableSettingSyncUserOverride", 1),
            ]),

        T("privacy.personaldata", "privacy", RiskLevel.Limited,
            "Deny apps access to account info, contacts, calendar, call history, email, tasks and messages",
            "Sets the system-wide permission for these data types to Deny. Each can be re-allowed per app in Settings > Privacy.",
            DenyCapabilities("userAccountInformation", "contacts", "appointments", "phoneCallHistory",
                "email", "userDataTasks", "chat", "appDiagnostics"),
            warning: "Mail, Calendar, People and Phone Link lose access until you allow them again."),

        T("privacy.background", "privacy", RiskLevel.Limited,
            "Stop Store apps running in the background",
            "Store apps can no longer run, sync or send data while you're not using them.",
            [ Lm(PolWin + @"\AppPrivacy", "LetAppsRunInBackground", 2) ],
            warning: "Notifications from Store apps (e.g. Mail, WhatsApp) may arrive late or not at all."),

        T("privacy.location", "privacy", RiskLevel.Limited,
            "Disable location services",
            "Turns off the Windows location platform for all apps and users.",
            [
                Lm(PolWin + @"\LocationAndSensors", "DisableLocation", 1),
                Lm(PolWin + @"\LocationAndSensors", "DisableLocationScripting", 1),
                Svc("lfsvc", ServiceStart.Disabled, ServiceStart.Manual),
            ],
            warning: "Breaks Maps, local weather, automatic time zone and Find My Device."),

        T("privacy.findmydevice", "privacy", RiskLevel.Limited,
            "Disable Find My Device",
            "This PC stops reporting its location to your Microsoft account.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0) ]),

        T("privacy.cameramic", "privacy", RiskLevel.Aggressive,
            "Deny all apps access to the camera and microphone",
            "Sets the system-wide camera and microphone permission to Deny.",
            DenyCapabilities("webcam", "microphone"),
            warning: "Video calls, Teams, Zoom, Discord voice and Windows Hello face sign-in stop working until you allow them in Settings."),

        T("privacy.motion", "privacy", RiskLevel.Recommended,
            "Deny apps access to motion and activity data",
            "Apps can no longer read your movement and activity history from the motion sensors.",
            [
                .. DenyCapabilities("activity"),
                Lm(AppPrivacy, "LetAppsAccessMotion", 2),
            ]),

        T("privacy.gaze", "privacy", RiskLevel.Recommended,
            "Deny apps access to eye tracking",
            "Apps can no longer use an eye tracker to see where you're looking.",
            [
                .. DenyCapabilities("gazeInput"),
                Lm(AppPrivacy, "LetAppsAccessGazeInput", 2),
            ]),

        T("privacy.voice", "privacy", RiskLevel.Recommended,
            "Disable voice activation for apps",
            "Apps can no longer listen for a wake word, including above the lock screen.",
            [
                Cu(@"Software\Microsoft\Speech_OneCore\Settings\VoiceActivation\UserPreferenceForAllApps", "AgentActivationEnabled", 0),
                Lm(AppPrivacy, "LetAppsActivateWithVoice", 2),
                Lm(AppPrivacy, "LetAppsActivateWithVoiceAboveLock", 2),
            ]),

        T("privacy.speechmodel", "privacy", RiskLevel.Recommended,
            "Disable automatic speech-model downloads",
            "Windows stops periodically downloading speech recognition and synthesis model updates from Microsoft.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate", 0) ]),

        T("privacy.notifications", "privacy", RiskLevel.Limited,
            "Deny apps access to your notifications",
            "Apps can no longer read the notifications other apps show you.",
            [
                .. DenyCapabilities("userNotificationListener"),
                Lm(AppPrivacy, "LetAppsAccessNotifications", 2),
            ],
            warning: "Phone Link and smartwatch or fitness-band companion apps can't mirror your notifications."),

        T("privacy.radios", "privacy", RiskLevel.Limited,
            "Deny apps control of radios and syncing with unpaired devices",
            "Apps can no longer switch Bluetooth or Wi-Fi on and off, or talk to devices you haven't paired.",
            [
                .. DenyCapabilities("radios", "bluetoothSync"),
                Lm(AppPrivacy, "LetAppsAccessRadios", 2),
                Lm(AppPrivacy, "LetAppsSyncWithDevices", 2),
            ],
            warning: "Store apps for beacons, fitness bands and similar unpaired devices stop working."),

        T("privacy.files", "privacy", RiskLevel.Limited,
            "Deny apps access to documents, pictures, videos, music, downloads and the file system",
            "Store apps can only open files you pick or double-click yourself. Desktop programs are unaffected.",
            DenyCapabilities("documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary",
                "downloadsFolder", "broadFileSystemAccess"),
            warning: "Store apps like Photos, Media Player and Clipchamp no longer show your libraries automatically."),

        T("privacy.screenshots", "privacy", RiskLevel.Limited,
            "Deny apps programmatic screen capture",
            "Apps can no longer take screenshots or record the screen on their own, or do it without the yellow capture border.",
            DenyCapabilities("graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder"),
            warning: "Screen sharing in some Store apps (such as new Teams) asks every time or fails."),

        T("privacy.sensors", "privacy", RiskLevel.Limited,
            "Disable sensors",
            "Turns off the Windows sensor platform (ambient light, orientation, proximity and similar) for all apps.",
            [ Lm(PolWin + @"\LocationAndSensors", "DisableSensors", 1) ],
            warning: "Auto-rotate and automatic brightness stop working on laptops and tablets."),

        // ===================================================================================
        // AI, Copilot & Recall
        // ===================================================================================
        T("ai.copilot", "ai", RiskLevel.Recommended,
            "Disable Windows Copilot",
            "Turns off Copilot in Windows via policy and hides the taskbar button.",
            [
                Lm(PolWin + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Cu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Cu(ExplorerAdv, "ShowCopilotButton", 0),
            ]),

        T("ai.recall", "ai", RiskLevel.Recommended,
            "Disable Recall and its screen snapshots",
            "Stops Recall saving snapshots of your screen and prevents it being enabled. Only matters on Copilot+ PCs.",
            [
                Lm(WindowsAI, "DisableAIDataAnalysis", 1),
                Lm(WindowsAI, "AllowRecallEnablement", 0),
                Cu(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            ]),

        T("ai.clicktodo", "ai", RiskLevel.Recommended,
            "Disable Click to Do",
            "Turns off the feature that analyses what's on screen to suggest actions.",
            [
                Lm(WindowsAI, "DisableClickToDo", 1),
                Cu(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1),
            ]),

        T("ai.apps", "ai", RiskLevel.Limited,
            "Disable AI features in Paint and Notepad",
            "Removes Cocreator, Image Creator and generative fill from Paint, and Copilot rewrite from Notepad.",
            [
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                Lm(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
            ]),

        // ===================================================================================
        // Start, search & ads
        // ===================================================================================
        T("search.bing", "search", RiskLevel.Recommended,
            "Disable Bing web results in Start menu search",
            "What you type into Start stays on your PC instead of being sent to Bing.",
            [
                Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
                Lm(WinSearch, "DisableWebSearch", 1),
                Lm(WinSearch, "ConnectedSearchUseWeb", 0),
            ]),

        T("search.cortana", "search", RiskLevel.Recommended,
            "Disable Cortana",
            "Prevents Cortana from running and collecting data, including above the lock screen.",
            [
                Lm(WinSearch, "AllowCortana", 0),
                Lm(WinSearch, "AllowCortanaAboveLock", 0),
            ]),

        T("search.location", "search", RiskLevel.Recommended,
            "Don't let search use your location",
            "Windows Search stops sending your location along with search queries.",
            [ Lm(WinSearch, "AllowSearchToUseLocation", 0) ]),

        T("search.cloud", "search", RiskLevel.Recommended,
            "Disable cloud content search and search history",
            "Search no longer pulls results from OneDrive, Outlook or your work account, and stops keeping device search history.",
            [
                Lm(WinSearch, "AllowCloudSearch", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsMSACloudSearchEnabled", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsAADCloudSearchEnabled", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDeviceSearchHistoryEnabled", 0),
            ]),

        T("search.highlights", "search", RiskLevel.Recommended,
            "Disable search highlights",
            "Removes the trending searches and illustrated \"highlights\" from the search box.",
            [ Lm(WinSearch, "EnableDynamicContentInWSB", 0) ]),

        T("search.suggestions", "search", RiskLevel.Recommended,
            "Disable suggested content, tips and app suggestions",
            "Turns off suggestions in Start and Settings, \"tips and tricks\" notifications and silently installed sponsored apps.",
            [
                Cu(Cdm, "SilentInstalledAppsEnabled", 0),
                Cu(Cdm, "SystemPaneSuggestionsEnabled", 0),
                Cu(Cdm, "SoftLandingEnabled", 0),
                Cu(Cdm, "PreInstalledAppsEnabled", 0),
                Cu(Cdm, "OemPreInstalledAppsEnabled", 0),
                Cu(Cdm, "SubscribedContent-310093Enabled", 0),
                Cu(Cdm, "SubscribedContent-338388Enabled", 0),
                Cu(Cdm, "SubscribedContent-338389Enabled", 0),
                Cu(Cdm, "SubscribedContent-338393Enabled", 0),
                Cu(Cdm, "SubscribedContent-353694Enabled", 0),
                Cu(Cdm, "SubscribedContent-353696Enabled", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
            ]),

        T("search.consumer", "search", RiskLevel.Recommended,
            "Block automatic install of sponsored apps",
            "Disables \"Microsoft consumer experiences\", which installs games and promoted apps for new users.",
            [
                Lm(CloudContentLm, "DisableWindowsConsumerFeatures", 1),
                Lm(CloudContentLm, "DisableSoftLanding", 1),
                Lm(CloudContentLm, "DisableCloudOptimizedContent", 1),
            ]),

        T("search.startads", "search", RiskLevel.Recommended,
            "Remove recommendations and account ads from Start",
            "Hides promoted apps in the Start \"Recommended\" section, Microsoft account nags and sync-provider ads in File Explorer.",
            [
                Cu(ExplorerAdv, "Start_IrisRecommendations", 0),
                Cu(ExplorerAdv, "Start_AccountNotifications", 0),
                Cu(ExplorerAdv, "ShowSyncProviderNotifications", 0),
            ]),

        T("search.widgets", "search", RiskLevel.Limited,
            "Disable Widgets and news feed",
            "Removes the Widgets board and its MSN news/ads feed from the taskbar.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0) ]),

        T("search.spotlight", "search", RiskLevel.Limited,
            "Disable Windows Spotlight and lock-screen tips",
            "Stops the rotating Bing lock-screen/desktop images and the ads and \"fun facts\" shown over them.",
            [
                Cu(CloudContentCu, "DisableWindowsSpotlightFeatures", 1),
                Cu(Cdm, "RotatingLockScreenEnabled", 0),
                Cu(Cdm, "RotatingLockScreenOverlayEnabled", 0),
            ]),

        // ===================================================================================
        // Browsers
        // ===================================================================================
        T("edge.telemetry", "edge", RiskLevel.Recommended,
            "Disable Edge diagnostic data and feedback",
            "Stops Edge sending diagnostic data, browsing-based personalisation data and feedback.",
            [
                Lm(Edge, "DiagnosticData", 0),
                Lm(Edge, "MetricsReportingEnabled", 0),
                Lm(Edge, "PersonalizationReportingEnabled", 0),
                Lm(Edge, "UserFeedbackAllowed", 0),
                Lm(Edge, "ConfigureDoNotTrack", 1),
            ]),

        T("edge.bloat", "edge", RiskLevel.Recommended,
            "Disable Edge shopping, recommendations, sidebar and Copilot",
            "Turns off the shopping assistant, product recommendations, the sidebar with Copilot, and promotional tabs.",
            [
                Lm(Edge, "EdgeShoppingAssistantEnabled", 0),
                Lm(Edge, "ShowRecommendationsEnabled", 0),
                Lm(Edge, "SpotlightExperiencesAndRecommendationsEnabled", 0),
                Lm(Edge, "HubsSidebarEnabled", 0),
                Lm(Edge, "CopilotPageContext", 0),
                Lm(Edge, "PromotionalTabsEnabled", 0),
                Lm(Edge, "ShowMicrosoftRewards", 0),
            ]),

        T("edge.background", "edge", RiskLevel.Recommended,
            "Stop Edge running in the background",
            "Disables Startup Boost and background mode, so Edge only runs while it's open.",
            [
                Lm(Edge, "StartupBoostEnabled", 0),
                Lm(Edge, "BackgroundModeEnabled", 0),
            ]),

        T("edge.webservices", "edge", RiskLevel.Limited,
            "Disable Edge search suggestions and web services",
            "Stops sending what you type in the address bar to Bing, and turns off web-based error page and typo resolution and link prefetching.",
            [
                Lm(Edge, "SearchSuggestEnabled", 0),
                Lm(Edge, "AddressBarMicrosoftSearchInBingProviderEnabled", 0),
                Lm(Edge, "ResolveNavigationErrorsUseWebService", 0),
                Lm(Edge, "AlternateErrorPagesEnabled", 0),
                Lm(Edge, "NetworkPredictionOptions", 2),
            ]),

        T("browser.brave", "edge", RiskLevel.Recommended,
            "Brave: turn off Rewards, Wallet, VPN, Leo AI, Talk, Tor and usage pings",
            "Removes Brave's crypto, VPN and AI extras and stops its anonymous usage reporting (P3A), daily stats ping and Web Discovery Project.",
            [
                Lm(BravePol, "BraveRewardsDisabled", 1),
                Lm(BravePol, "BraveWalletDisabled", 1),
                Lm(BravePol, "BraveVPNDisabled", 1),
                Lm(BravePol, "BraveAIChatEnabled", 0),
                Lm(BravePol, "BraveTalkDisabled", 1),
                Lm(BravePol, "TorDisabled", 1),
                Lm(BravePol, "BraveP3AEnabled", 0),
                Lm(BravePol, "BraveStatsPingEnabled", 0),
                Lm(BravePol, "BraveWebDiscoveryEnabled", 0),
                Lm(BravePol, "MetricsReportingEnabled", 0),
            ]),

        T("browser.firefox", "edge", RiskLevel.Recommended,
            "Firefox: turn off telemetry, studies, Pocket and the default-browser agent",
            "Stops Firefox sending telemetry and running Mozilla studies (Shield), removes Pocket, and disables the background task that reports your default browser to Mozilla.",
            [
                Lm(FirefoxPol, "DisableTelemetry", 1),
                Lm(FirefoxPol, "DisableFirefoxStudies", 1),
                Lm(FirefoxPol, "DisablePocket", 1),
                Lm(FirefoxPol, "DisableDefaultBrowserAgent", 1),
                Lm(FirefoxPol, "DisableFeedbackCommands", 1),
            ]),

        T("browser.chrome", "edge", RiskLevel.Recommended,
            "Chrome: turn off usage reporting and Privacy Sandbox ad tracking",
            "Stops Chrome's usage statistics, URL-keyed data collection and feedback, and turns off Privacy Sandbox ad topics, site-suggested ads and ad measurement.",
            [
                Lm(ChromePol, "MetricsReportingEnabled", 0),
                Lm(ChromePol, "UrlKeyedAnonymizedDataCollectionEnabled", 0),
                Lm(ChromePol, "UserFeedbackAllowed", 0),
                Lm(ChromePol, "ChromeCleanupReportingEnabled", 0),
                Lm(ChromePol, "SafeBrowsingExtendedReportingEnabled", 0),
                Lm(ChromePol, "PrivacySandboxPromptEnabled", 0),
                Lm(ChromePol, "PrivacySandboxAdTopicsEnabled", 0),
                Lm(ChromePol, "PrivacySandboxSiteEnabledAdsEnabled", 0),
                Lm(ChromePol, "PrivacySandboxAdMeasurementEnabled", 0),
            ]),

        // ===================================================================================
        // Windows Update, Defender & SmartScreen
        // ===================================================================================
        T("updates.p2p", "updates", RiskLevel.Recommended,
            "Disable peer-to-peer update sharing",
            "Delivery Optimization will download updates from Microsoft only, never uploading pieces to other PCs on the internet.",
            [ Lm(PolWin + @"\DeliveryOptimization", "DODownloadMode", 0) ]),

        T("updates.mrt", "defender", RiskLevel.Recommended,
            "Don't send Malicious Software Removal Tool reports",
            "MRT still runs monthly but no longer reports infection data to Microsoft.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\MRT", "DontReportInfectionInformation", 1) ]),

        T("updates.kms", "telemetry", RiskLevel.Recommended,
            "Disable online activation-ticket telemetry",
            "Stops the Software Protection Platform sending AVS validation data after activation.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\Windows NT\CurrentVersion\Software Protection Platform", "NoGenTicket", 1) ]),

        T("updates.drivers", "updates", RiskLevel.Limited,
            "Don't install drivers through Windows Update",
            "Driver updates must be installed by you, from the manufacturer.",
            [ Lm(PolWin + @"\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1) ]),

        T("updates.activehours", "updates", RiskLevel.Recommended,
            "Set active hours so Windows never restarts while you use the PC",
            "Windows Update won't restart during your active hours. Pick your own hours in the card above; the default 08:00-02:00 is the widest window Windows allows (18 hours).",
            [ ActiveHours.Action() ]),

        T("updates.defer", "updates", RiskLevel.Limited,
            "Security-first updates: delay feature updates by a year",
            "Security and quality fixes still arrive (4 days after release, once early problems are caught); big feature updates, which bring new features, ads and re-enabled settings, wait 365 days. Pro, Education and Enterprise only.",
            [
                Lm(PolWin + @"\WindowsUpdate", "DeferFeatureUpdates", 1),
                Lm(PolWin + @"\WindowsUpdate", "DeferFeatureUpdatesPeriodInDays", 365),
                Lm(PolWin + @"\WindowsUpdate", "DeferQualityUpdates", 1),
                Lm(PolWin + @"\WindowsUpdate", "DeferQualityUpdatesPeriodInDays", 4),
            ],
            warning: "New Windows features arrive up to a year later. Ignored on Windows Home."),

        T("updates.stayonversion", "updates", RiskLevel.Limited,
            $"Stay on {WindowsVersion.ProductName} {WindowsVersion.DisplayVersion}",
            "Windows Update keeps installing security updates but won't upgrade you to a newer Windows version until you revert this. Pro, Education and Enterprise only.",
            [
                Lm(PolWin + @"\WindowsUpdate", "TargetReleaseVersion", 1),
                LmStr(PolWin + @"\WindowsUpdate", "ProductVersion", WindowsVersion.ProductName),
                LmStr(PolWin + @"\WindowsUpdate", "TargetReleaseVersionInfo", WindowsVersion.DisplayVersion),
            ],
            warning: "Each Windows version only gets security updates for about 2 years (Home/Pro). Revert before it reaches end of support.",
            blocker: WindowsVersion.StayOnVersionBlocker),

        T("updates.storeauto", "updates", RiskLevel.Limited,
            "Turn off automatic Microsoft Store app updates",
            "Store apps update only when you open the Store's Library and press Update. Stops background downloads and surprise app changes.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2) ],
            warning: "Store apps (including Photos, Terminal and codecs) stop getting security fixes until you update them yourself."),

        T("updates.noreboot", "updates", RiskLevel.Recommended,
            "Never restart automatically while someone is signed in",
            "Windows Update waits for you to restart instead of rebooting on its own and closing your work.",
            [ Lm(PolWin + @"\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1) ]),

        T("updates.experiments", "updates", RiskLevel.Recommended,
            "Block Microsoft feature experiments on this PC",
            "Stops Microsoft remotely switching trial features and settings on and off for this PC.",
            [ Lm(@"SOFTWARE\Microsoft\PolicyManager\current\device\System", "AllowExperimentation", 0) ]),

        T("updates.insider", "updates", RiskLevel.Limited,
            "Block Windows Insider preview builds",
            "This PC can't be enrolled in the Insider programme, which collects extra diagnostic data.",
            [ Lm(PolWin + @"\PreviewBuilds", "AllowBuildPreview", 0) ]),

        T("updates.spynet", "defender", RiskLevel.Aggressive,
            "Disable Defender cloud protection and sample submission",
            "Defender stops sending file metadata and suspicious samples to Microsoft.",
            [
                Lm(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SpynetReporting", 0),
                Lm(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SubmitSamplesConsent", 2),
            ],
            warning: "Weakens malware protection: new threats are detected later. Ignored while Tamper Protection is on (turn it off in Windows Security first)."),

        T("updates.smartscreen", "defender", RiskLevel.Aggressive,
            "Disable SmartScreen",
            "Windows and Edge stop sending the URLs and downloads you open to Microsoft for reputation checks.",
            [
                Lm(SystemPol, "EnableSmartScreen", 0),
                Lm(Edge, "SmartScreenEnabled", 0),
                Lm(Edge, "SmartScreenPuaEnabled", 0),
            ],
            warning: "You lose warnings about phishing sites and known-malicious downloads."),

        T("updates.auto", "updates", RiskLevel.Aggressive,
            "Disable automatic Windows Update",
            "Windows Update only runs when you click \"Check for updates\" yourself.",
            [ Lm(PolWin + @"\WindowsUpdate\AU", "NoAutoUpdate", 1) ],
            warning: "You must install security updates manually. Unpatched PCs get compromised."),

        // ===================================================================================
        // Network blocking
        // ===================================================================================
        T("network.hotspots", "network", RiskLevel.Recommended,
            "Don't connect automatically to open or suggested hotspots",
            "Windows no longer joins open Wi-Fi hotspots on its own or shares hotspot information with Microsoft.",
            [
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\wifinetworkmanager\config", "AutoConnectAllowedOEM", 0),
                Lm(@"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots", "value", 0),
                Lm(@"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting", "value", 0),
            ]),

        T("network.doh", "network", RiskLevel.Recommended,
            "Encrypt DNS lookups when the DNS server supports it",
            "Turns on automatic DNS over HTTPS, so your ISP and public Wi-Fi can't see or tamper with the sites you look up. Works with Cloudflare, Google, Quad9 and other servers Windows knows. Windows 11 only.",
            [ Lm(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "EnableAutoDoh", 2) ]),

        T("network.ipv6", "network", RiskLevel.Limited,
            "Prefer IPv4 and turn off IPv6 tunnelling (Teredo, 6to4, ISATAP)",
            "IPv6 stays available, but Windows prefers IPv4 and stops creating tunnel adapters. Tunnels can route traffic around a VPN and leak your real address. Takes effect after a restart.",
            [ Lm(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x21) ],
            warning: "Xbox Live party chat and some peer-to-peer games use Teredo; they may report a \"Strict\" NAT type."),

        T("network.ntp", "network", RiskLevel.Limited,
            "Sync the clock with pool.ntp.org instead of time.windows.com",
            "Windows Time uses the volunteer NTP Pool Project rather than contacting Microsoft every time it syncs the clock. Takes effect after a restart.",
            [
                LmStr(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer", "0.pool.ntp.org,0x9 1.pool.ntp.org,0x9 2.pool.ntp.org,0x9"),
                LmStr(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "Type", "NTP"),
            ],
            warning: "Domain-joined (work) PCs should keep syncing from their domain controller. Don't apply this on them."),

        T("network.ncsi", "network", RiskLevel.Aggressive,
            "Disable the connectivity check to Microsoft",
            "Windows stops contacting msftconnecttest.com every time a network connects.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator", "NoActiveProbe", 1) ],
            warning: "The network icon may show \"No internet\", hotel/airport sign-in pages won't pop up, and some apps (Store, Outlook) may think you're offline."),

        T("network.telemetry", "network", RiskLevel.Limited,
            "Block telemetry endpoints in the hosts file",
            "Points known Microsoft telemetry, error-reporting and feedback hosts at 0.0.0.0.",
            [
                Hosts("telemetry",
                    "vortex.data.microsoft.com", "vortex-win.data.microsoft.com", "vortex-sandbox.data.microsoft.com",
                    "v10.events.data.microsoft.com", "v10.vortex-win.data.microsoft.com", "v20.events.data.microsoft.com",
                    "self.events.data.microsoft.com", "mobile.events.data.microsoft.com",
                    "umwatson.events.data.microsoft.com", "watson.events.data.microsoft.com", "kmwatsonc.events.data.microsoft.com",
                    "telecommand.telemetry.microsoft.com", "telecommand.telemetry.microsoft.com.nsatc.net",
                    "oca.telemetry.microsoft.com", "oca.telemetry.microsoft.com.nsatc.net",
                    "sqm.telemetry.microsoft.com", "sqm.telemetry.microsoft.com.nsatc.net",
                    "watson.telemetry.microsoft.com", "watson.telemetry.microsoft.com.nsatc.net",
                    "watson.ppe.telemetry.microsoft.com", "telemetry.microsoft.com", "telemetry.urs.microsoft.com",
                    "telemetry.appex.bing.net", "df.telemetry.microsoft.com", "wes.df.telemetry.microsoft.com",
                    "reports.wes.df.telemetry.microsoft.com", "services.wes.df.telemetry.microsoft.com",
                    "sqm.df.telemetry.microsoft.com", "redir.metaservices.microsoft.com",
                    "choice.microsoft.com", "choice.microsoft.com.nsatc.net", "settings-sandbox.data.microsoft.com",
                    "survey.watson.microsoft.com", "watson.live.com", "watson.microsoft.com",
                    "statsfe1.ws.microsoft.com", "statsfe2.ws.microsoft.com", "compatexchange.cloudapp.net",
                    "diagnostics.support.microsoft.com", "corpext.msitadfs.glbdns2.microsoft.com",
                    "feedback.windows.com", "feedback.microsoft-hohm.com", "feedback.search.microsoft.com",
                    "mobile.pipe.aria.microsoft.com", "browser.events.data.msn.com",
                    "ceuswatcab01.blob.core.windows.net", "ceuswatcab02.blob.core.windows.net",
                    "eaus2watcab01.blob.core.windows.net", "eaus2watcab02.blob.core.windows.net",
                    "weus2watcab01.blob.core.windows.net", "weus2watcab02.blob.core.windows.net"),
            ],
            warning: "Microsoft Defender may flag this hosts file as \"SettingsModifier:Win32/HostsFileHijack\" and offer to undo it. Choose \"Allow on device\" to keep it."),

        T("network.ads", "network", RiskLevel.Aggressive,
            "Block Microsoft ad, Spotlight and MSN content hosts",
            "Points Microsoft advertising, Spotlight and MSN feed hosts at 0.0.0.0.",
            [
                Hosts("ads",
                    "arc.msn.com", "ris.api.iris.microsoft.com", "fd.api.iris.microsoft.com",
                    "ads.msn.com", "ads1.msn.com", "rad.msn.com", "a.ads1.msn.com", "a.ads2.msn.com",
                    "ads1.msads.net", "adnexus.net", "adnxs.com", "c.msn.com", "c.bing.com",
                    "assets.msn.com", "api.msn.com", "ntp.msn.com", "srtb.msn.com"),
            ],
            warning: "Breaks Widgets, Spotlight wallpapers, the weather in the taskbar and the Edge new-tab news feed."),

        T("network.core", "network", RiskLevel.Extreme,
            "Block Microsoft account, Store, OneDrive, Update, Bing and Copilot",
            "The \"block Microsoft entirely\" option. Blackholes sign-in, Windows Update, Microsoft Store, OneDrive, Bing, Copilot and Edge cloud services in the hosts file. Defender definition updates still work through their separate endpoint.",
            [
                Hosts("core",
                    "login.live.com", "login.microsoftonline.com", "account.live.com", "account.microsoft.com",
                    "onedrive.live.com", "skyapi.live.net", "oneclient.sfx.ms", "g.live.com",
                    "windowsupdate.microsoft.com", "update.microsoft.com", "www.update.microsoft.com",
                    "download.windowsupdate.com", "fe2.update.microsoft.com", "fe3.delivery.mp.microsoft.com",
                    "sls.update.microsoft.com", "slscr.update.microsoft.com", "emdl.ws.microsoft.com",
                    "dl.delivery.mp.microsoft.com", "tlu.dl.delivery.mp.microsoft.com",
                    "tsfe.trafficshaping.dsp.mp.microsoft.com",
                    "displaycatalog.mp.microsoft.com", "storeedgefd.dsx.mp.microsoft.com",
                    "licensing.mp.microsoft.com", "purchase.mp.microsoft.com",
                    "settings-win.data.microsoft.com", "settings.data.microsoft.com",
                    "config.edge.skype.com", "edge.microsoft.com", "msedge.api.cdp.microsoft.com",
                    "copilot.microsoft.com", "sydney.bing.com", "www.bing.com", "bing.com", "api.bing.com"),
            ],
            warning: "You will not receive Windows security updates, cannot sign in with a Microsoft account, use the Store, OneDrive, Teams/Office sign-in, Copilot or Bing. Revert this before you need any of them."),

        // ===================================================================================
        // Performance  (plus shared: Bing/cloud search, highlights, Widgets, background apps)
        // ===================================================================================
        T("perf.searchindex", "performance", RiskLevel.Recommended,
            "Keep the search indexer out of the way",
            "Indexing pauses on battery and backs off while you're using the PC, so it doesn't compete with apps and games. Search still finds everything.",
            [
                Lm(WinSearch, "PreventIndexOnBattery", 1),
                Lm(WinSearch, "PreventIndexingLowDiskSpaceMB", 2048),
            ]),

        T("perf.searchindex.off", "performance", RiskLevel.Aggressive,
            "Turn off Windows Search indexing",
            "Stops the Windows Search service. Start still finds apps and settings instantly; searching file contents becomes a slow live scan.",
            [ Svc("WSearch", ServiceStart.Disabled, ServiceStart.Automatic) ],
            warning: "File Explorer and Outlook search get much slower, and search inside Outlook/Office may stop working."),

        T("perf.explorer", "performance", RiskLevel.Recommended,
            "Open File Explorer to This PC, without recent files",
            "Explorer opens straight to your drives instead of Home, which first has to load recent and frequent files and the Gallery.",
            [
                Cu(ExplorerAdv, "LaunchTo", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowFrequent", 0),

            ]),

        T("perf.folderdiscovery", "performance", RiskLevel.Recommended,
            "Stop File Explorer guessing folder types",
            "Big folders such as Downloads open faster because Explorer no longer scans their contents to pick a \"Pictures\" or \"Music\" layout.",
            [ CuStr(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified") ]),

        T("perf.startupdelay", "performance", RiskLevel.Recommended,
            "Start startup apps without the built-in delay",
            "Windows normally waits several seconds after sign-in before launching startup apps. This removes that wait.",
            [ Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0) ]),

        T("perf.menudelay", "performance", RiskLevel.Recommended,
            "Open menus and submenus faster",
            "Cuts the hover delay before submenus open from 400 ms to 100 ms.",
            [ CuStr(@"Control Panel\Desktop", "MenuShowDelay", "100") ]),

        T("perf.lastaccess", "performance", RiskLevel.Recommended,
            "Don't update \"last accessed\" times on every file read",
            "NTFS stops writing a timestamp each time a file is opened, saving disk writes. Very few programs use this.",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", unchecked((int)0x80000001)) ]),

        T("perf.services", "performance", RiskLevel.Limited,
            "Set rarely used services to start only when needed",
            "Offline Maps and Distributed Link Tracking start on demand instead of at boot; the retail demo service is disabled.",
            [
                Svc("MapsBroker", ServiceStart.Manual, ServiceStart.Automatic),
                Svc("TrkWks", ServiceStart.Manual, ServiceStart.Automatic),
                Svc("RetailDemo", ServiceStart.Disabled, ServiceStart.Manual),
            ],
            warning: "Offline maps update only when you open Maps; shortcuts to files moved between NTFS drives may not be repaired automatically."),

        T("perf.animations", "performance", RiskLevel.Limited,
            "Turn off window and taskbar animations",
            "Windows snap open and closed instantly instead of animating. Smooth fonts and thumbnails stay on. Takes effect after signing out.",
            [
                CuStr(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                Cu(ExplorerAdv, "TaskbarAnimations", 0),
                Cu(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3),
            ]),

        T("perf.transparency", "performance", RiskLevel.Limited,
            "Turn off transparency effects",
            "Start, the taskbar and Settings use solid colours instead of blurred see-through backgrounds, which helps older or integrated GPUs.",
            [ Cu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0) ]),

        T("perf.powerplan", "performance", RiskLevel.Limited,
            "Use a maximum-performance power plan",
            "Activates \"Lockstep Performance\", a copy of Microsoft's Ultimate Performance plan: no CPU parking or idle down-clocking. Revert switches back to your previous plan.",
            [ PowerActions.HighPerformancePlan() ],
            warning: "Uses more power and runs hotter. Not recommended on laptops running on battery."),

        T("perf.hibernation", "performance", RiskLevel.Limited,
            "Turn off hibernation and Fast Startup",
            "Frees disk space equal to a large part of your RAM (hiberfil.sys) and makes every shutdown a clean one, which avoids many \"slow after boot\" and driver issues.",
            [ PowerActions.DisableHibernation() ],
            warning: "Cold boots can take a few seconds longer, and Hibernate disappears from the power menu."),

        // ===================================================================================
        // Gaming  (plus shared: Game DVR, power plan, background apps, no auto-restart)
        // ===================================================================================
        T("gaming.gamemode", "gaming", RiskLevel.Recommended,
            "Turn on Game Mode",
            "Windows prioritises the game you're playing and holds back Windows Update installs and driver installs while it runs.",
            [
                Cu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
                Cu(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
            ]),

        T("gaming.gamebarpopups", "gaming", RiskLevel.Recommended,
            "Stop Game Bar pop-ups",
            "No Game Bar tips when a game starts, and the controller's Xbox button no longer opens Game Bar.",
            [
                Cu(@"Software\Microsoft\GameBar", "ShowStartupPanel", 0),
                Cu(@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0),
            ]),

        T("gaming.windowed", "gaming", RiskLevel.Recommended,
            "Turn on optimizations for windowed games",
            "Lets windowed and borderless DirectX 10/11 games use the faster flip presentation model, lowering input latency and enabling VRR. Windows 11.",
            [ CuStr(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;") ]),

        T("gaming.mouse", "gaming", RiskLevel.Limited,
            "Turn off mouse acceleration (\"Enhance pointer precision\")",
            "The pointer moves the same distance for the same hand movement, whatever the speed, which makes aiming consistent.",
            [
                CuStr(@"Control Panel\Mouse", "MouseSpeed", "0"),
                CuStr(@"Control Panel\Mouse", "MouseThreshold1", "0"),
                CuStr(@"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
            warning: "On a large screen with a low-DPI mouse you may need to raise the pointer speed. Takes effect after signing out."),

        T("gaming.hags", "gaming", RiskLevel.Limited,
            "Turn on hardware-accelerated GPU scheduling",
            "The graphics card manages its own memory and scheduling, which can lower latency. Needed for DLSS Frame Generation. Requires a supported GPU and driver; takes effect after a restart.",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2) ],
            warning: "Some older games, capture software and multi-GPU setups stutter with it on. Revert if you see problems."),

        T("gaming.priority", "gaming", RiskLevel.Limited,
            "Give games priority for CPU, GPU and disk",
            "Tunes the Multimedia Class Scheduler so games get more CPU time, higher GPU and disk I/O priority, and network traffic isn't throttled during media playback. Gains are usually small and hardware-dependent.",
            [
                Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10),
                Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF)),
                Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8),
                Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6),
                LmStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High"),
                LmStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High"),
            ],
            warning: "Background apps such as voice chat and streaming get slightly less CPU while a game runs."),

        T("gaming.powerthrottling", "gaming", RiskLevel.Limited,
            "Turn off power throttling",
            "Windows stops slowing down background processes (e.g. a game that loses focus, voice chat, recording software) to save power.",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1) ],
            warning: "Shorter battery life on laptops."),

        T("gaming.vbs", "gaming", RiskLevel.Aggressive,
            "Turn off Memory Integrity and virtualization-based security",
            "Microsoft itself suggests this for maximum gaming performance: some games and CPUs lose 5-15 % FPS with VBS on. Takes effect after a restart.",
            [
                Lm(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0),
                Lm(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0),
            ],
            warning: "Lowers protection against kernel-level malware and malicious drivers. Some anti-cheat systems (e.g. FACEIT, Vanguard) and Windows Hello Enhanced Sign-in require these on. If your PC enables them through a UEFI lock, this has no effect."),

        // ===================================================================================
        // Device identity (GDID)
        // ===================================================================================
        T("identity.cdp", "identity", RiskLevel.Limited,
            "Disable Connected Devices Platform",
            "Turns off the services that register this PC, with its GDID, in Microsoft's device directory (dds.microsoft.com) so it can be linked to your other devices. Takes effect after a restart.",
            [
                // The EnableCdp policy belongs to "Disable Phone Link" (system.phonelink); one owner per value.
                Svc("CDPSvc", ServiceStart.Disabled, ServiceStart.Automatic),
                Svc("CDPUserSvc", ServiceStart.Disabled, ServiceStart.Automatic),
            ],
            warning: "Breaks Phone Link, Nearby Share, cloud clipboard and \"continue on PC\"."),

        T("identity.deliveryoptimization", "identity", RiskLevel.Limited,
            "Bypass Delivery Optimization",
            "Windows Update downloads with plain BITS instead of Delivery Optimization, which reports the GDID together with your city, country and ISP. Overrides \"Disable peer-to-peer update sharing\".",
            [ Lm(PolWin + @"\DeliveryOptimization", "DODownloadMode", 99) ],
            warning: "Large updates can download more slowly. Store app downloads may fall back to a slower path."),

        T("identity.hosts", "identity", RiskLevel.Limited,
            "Block the device-directory and activity endpoints",
            "Points the Connected Devices Platform directory and activity-sync hosts at 0.0.0.0, so the GDID isn't published even if the services are re-enabled.",
            [ Hosts("gdid", "dds.microsoft.com", "activity.windows.com", "edge.activity.windows.com") ],
            warning: "Same effect as disabling Connected Devices Platform: Phone Link and cross-device features stop working."),

        T("identity.prevent", "identity", RiskLevel.Extreme,
            "Prevent a GDID being created",
            "Disables the Microsoft Account Sign-in Assistant (wlidsvc) and blocks login.live.com, which assigns the GDID. This is the only reported way to stop one being minted, and only on a PC that uses a local account. It does not delete an ID Microsoft already has on record.",
            [
                Svc("wlidsvc", ServiceStart.Disabled, ServiceStart.Manual),
                Hosts("gdid-login", "login.live.com"),
            ],
            warning: "Breaks Microsoft account sign-in everywhere, the Microsoft Store and Store apps, Xbox, OneDrive and Office sign-in, and can stop digital-license activation from re-validating.",
            blocker: DeviceIdentity.MicrosoftAccountBlocker),

        // ===================================================================================
        // System & apps
        // ===================================================================================
        T("system.remoteassist", "system", RiskLevel.Recommended,
            "Disable Remote Assistance",
            "Nobody can be invited to view or control this PC through Remote Assistance (Quick Assist is unaffected).",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0) ]),

        T("system.onedrive", "system", RiskLevel.Limited,
            "Disable OneDrive file sync",
            "Prevents OneDrive from syncing files and hides cloud files from Quick Access.",
            [
                Lm(PolWin + @"\OneDrive", "DisableFileSyncNGSC", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowCloudFilesInQuickAccess", 0),
            ],
            warning: "Files only stored in OneDrive won't be available offline any more."),

        T("system.onedrive.presignin", "system", RiskLevel.Recommended,
            "Stop OneDrive contacting Microsoft before you sign in to it",
            "OneDrive makes no network requests until you actually sign in to it.",
            [ Lm(@"SOFTWARE\Policies\Microsoft\OneDrive", "PreventNetworkTrafficPreUserSignIn", 1) ]),

        T("system.onedrive.explorer", "system", RiskLevel.Limited,
            "Hide OneDrive from File Explorer",
            "Removes the OneDrive entry from the File Explorer navigation pane. Your OneDrive folder stays where it is.",
            [ RegistryAction.Dword(RegistryHive.CurrentUser, @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 0) ]),

        T("system.onedrive.notifications", "system", RiskLevel.Recommended,
            "Turn off OneDrive notifications",
            "No more OneDrive pop-ups (\"On this day\" memories, storage warnings, backup nags). Same as switching OneDrive off in Settings > System > Notifications.",
            [ Cu(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Microsoft.SkyDrive.Desktop", "Enabled", 0) ]),

        T("system.onedrive.overlays", "system", RiskLevel.Limited,
            "Remove OneDrive's sync-status icons in File Explorer",
            "Removes the cloud and tick overlay badges OneDrive adds to files, which also frees up overlay slots Windows limits to 15 (Dropbox, Git and others share them). Restart Explorer afterwards.",
            [ OneDriveOverlays.Action() ],
            warning: "You can no longer see at a glance which files are online-only. OneDrive updates may add the icons back (Keep settings applied removes them again)."),
        T("system.msaccount", "system", RiskLevel.Aggressive,
            "Block adding Microsoft accounts",
            "New Microsoft accounts can't be added to Windows. Accounts that already exist, local accounts and work/school accounts keep working.",
            [ Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "NoConnectedUser", 1) ],
            warning: "You can't sign in to Windows, the Store or Settings with a new Microsoft account until this is reverted."),

        T("system.regbackup", "system", RiskLevel.Recommended,
            "Back up the registry automatically",
            "Turns Windows' own registry backup back on (off by default since 2018). During automatic maintenance it copies the registry to System32\\config\\RegBack, so a corrupted registry can be restored from Windows Recovery.",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager", "EnablePeriodicBackup", 1) ]),

        T("system.f8menu", "system", RiskLevel.Limited,
            "Bring back the F8 boot menu",
            "Pressing F8 while Windows starts opens the classic Advanced Boot Options (Safe Mode, last known good configuration…) instead of needing Settings or a recovery drive.",
            [ BootActions.LegacyBootMenu() ],
            warning: "Startup can take a second longer. If BitLocker is on, keep your recovery key handy in case Windows asks for it after the boot configuration changes."),

        T("system.devicesoftware", "system", RiskLevel.Limited,
            "Don't auto-install manufacturer apps for new devices",
            "Plugging in a mouse, keyboard or headset no longer silently installs the maker's companion software (Razer Synapse, Logitech, ASUS Armoury Crate and similar) from Windows Update.",
            [ Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers", 1) ],
            warning: "Devices still work with their standard drivers, but extra features (RGB, macros, DPI profiles) need the maker's app installed by hand."),
        T("system.wpbt", "system", RiskLevel.Limited,
            "Block programs embedded in the firmware (WPBT)",
            "Stops Windows running executables that a PC maker hides in the UEFI firmware's Windows Platform Binary Table at every boot. These have been used for persistent bloatware and anti-theft agents. Takes effect after a restart.",
            [ Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1) ],
            warning: "OEM anti-theft tools (e.g. Absolute/Computrace) and some vendor driver installers that rely on WPBT stop working."),

        T("system.devicemetadata", "system", RiskLevel.Recommended,
            "Don't download device companion apps and metadata",
            "When you plug in a device, Windows no longer fetches its icons, info and companion apps from the internet.",
            [
                Lm(PolWin + @"\Device Metadata", "PreventDeviceMetadataFromNetwork", 1),
                Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata", "PreventDeviceMetadataFromNetwork", 1),
            ]),

        T("system.gamedvr", "system", RiskLevel.Limited,
            "Disable Game DVR background recording",
            "Stops the Xbox Game Bar recording gameplay in the background.",
            [
                Lm(PolWin + @"\GameDVR", "AllowGameDVR", 0),
                Cu(@"System\GameConfigStore", "GameDVR_Enabled", 0),
            ]),

        T("system.phonelink", "system", RiskLevel.Limited,
            "Disable Phone Link and cross-device experiences",
            "Stops linking this PC to your phone and continuing tasks across devices.",
            [
                Lm(SystemPol, "EnableMmx", 0),
                Lm(SystemPol, "EnableCdp", 0),
            ]),
    ];
}

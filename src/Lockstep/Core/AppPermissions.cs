using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Lockstep.Core;

/// <summary>
/// Per-app privacy permissions, as shown in Settings > Privacy &amp; security. Windows keeps them in
/// CapabilityAccessManager\ConsentStore: one key per capability, one subkey per app (package family
/// name), each with Value = Allow/Deny, plus LastUsedTimeStart/Stop for apps that used it.
/// Desktop (non-Store) apps live under NonPackaged and can only be allowed or blocked as a group.
/// </summary>
public static class AppPermissions
{
    public sealed record Capability(string Id, string Name);

    public static IReadOnlyList<Capability> Capabilities { get; } =
    [
        new("webcam", "Camera"),
        new("microphone", "Microphone"),
        new("location", "Location"),
        new("contacts", "Contacts"),
        new("appointments", "Calendar"),
        new("phoneCallHistory", "Call history"),
        new("email", "Email"),
        new("userDataTasks", "Tasks"),
        new("chat", "Messaging"),
        new("userAccountInformation", "Account info"),
        new("userNotificationListener", "Notifications"),
        new("radios", "Radios"),
        new("bluetoothSync", "Other devices"),
        new("activity", "Motion"),
        new("gazeInput", "Eye tracking"),
        new("documentsLibrary", "Documents"),
        new("picturesLibrary", "Pictures"),
        new("videosLibrary", "Videos"),
        new("musicLibrary", "Music library"),
        new("downloadsFolder", "Downloads folder"),
        new("broadFileSystemAccess", "File system"),
        new("graphicsCaptureProgrammatic", "Screenshots"),
        new("appDiagnostics", "App diagnostics"),
    ];

    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public sealed record Entry(
        string Key,
        string DisplayName,
        bool IsDesktopApp,
        bool Allowed,
        DateTime? LastUsed,
        bool InUse);

    public sealed record State(
        string CapabilityId,
        bool BlockedForAllUsers,
        bool UserSwitchOn,
        IReadOnlyList<Entry> Apps);

    public static State Load(string capabilityId)
    {
        bool blockedMachineWide;
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = root.OpenSubKey($@"{Store.Replace("Software", "SOFTWARE")}\{capabilityId}"))
            blockedMachineWide = string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);

        using var cap = Registry.CurrentUser.OpenSubKey($@"{Store}\{capabilityId}");
        bool userOn = !string.Equals(cap?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);

        var apps = new List<Entry>();
        if (cap is not null)
        {
            var names = PackageNames();
            foreach (var sub in cap.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                using var app = cap.OpenSubKey(sub);
                if (app is null) continue;
                apps.Add(Read(app, sub, names.TryGetValue(sub, out var n) ? n : FriendlyFamilyName(sub), desktop: false));
            }

            using var nonPackaged = cap.OpenSubKey("NonPackaged");
            if (nonPackaged is not null)
            {
                foreach (var sub in nonPackaged.GetSubKeyNames())
                {
                    using var app = nonPackaged.OpenSubKey(sub);
                    if (app is null) continue;
                    var path = sub.Replace('#', '\\');
                    apps.Add(Read(app, "NonPackaged\\" + sub, Path.GetFileName(path) + "  (" + Path.GetDirectoryName(path) + ")", desktop: true));
                }
            }
        }

        return new State(capabilityId, blockedMachineWide, userOn,
            apps.OrderByDescending(a => a.InUse)
                .ThenByDescending(a => a.LastUsed ?? DateTime.MinValue)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    private static Entry Read(RegistryKey app, string key, string name, bool desktop)
    {
        bool allowed = !string.Equals(app.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        DateTime? Time(string value) => app.GetValue(value) is long ft && ft > 0 ? DateTime.FromFileTimeUtc(ft) : null;
        var start = Time("LastUsedTimeStart");
        var stop = Time("LastUsedTimeStop");
        bool inUse = start is not null && (stop is null || stop < start);
        return new Entry(key, name, desktop, allowed, stop ?? start, inUse);
    }

    /// <summary>Allows or denies one app (Store apps only; same as the switch in Settings).</summary>
    public static void SetAllowed(string capabilityId, string appKey, bool allowed)
    {
        if (appKey.StartsWith("NonPackaged", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Desktop apps can only be allowed or blocked together.");
        using var key = Registry.CurrentUser.CreateSubKey($@"{Store}\{capabilityId}\{appKey}");
        key.SetValue("Value", allowed ? "Allow" : "Deny", RegistryValueKind.String);
    }

    /// <summary>The user's master switch for this capability (all apps, including desktop apps).</summary>
    public static void SetUserSwitch(string capabilityId, bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Store}\{capabilityId}");
        key.SetValue("Value", on ? "Allow" : "Deny", RegistryValueKind.String);
    }

    /// <summary>Desktop apps share one switch per capability.</summary>
    public static void SetDesktopApps(string capabilityId, bool allowed)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Store}\{capabilityId}\NonPackaged");
        key.SetValue("Value", allowed ? "Allow" : "Deny", RegistryValueKind.String);
    }

    private static Dictionary<string, string>? _names;

    private static Dictionary<string, string> PackageNames()
    {
        if (_names is not null) return _names;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in new PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    var family = p.Id.FamilyName;
                    if (names.ContainsKey(family) || p.IsFramework || p.IsResourcePackage) continue;
                    var display = p.DisplayName;
                    if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        names[family] = display;
                }
                catch { }
            }
        }
        catch { }
        return _names = names;
    }

    private static string FriendlyFamilyName(string family)
    {
        var name = family.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}

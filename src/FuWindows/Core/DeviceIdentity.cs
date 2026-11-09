using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace FuWindows.Core;

/// <summary>
/// Inspects and resets the Windows Global Device ID (GDID). The GDID is a device PUID assigned by
/// Microsoft's login servers (via the wlidsvc service and login.live.com) and cached per user in
/// HKCU\SOFTWARE\Microsoft\IdentityCRL. Public research (July 2026) reports it is sent back to
/// Microsoft by Connected Devices Platform, Delivery Optimization and telemetry.
///
/// Limits, stated plainly in the UI: there is no Microsoft-supported way to change it, and clearing
/// the local cache makes Windows request a new one, which Microsoft may link to the old one through
/// your hardware, account and activation history.
/// </summary>
public static class DeviceIdentity
{
    private const string IdentityCrl = @"SOFTWARE\Microsoft\IdentityCRL";
    private const string ExtendedProperties = IdentityCrl + @"\ExtendedProperties";
    private const string DeviceTokens = IdentityCrl + @"\Immersive\production\Token";

    public sealed record Status(
        string? Gdid,
        bool SignedInWithMicrosoftAccount,
        int MicrosoftAccountsInApps,
        string AccountService,
        string ConnectedDevicesPlatform,
        string DeliveryOptimization,
        string TelemetryService,
        bool HasBackup);

    /// <summary>
    /// True when the current Windows sign-in is a Microsoft account. Those logons carry a group SID
    /// from the Microsoft Account authority (S-1-11-96-…).
    /// </summary>
    public static bool IsMicrosoftAccountLogon()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Groups?.Any(g => g.Value.StartsWith("S-1-11-96-", StringComparison.Ordinal)) == true;
    }

    /// <summary>The stored GDID as "g:&lt;decimal&gt;", or null if none is cached for this user.</summary>
    public static string? ReadGdid()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ExtendedProperties);
        if (key?.GetValue("LID") is not string lid || string.IsNullOrWhiteSpace(lid)) return null;

        return ulong.TryParse(lid.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var puid)
            ? $"g:{puid}"
            : lid.Trim();
    }

    /// <summary>Shows enough to compare before/after without exposing the full identifier.</summary>
    public static string Mask(string gdid) =>
        gdid.Length <= 9 ? gdid : $"{gdid[..6]}…{gdid[^3..]}";

    public static Status GetStatus()
    {
        int appAccounts = 0;
        using (var key = Registry.CurrentUser.OpenSubKey(IdentityCrl + @"\UserExtendedProperties"))
            appAccounts = key?.SubKeyCount ?? 0;

        return new Status(
            Gdid: ReadGdid(),
            SignedInWithMicrosoftAccount: IsMicrosoftAccountLogon(),
            MicrosoftAccountsInApps: appAccounts,
            AccountService: DescribeService("wlidsvc"),
            ConnectedDevicesPlatform: $"{DescribeService("CDPSvc")} (per-user: {DescribeService("CDPUserSvc")})",
            DeliveryOptimization: DescribeDeliveryOptimization(),
            TelemetryService: DescribeService("DiagTrack"),
            HasBackup: LatestBackup() is not null);
    }

    private static string DescribeService(string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return (key?.GetValue("Start") as int?) switch
        {
            null => "not present",
            2 => "on (automatic)",
            3 => "on demand (manual)",
            4 => "disabled",
            var other => $"start type {other}",
        };
    }

    private static string DescribeDeliveryOptimization()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization");
        return (key?.GetValue("DODownloadMode") as int?) switch
        {
            null => "Windows default (peer-to-peer on)",
            0 => "Microsoft servers only (still reports status)",
            99 => "bypass (Delivery Optimization not used)",
            100 => "bypass (legacy)",
            var other => $"mode {other}",
        };
    }

    // -----------------------------------------------------------------------------------------
    // Reset / restore
    // -----------------------------------------------------------------------------------------

    /// <summary>Backups live in the user's own profile: they contain DPAPI-protected device tickets.</summary>
    public static string BackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuWindows", "identity-backups");

    public static string? LatestBackup() =>
        Directory.Exists(BackupFolder)
            ? Directory.GetFiles(BackupFolder, "IdentityCRL-*.reg").OrderDescending().FirstOrDefault()
            : null;

    /// <summary>
    /// Exports HKCU\...\IdentityCRL, then clears the cached device ID and device tickets so Windows
    /// requests a fresh device identity. Apps signed in with a Microsoft account may ask you to
    /// sign in again.
    /// </summary>
    public static void ResetLocal(Action<string> log)
    {
        Directory.CreateDirectory(BackupFolder);
        var backup = Path.Combine(BackupFolder, $"IdentityCRL-{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        var export = ProcessRunner.Run(ProcessRunner.System32("reg.exe"),
            $"export \"HKCU\\{IdentityCrl}\" \"{backup}\" /y");
        if (!export.Success) throw new InvalidOperationException($"Backup failed, nothing changed: {export.Message}");
        log($"  ✓ backed up to {backup}");

        var before = ReadGdid();
        ProcessRunner.Run(ProcessRunner.System32("sc.exe"), "stop wlidsvc");

        using (var key = Registry.CurrentUser.OpenSubKey(ExtendedProperties, writable: true))
            key?.DeleteValue("LID", throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(DeviceTokens, throwOnMissingSubKey: false);
        log("  ✓ cleared cached device ID and device tickets");

        // Leave wlidsvc stopped; it is demand-start and Windows re-provisions when it next needs it.
        log(before is null
            ? "  No GDID was stored before the reset."
            : $"  Previous GDID {Mask(before)} removed. Windows requests a new one the next time it needs it (often after a restart or opening the Store).");
    }

    public static void RestoreLatest(Action<string> log)
    {
        var backup = LatestBackup() ?? throw new InvalidOperationException("No identity backup found.");
        ProcessRunner.Run(ProcessRunner.System32("sc.exe"), "stop wlidsvc");
        Registry.CurrentUser.DeleteSubKeyTree(DeviceTokens, throwOnMissingSubKey: false);

        var import = ProcessRunner.Run(ProcessRunner.System32("reg.exe"), $"import \"{backup}\"");
        if (!import.Success) throw new InvalidOperationException(import.Message);
        log($"  ✓ restored device identity from {backup}");
    }

    public static string? MicrosoftAccountBlocker() =>
        IsMicrosoftAccountLogon()
            ? "You sign in to Windows with a Microsoft account. Switch to a local account first (Settings > Accounts > Your info), otherwise this could stop you signing in."
            : null;
}

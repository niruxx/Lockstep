using Microsoft.Win32;

namespace Lockstep.Core;

/// <summary>The running Windows version, as Windows Update's target-release policy expects it.</summary>
public static class WindowsVersion
{
    private static string? Read(string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue(name)?.ToString();
    }

    public static int Build { get; } = int.TryParse(Read("CurrentBuildNumber"), out var b) ? b : 0;

    /// <summary>"Windows 11" or "Windows 10" (ProductName in the registry still says 10 on 11).</summary>
    public static string ProductName { get; } = Build >= 22000 ? "Windows 11" : "Windows 10";

    /// <summary>e.g. "24H2"; empty if it can't be read.</summary>
    public static string DisplayVersion { get; } = Read("DisplayVersion") ?? Read("ReleaseId") ?? "";

    public static string? StayOnVersionBlocker() =>
        DisplayVersion.Length == 0
            ? "Couldn't detect your Windows version (e.g. 24H2), so Lockstep won't guess one."
            : null;
}

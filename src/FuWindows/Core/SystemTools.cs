using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace FuWindows.Core;

public static class SystemTools
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Enables System Restore on the system drive and creates a restore point. The 24-hour
    /// creation throttle is lifted only for the duration of this call.
    /// </summary>
    public static ProcessResult CreateRestorePoint()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        const string name = "SystemRestorePointCreationFrequency";

        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(path, writable: true);
        var previous = key.GetValue(name);
        key.SetValue(name, 0, RegistryValueKind.DWord);
        try
        {
            return ProcessRunner.PowerShell("""
                $ErrorActionPreference = 'Stop'
                Enable-ComputerRestore -Drive "$env:SystemDrive\"
                Checkpoint-Computer -Description 'FuWindows' -RestorePointType 'MODIFY_SETTINGS'
                """);
        }
        finally
        {
            if (previous is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, previous, RegistryValueKind.DWord);
        }
    }

    public static void RestartExplorer()
    {
        ProcessRunner.Run(ProcessRunner.System32("taskkill.exe"), "/f /im explorer.exe");
        // Winlogon restarts the shell itself (un-elevated). Only step in if it didn't.
        Thread.Sleep(3000);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    /// <summary>
    /// Deletes temp files older than a day from the user and Windows temp folders, plus the
    /// Delivery Optimization cache. Never follows junctions/symlinks and skips anything in use.
    /// </summary>
    /// <returns>Bytes freed.</returns>
    public static long CleanTempFiles(Action<string> log)
    {
        var folders = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        };
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var cutoff = DateTime.UtcNow.AddDays(-1);
        long freed = 0;
        int files = 0;

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue;
                    long size = info.Length;
                    info.Delete();
                    freed += size;
                    files++;
                }
                catch { /* in use or protected: leave it */ }
            }

            foreach (var dir in Directory.EnumerateDirectories(folder, "*", options).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }

        var doCache = ProcessRunner.PowerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue", 120_000);
        log($"  ✓ deleted {files} temp file(s), {freed / 1024.0 / 1024.0:N0} MB" +
            (doCache.Success ? "; Delivery Optimization cache cleared" : ""));
        return freed;
    }

    /// <summary>Removes superseded Windows Update components in a visible console (can take a while).</summary>
    public static void StartComponentCleanup() =>
        Process.Start(new ProcessStartInfo(ProcessRunner.System32("cmd.exe"),
            "/k title FuWindows - Windows Update cleanup & DISM /Online /Cleanup-Image /StartComponentCleanup")
        { UseShellExecute = true });

    /// <summary>winget.exe (an App Installer alias in the user's WindowsApps folder), or null.</summary>
    public static string? FindWinget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(alias) ? alias : null;
    }
    public static void OpenInNotepad(string file) =>
        Process.Start(new ProcessStartInfo(ProcessRunner.System32("notepad.exe"), $"\"{file}\"") { UseShellExecute = true });

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}

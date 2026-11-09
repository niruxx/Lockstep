using Microsoft.Win32;

namespace Lockstep.Core;

/// <summary>
/// Lockstep was called FuWindows. This carries an existing install over once, at startup:
/// data folders, settings, the "Keep settings applied" task and the power plan name. Hosts
/// blocks and firewall rules with the old name are recognised in place (see Actions.cs).
/// </summary>
public static class Legacy
{
    /// <summary>The previous product name, as written into folders, tasks, hosts and firewall rules.</summary>
    public const string Name = "FuWindows";

    private const string OldTask = @"\" + Name + @"\Keep settings applied";

    public static void Migrate()
    {
        TryStep(() => MoveFolder(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Name),
            AppData.Folder));
        TryStep(() => MoveFolder(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lockstep")));
        TryStep(MoveSettings);
        TryStep(MoveTask);
        TryStep(RenamePowerPlan);
    }

    private static void TryStep(Action step)
    {
        try { step(); } catch { /* Never block startup on migration; the old data stays where it was. */ }
    }

    private static void MoveFolder(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        if (!Directory.Exists(to))
        {
            Directory.Move(from, to);
            return;
        }

        // Both exist: copy over anything the new folder doesn't have yet, then remove the old one.
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Directory.Delete(from, recursive: true);
    }

    private static void MoveSettings()
    {
        using var old = Registry.CurrentUser.OpenSubKey($@"Software\{Name}");
        if (old is null) return;
        using (var current = Registry.CurrentUser.CreateSubKey(@"Software\Lockstep"))
        {
            foreach (var value in old.GetValueNames())
                if (current.GetValue(value) is null)
                    current.SetValue(value, old.GetValue(value)!, old.GetValueKind(value));
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{Name}", throwOnMissingSubKey: false);
    }

    private static void MoveTask()
    {
        var schtasks = ProcessRunner.System32("schtasks.exe");
        if (!ProcessRunner.Run(schtasks, $"/Query /TN \"{OldTask}\"").Success) return;

        // Re-create under the new name, pointing at this (renamed) exe, with the same tracked tweaks.
        if (Environment.ProcessPath is { } exe) Enforcement.Enable(exe, Enforcement.LoadIds());
        ProcessRunner.Run(schtasks, $"/Delete /TN \"{OldTask}\" /F");
    }

    private static void RenamePowerPlan()
    {
        const string plan = "8f1a2c3d-4b5e-4f60-9a7b-1c2d3e4f5a6b";
        var powercfg = ProcessRunner.System32("powercfg.exe");
        if (ProcessRunner.Run(powercfg, "/list").Output.Contains($"{Name} Performance", StringComparison.OrdinalIgnoreCase))
            ProcessRunner.Run(powercfg, $"-changename {plan} \"Lockstep Performance\" \"Maximum performance. Created by Lockstep.\"");
    }
}

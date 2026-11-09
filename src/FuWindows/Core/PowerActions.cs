using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FuWindows.Core;

/// <summary>
/// A change made by running a command (powercfg and friends) rather than writing a value.
/// The delegates decide state, apply and revert; backups go through the same BackupStore.
/// </summary>
public sealed class CommandAction(
    string key,
    string description,
    Func<ActionState> getState,
    Action<TweakContext> apply,
    Action<TweakContext> revert) : ITweakAction
{
    public string Key { get; } = key;
    public string Describe() => description;
    public ActionState GetState() => getState();
    public void Apply(TweakContext ctx) => apply(ctx);
    public void Revert(TweakContext ctx) => revert(ctx);
    public bool IsOwnedBy(BackupStore backup) => backup.Contains(Key);
}

public static class PowerActions
{
    private static string Powercfg => ProcessRunner.System32("powercfg.exe");

    private static readonly Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>FuWindows' own copy of the Ultimate (or High) Performance plan.</summary>
    private const string FuWindowsPlan = "8f1a2c3d-4b5e-4f60-9a7b-1c2d3e4f5a6b";

    private static string? ActiveScheme()
    {
        var r = ProcessRunner.Run(Powercfg, "/getactivescheme");
        var m = GuidPattern.Match(r.Output);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static void Check(ProcessResult r, string what)
    {
        if (!r.Success) throw new InvalidOperationException($"{what}: {r.Message}");
    }

    /// <summary>Switches to a copy of the Ultimate Performance plan; revert restores the previous plan.</summary>
    public static CommandAction HighPerformancePlan() => new(
        "power:plan",
        "Power     activate \"FuWindows Performance\" (a copy of Ultimate Performance; High Performance if unavailable)",
        () => ActiveScheme() == FuWindowsPlan ? ActionState.Applied : ActionState.NotApplied,
        ctx =>
        {
            var previous = ActiveScheme() ?? Balanced;
            if (previous == FuWindowsPlan) return;
            ctx.Backup.RecordOnce("power:plan", () => new BackupEntry { Existed = true, Data = previous });

            if (!ProcessRunner.Run(Powercfg, "/list").Output.Contains(FuWindowsPlan, StringComparison.OrdinalIgnoreCase))
            {
                var dup = ProcessRunner.Run(Powercfg, $"-duplicatescheme {UltimatePerformance} {FuWindowsPlan}");
                if (!dup.Success) dup = ProcessRunner.Run(Powercfg, $"-duplicatescheme {HighPerformance} {FuWindowsPlan}");
                Check(dup, "Couldn't create the performance power plan");
                ProcessRunner.Run(Powercfg, $"-changename {FuWindowsPlan} \"FuWindows Performance\" \"Maximum performance. Created by FuWindows.\"");
            }
            Check(ProcessRunner.Run(Powercfg, $"/setactive {FuWindowsPlan}"), "Couldn't activate the power plan");
        },
        ctx =>
        {
            var restore = ctx.Backup.TryGet("power:plan", out var e) && e.Data is { } d ? d : Balanced;
            if (ActiveScheme() == FuWindowsPlan)
                Check(ProcessRunner.Run(Powercfg, $"/setactive {restore}"), "Couldn't restore the previous power plan");
            ProcessRunner.Run(Powercfg, $"/delete {FuWindowsPlan}");
            ctx.Backup.Remove("power:plan");
        });

    private static bool? HibernationEnabled()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return key?.GetValue("HibernateEnabled") is int v ? v != 0 : null;
    }

    /// <summary>Turns off hibernation (and with it Fast Startup), deleting hiberfil.sys.</summary>
    public static CommandAction DisableHibernation() => new(
        "power:hibernate",
        "Power     powercfg /hibernate off (removes hiberfil.sys, turns off Fast Startup)",
        () => HibernationEnabled() switch
        {
            null => ActionState.NotApplicable,
            true => ActionState.NotApplied,
            false => ActionState.Applied,
        },
        ctx =>
        {
            var enabled = HibernationEnabled();
            if (enabled is null) return;
            ctx.Backup.RecordOnce("power:hibernate", () => new BackupEntry { Existed = true, Data = enabled.Value ? "on" : "off" });
            Check(ProcessRunner.Run(Powercfg, "/hibernate off"), "Couldn't turn off hibernation");
        },
        ctx =>
        {
            bool wasOn = !ctx.Backup.TryGet("power:hibernate", out var e) || e.Data == "on";
            if (wasOn) Check(ProcessRunner.Run(Powercfg, "/hibernate on"), "Couldn't turn hibernation back on");
            ctx.Backup.Remove("power:hibernate");
        });
}

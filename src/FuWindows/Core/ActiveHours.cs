using Microsoft.Win32;

namespace FuWindows.Core;

/// <summary>
/// Windows Update active hours: the part of the day Windows won't restart to finish updates.
/// Set through the official policy (SetActiveHours / ActiveHoursStart / ActiveHoursEnd), which
/// also locks the Settings page so Windows' "automatically adjust" can't shrink it again.
/// The hours are user-chosen (Updates page); the default 08:00–02:00 is the widest window allowed.
/// </summary>
public static class ActiveHours
{
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string SettingsKey = @"Software\FuWindows";
    public const int MaxSpan = 18;
    public const int DefaultStart = 8;
    public const int DefaultEnd = 2;

    /// <summary>Hours in the window, start inclusive, end exclusive, wrapping past midnight.</summary>
    public static int Span(int start, int end) => (end - start + 24) % 24;

    public static bool IsValid(int start, int end) =>
        start is >= 0 and <= 23 && end is >= 0 and <= 23 && Span(start, end) is >= 1 and <= MaxSpan;

    public static (int Start, int End) Chosen
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            int start = key?.GetValue("ActiveHoursStart") as int? ?? DefaultStart;
            int end = key?.GetValue("ActiveHoursEnd") as int? ?? DefaultEnd;
            return IsValid(start, end) ? (start, end) : (DefaultStart, DefaultEnd);
        }
        set
        {
            if (!IsValid(value.Start, value.End))
                throw new ArgumentException($"Active hours must be 1 to {MaxSpan} hours long.");
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("ActiveHoursStart", value.Start, RegistryValueKind.DWord);
            key.SetValue("ActiveHoursEnd", value.End, RegistryValueKind.DWord);
        }
    }

    public static string Format((int Start, int End) h) =>
        $"{h.Start:00}:00–{h.End:00}:00 ({Span(h.Start, h.End)} hours)";

    private static RegistryAction[] Values((int Start, int End) h) =>
    [
        RegistryAction.Dword(RegistryHive.LocalMachine, Policy, "SetActiveHours", 1),
        RegistryAction.Dword(RegistryHive.LocalMachine, Policy, "ActiveHoursStart", h.Start),
        RegistryAction.Dword(RegistryHive.LocalMachine, Policy, "ActiveHoursEnd", h.End),
    ];

    /// <summary>The tweak action. Reads the chosen hours each time, so changing them takes effect on re-apply.</summary>
    public static CommandAction Action() => new(
        "activehours",
        $@"Registry  HKLM\{Policy}  →  SetActiveHours = 1, ActiveHoursStart / ActiveHoursEnd = your chosen hours (default {Format((DefaultStart, DefaultEnd))})",
        () => Values(Chosen).All(a => a.GetState() == ActionState.Applied) ? ActionState.Applied : ActionState.NotApplied,
        ctx =>
        {
            ctx.Backup.RecordOnce("activehours", () => new BackupEntry { Existed = true, Data = "applied" });
            foreach (var a in Values(Chosen)) a.Apply(ctx);
            ctx.Log($"  Active hours: {Format(Chosen)}");
        },
        ctx =>
        {
            // Revert restores the original values from backup, whatever hours were chosen since.
            foreach (var a in Values(Chosen)) a.Revert(ctx);
            ctx.Backup.Remove("activehours");
        });
}

using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Lockstep.Core;

public static class AppData
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lockstep");

    public static string File(string name)
    {
        Directory.CreateDirectory(Folder);
        return Path.Combine(Folder, name);
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// A shareable configuration: which tweaks to apply and which apps to remove. Used by
/// export/import in the UI and by <c>Lockstep.exe --config file.json</c>.
/// </summary>
public sealed class FuProfile
{
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Tweak IDs, e.g. "telemetry.level". See <c>--list</c>.</summary>
    public List<string> Tweaks { get; set; } = [];

    /// <summary>Package names to uninstall, e.g. "Microsoft.BingNews". A trailing * matches a prefix.</summary>
    public List<string> RemoveApps { get; set; } = [];

    public bool RemoveAppsForAllUsers { get; set; } = true;
    public bool DeprovisionApps { get; set; } = true;

    public static bool MatchesApp(string pattern, string packageName) =>
        pattern.EndsWith('*')
            ? packageName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : packageName.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    public static FuProfile Load(string path) =>
        JsonSerializer.Deserialize<FuProfile>(System.IO.File.ReadAllText(path), AppData.Json)
        ?? throw new InvalidDataException("The profile file is empty.");

    public void Save(string path) =>
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, AppData.Json));

}

/// <summary>
/// "Keep settings applied": a logon task that runs <c>Lockstep.exe --enforce</c>, which re-applies
/// any tracked tweak Windows (usually a feature update) has switched back.
/// </summary>
public static class Enforcement
{
    public const string TaskName = @"\Lockstep\Keep settings applied";

    private static string IdsPath => AppData.File("enforce.json");
    private static readonly object Gate = new();

    public static bool IsEnabled() =>
        ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), $"/Query /TN \"{TaskName}\"").Success;

    public static HashSet<string> LoadIds()
    {
        lock (Gate)
        {
            if (!System.IO.File.Exists(IdsPath)) return new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(IdsPath)) ?? [];
                return new(ids, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }
    }

    private static void SaveIds(IEnumerable<string> ids)
    {
        lock (Gate)
            System.IO.File.WriteAllText(IdsPath, JsonSerializer.Serialize(ids.Order().ToList(), AppData.Json));
    }

    public static void Enable(string exePath, IEnumerable<string> ids)
    {
        SaveIds(ids);

        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Lockstep</Author>
                <Description>Re-applies Lockstep privacy settings that Windows updates have reset.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT1M</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>--enforce</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), $"lockstep-task-{Guid.NewGuid():N}.xml");
        System.IO.File.WriteAllText(xmlPath, xml, Encoding.Unicode);
        try
        {
            var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"),
                $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (!r.Success) throw new InvalidOperationException(r.Message);
        }
        finally
        {
            System.IO.File.Delete(xmlPath);
        }
    }

    public static void Disable()
    {
        if (IsEnabled())
            ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), $"/Delete /TN \"{TaskName}\" /F");
    }

    /// <summary>Keeps the tracked list in sync with what the user applies and reverts.</summary>
    public static void Track(IEnumerable<string> applied, IEnumerable<string> reverted)
    {
        if (!System.IO.File.Exists(IdsPath)) return;
        var ids = LoadIds();
        ids.UnionWith(applied);
        ids.ExceptWith(reverted);
        SaveIds(ids);
    }

    /// <summary>Re-applies every tracked tweak that is no longer fully applied.</summary>
    /// <returns>Number of failed actions.</returns>
    public static int Run(Action<string> log)
    {
        var ids = LoadIds();
        var ctx = new TweakContext(BackupStore.Instance, log);
        int failures = 0, reapplied = 0;

        foreach (var tweak in TweakCatalog.All.Where(t => ids.Contains(t.Id)))
        {
            var status = tweak.GetStatus();
            if (status is TweakStatus.Applied or TweakStatus.NotApplicable) continue;
            if (tweak.GetBlockReason() is { } reason) { log($"Skipped: {tweak.Title}: {reason}"); continue; }

            log($"Re-applying: {tweak.Title} (was {status})");
            failures += tweak.Apply(ctx);
            reapplied++;
        }

        log(reapplied == 0
            ? $"All {ids.Count} tracked tweaks are still applied."
            : $"Re-applied {reapplied} tweak(s), {failures} error(s).");
        return failures;
    }
}

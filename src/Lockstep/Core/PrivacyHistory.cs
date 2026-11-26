using System.Text.Json;

namespace Lockstep.Core;

public static class PrivacyHistory
{
    private const int Limit = 200;
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lockstep");
    private static string HistoryPath => Path.Combine(Folder, "privacy-history.json");
    private static string ReportOnlyPath => Path.Combine(Folder, "privacy-report-only");

    public sealed record Change(DateTimeOffset DetectedUtc, string TweakId, TweakStatus Status, string Outcome);

    public static bool IsReportOnly => File.Exists(ReportOnlyPath);

    public static void SetReportOnly(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Folder);
            using var marker = File.Open(ReportOnlyPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        }
        else if (Directory.Exists(Folder)) File.Delete(ReportOnlyPath);
    }

    public static IReadOnlyList<Change> Read()
    {
        if (!File.Exists(HistoryPath)) return [];
        try
        {
            using var stream = File.Open(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<Change[]>(stream, AppData.Json) ?? [];
        }
        catch (FileNotFoundException) { return []; }
    }

    public static void Add(IReadOnlyList<Change> changes) => WithHistoryLock(() =>
    {
        Directory.CreateDirectory(Folder);
        var history = changes.Reverse().Concat(Read()).Take(Limit).ToArray();
        var temporary = Path.Combine(Folder, $"privacy-history-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(history, AppData.Json));
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    });

    public static void Clear() => WithHistoryLock(() =>
    {
        if (Directory.Exists(Folder)) File.Delete(HistoryPath);
    });

    private static void WithHistoryLock(Action action)
    {
        using var mutex = new Mutex(false, @"Global\Lockstep.PrivacyHistory." +
            System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Privacy history is busy.");
        }
        catch (AbandonedMutexException) { }

        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }
}

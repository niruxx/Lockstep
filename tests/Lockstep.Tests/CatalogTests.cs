using Lockstep.Core;
using Microsoft.Win32;

namespace Lockstep.Tests;

/// <summary>
/// Guards the tweak catalog: a typo here fails silently on users' PCs, so catch it in CI.
/// These tests only read the catalog; they never touch the registry or system.
/// </summary>
public class CatalogTests
{
    private static IReadOnlyList<Tweak> Tweaks => TweakCatalog.All;

    [Fact]
    public void Tweak_ids_are_unique()
    {
        var duplicates = Tweaks.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_tweak_has_title_description_actions_and_a_real_section()
    {
        var categories = TweakCatalog.Categories.Select(c => c.Id).ToHashSet();
        foreach (var t in Tweaks)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title), $"{t.Id}: no title");
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Id}: no description");
            Assert.NotEmpty(t.Actions);
            Assert.Contains(t.Category, categories);
            foreach (var extra in t.AlsoIn) Assert.Contains(extra, categories);
        }
    }

    [Fact]
    public void Every_section_has_at_least_one_tweak()
    {
        foreach (var c in TweakCatalog.Categories)
            Assert.True(Tweaks.Any(t => t.IsIn(c.Id)), $"Section {c.Id} is empty");
    }

    [Fact]
    public void Risky_tweaks_always_explain_what_breaks()
    {
        var missing = Tweaks.Where(t => t.Risk >= RiskLevel.Aggressive && string.IsNullOrWhiteSpace(t.Warning))
                            .Select(t => t.Id).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Registry_paths_are_well_formed()
    {
        foreach (var (tweak, action) in RegistryActions())
        {
            Assert.False(action.Path.StartsWith('\\') || action.Path.EndsWith('\\'), $"{tweak.Id}: path has a stray backslash: {action.Path}");
            Assert.DoesNotContain("\\\\", action.Path);
            Assert.False(action.Path.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase), $"{tweak.Id}: hive belongs in Hive, not the path");
            Assert.False(string.IsNullOrWhiteSpace(action.Name), $"{tweak.Id}: empty value name under {action.Path}");

            var roots = action.Hive == RegistryHive.LocalMachine
                ? new[] { "SOFTWARE\\", "SYSTEM\\" }
                : new[] { "Software\\", "Control Panel\\", "System\\" };
            Assert.True(roots.Any(r => action.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)),
                $"{tweak.Id}: unexpected root for {action.Hive}: {action.Path}");
        }
    }

    /// <summary>
    /// Two tweaks writing the same value fight each other: reverting one silently undoes the other.
    /// Shared tweaks belong in <c>AlsoIn</c> instead. The pairs below are deliberate alternatives
    /// (pick one or the other) and documented as such in their descriptions.
    /// </summary>
    [Fact]
    public void No_two_tweaks_write_the_same_registry_value()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // "Disable peer-to-peer update sharing" vs "Bypass Delivery Optimization" (GDID): 0 vs 99.
            @"LocalMachine\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode",
        };

        var conflicts = RegistryActions()
            .GroupBy(x => $@"{x.Action.Hive}\{x.Action.Path}\{x.Action.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Tweak.Id).Distinct().Count() > 1 && !allowed.Contains(g.Key))
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Tweak.Id).Distinct())}")
            .ToList();
        Assert.True(conflicts.Count == 0, "Conflicting tweaks:\n" + string.Join("\n", conflicts));
    }

    [Fact]
    public void Action_backup_keys_are_unique_across_tweaks()
    {
        var conflicts = Tweaks.SelectMany(t => t.Actions.Select(a => (t.Id, a.Key)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
            .Where(g => !g.Key.Contains("DODownloadMode", StringComparison.OrdinalIgnoreCase))
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Id).Distinct())}")
            .ToList();
        Assert.True(conflicts.Count == 0, "Shared backup keys:\n" + string.Join("\n", conflicts));
    }

    [Fact]
    public void Install_catalog_has_no_duplicates()
    {
        Assert.Equal(Installer.Catalog.Count, Installer.Catalog.Select(a => a.Name).Distinct().Count());
        Assert.Equal(Installer.Catalog.Count, Installer.Catalog.Select(a => a.WingetId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Installer.Catalog, a => Assert.Contains(a.WingetSource, new[] { "winget", "msstore" }));
    }

    [Fact]
    public void Optional_apps_have_store_ids_and_unique_packages()
    {
        Assert.All(OptionalApps.Catalog, a => Assert.Matches("^[0-9A-Z]{12}$", a.StoreId));
        Assert.Equal(OptionalApps.Catalog.Count, OptionalApps.Catalog.Select(a => a.PackageName).Distinct().Count());
    }

    private static IEnumerable<(Tweak Tweak, RegistryAction Action)> RegistryActions() =>
        Tweaks.SelectMany(t => t.Actions.OfType<RegistryAction>().Select(a => (t, a)));
}

public class ProfileAndScheduleTests
{
    [Theory]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNews", true)]
    [InlineData("microsoft.bingnews", "Microsoft.BingNews", true)]
    [InlineData("Microsoft.Bing*", "Microsoft.BingWeather", true)]
    [InlineData("king.com.*", "king.com.CandyCrushSaga", true)]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNewsExtra", false)]
    [InlineData("Microsoft.Bing*", "Microsoft.Paint", false)]
    public void Profile_app_patterns_match_as_documented(string pattern, string package, bool expected) =>
        Assert.Equal(expected, FuProfile.MatchesApp(pattern, package));

    [Fact]
    public void Profile_round_trips_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lockstep-test-{Guid.NewGuid():N}.json");
        try
        {
            new FuProfile { Name = "Test", Tweaks = ["telemetry.level"], RemoveApps = ["king.com.*"], DeprovisionApps = false }.Save(path);
            var loaded = FuProfile.Load(path);
            Assert.Equal("Test", loaded.Name);
            Assert.Equal(["telemetry.level"], loaded.Tweaks);
            Assert.Equal(["king.com.*"], loaded.RemoveApps);
            Assert.False(loaded.DeprovisionApps);
            Assert.Contains("\"tweaks\"", File.ReadAllText(path)); // camelCase on disk
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8, 2, 18, true)]    // the default: widest window Windows allows
    [InlineData(9, 17, 8, true)]
    [InlineData(22, 6, 8, true)]    // wraps past midnight
    [InlineData(8, 8, 0, false)]    // empty
    [InlineData(6, 1, 19, false)]   // longer than 18 hours
    public void Active_hours_span_and_limits(int start, int end, int span, bool valid)
    {
        Assert.Equal(span, ActiveHours.Span(start, end));
        Assert.Equal(valid, ActiveHours.IsValid(start, end));
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using FuWindows.Core;
using FuWindows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FuWindows;

public sealed partial class MainWindow : Window
{
    private const string AllCategory = "all";
    private const string ToolsCategory = "tools";
    private const string AppsCategory = "apps";
    private const string PermissionsCategory = "permissions";
    private const string InstallCategory = "install";
    private const string RepositoriesCategory = "repositories";

    private readonly List<TweakItem> _all;
    private readonly IProgress<string> _log;
    private string _category = AllCategory;
    private string _search = "";
    private bool _busy;

    public ObservableCollection<TweakItem> VisibleTweaks { get; } = [];
    public ObservableCollection<AppItem> VisibleApps { get; } = [];

    private List<AppItem> _apps = [];
    private bool _appsLoaded;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SizeWindowForDisplay();

        _log = new Progress<string>(AppendLog);
        _all = TweakCatalog.All.Select(t => new TweakItem(t)).ToList();
        foreach (var item in _all) item.PropertyChanged += Item_PropertyChanged;

        BuildNavigation();
        ElevationBar.IsOpen = !SystemTools.IsElevated();
        BackupPathText.Text = Path.GetDirectoryName(BackupStore.Instance.FilePath);
        CliUsageText.Text = Cli.Usage;
        BuildPanelsMenu();
        _ = InitEnforcementToggleAsync();
        if (Content is FrameworkElement root)
            root.Loaded += async (_, _) =>
            {
                if (App.ScreenshotFolder is { } folder) await CaptureScreenshotsAsync(folder);
                else if (!WelcomeShown) await ShowWelcomeAsync();
            };

        Nav.SelectedItem = Nav.MenuItems[0];
        UpdateSelectionText();
        UpdateAppSelectionText();
        _ = RefreshStatusAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// AppWindow sizes are physical pixels, so scale by the display's DPI (a fixed 1280×880 is
    /// only ~853×587 at 150 % and squeezes the toolbars). Also centres the window and sets a
    /// minimum size the layout fits in.
    /// </summary>
    private void SizeWindowForDisplay()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var area = Microsoft.UI.Windowing.DisplayArea
            .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;

        int width = Math.Min((int)(1280 * scale), area.Width);
        int height = Math.Min((int)(880 * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));

        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(1000 * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), area.Height);
        }
    }
    // -------------------------------------------------------------------------------------
    // Navigation & filtering
    // -------------------------------------------------------------------------------------

    private void BuildNavigation()
    {
        Nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "All tweaks",
            Tag = AllCategory,
            Icon = new FontIcon { Glyph = "" },
        });
        Nav.MenuItems.Add(new NavigationViewItemSeparator());

        foreach (var category in TweakCatalog.Categories)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = category.Title,
                Tag = category.Id,
                Icon = new FontIcon { Glyph = category.Glyph },
            });
        }

        Nav.MenuItems.Add(new NavigationViewItemSeparator());
        Nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "Apps",
            Tag = AppsCategory,
            Icon = new FontIcon { Glyph = "" },
        });
        Nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "App permissions",
            Tag = PermissionsCategory,
            Icon = new FontIcon { Glyph = "" },
        });
        Nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "Install",
            Tag = InstallCategory,
            Icon = new FontIcon { Glyph = "" },
        });
        Nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "Third-party repositories",
            Tag = RepositoriesCategory,
            Icon = new FontIcon { Glyph = "" },
        });
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        _category = tag;
        if (!string.IsNullOrEmpty(_search)) SearchBox.Text = "";
        ApplyFilter();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text.Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        bool apps = _category == AppsCategory;
        bool permissions = _category == PermissionsCategory;
        bool install = _category == InstallCategory;
        bool repos = _category == RepositoriesCategory;
        InstallPanel.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        RepositoriesPanel.Visibility = repos ? Visibility.Visible : Visibility.Collapsed;
        bool tools = _category == ToolsCategory && _search.Length == 0;
        AppsPanel.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        PermissionsPanel.Visibility = permissions ? Visibility.Visible : Visibility.Collapsed;
        DnsCard.Visibility = _category == "network" && _search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_category == "network" && _search.Length == 0) InitDnsCard();
        bool identity = _category == "identity" && _search.Length == 0;
        GdidCard.Visibility = identity ? Visibility.Visible : Visibility.Collapsed;
        if (identity) _ = RefreshGdidAsync();
        PerfCard.Visibility = _category == "performance" && _search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool updates = _category == "updates" && _search.Length == 0;
        ActiveHoursCard.Visibility = updates ? Visibility.Visible : Visibility.Collapsed;
        if (updates) InitActiveHoursCard();
        ToolsPanel.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        TweakList.Visibility = !apps && !tools && !permissions && !install && !repos ? Visibility.Visible : Visibility.Collapsed;
        // Set before any early return below, or the tweak toolbar stays on top of other pages.
        Toolbar.Visibility = TweakList.Visibility;

        if (repos)
        {
            HeaderTitle.Text = "Third-party repositories";
            HeaderSubtitle.Text = "Package managers installed on this PC and the repositories they install from: winget sources, Chocolatey sources, Scoop buckets and Homebrew taps. Remove ones you don't trust, add your own, and switch off their telemetry.";
            if (!_reposLoaded) _ = RefreshRepositoriesAsync();
            return;
        }

        if (install)
        {
            HeaderTitle.Text = "Install";
            InitInstallPage();
            return;
        }

        if (permissions)
        {
            HeaderTitle.Text = "App permissions";
            HeaderSubtitle.Text = "See which apps asked for your camera, microphone, location, contacts and more, when they last used it, and allow or deny each Store app individually.";
            InitPermissionsPage();
            return;
        }

        if (apps)
        {
            HeaderTitle.Text = "Apps";
            if (_appsLoaded) FilterApps();
            else _ = RunBusyAsync(LoadAppsAsync);
            return;
        }

        if (tools)
        {
            HeaderTitle.Text = "Tools & backup";
            HeaderSubtitle.Text = "Restore points, undo, and utilities. FuWindows records every original value before changing it, so anything here can be put back.";
            UpdateBackupSummary();
            return;
        }

        IEnumerable<TweakItem> items = _all;
        if (_search.Length > 0)
        {
            items = items.Where(i =>
                i.Title.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                i.Details.Contains(_search, StringComparison.OrdinalIgnoreCase));
            HeaderTitle.Text = $"Search: {_search}";
        }
        else if (_category != AllCategory)
        {
            items = items.Where(i => i.Tweak.IsIn(_category));
            HeaderTitle.Text = TweakCatalog.Categories.First(c => c.Id == _category).Title;
        }
        else
        {
            HeaderTitle.Text = "All tweaks";
        }

        VisibleTweaks.Clear();
        foreach (var item in items) VisibleTweaks.Add(item);
        UpdateHeaderSubtitle();
    }

    private void UpdateHeaderSubtitle()
    {
        if (_category is AppsCategory or PermissionsCategory or InstallCategory or RepositoriesCategory || (_category == ToolsCategory && _search.Length == 0)) return;

        string description = _search.Length > 0 || _category == AllCategory
            ? "Every privacy tweak FuWindows offers. Start with \"Apply recommended\": those tweaks don't break anything."
            : TweakCatalog.Categories.First(c => c.Id == _category).Description;

        int applied = VisibleTweaks.Count(i => i.Status == TweakStatus.Applied);
        HeaderSubtitle.Text = $"{description}\n{applied} of {VisibleTweaks.Count} applied.";
    }

    // -------------------------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------------------------

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TweakItem.IsSelected)) UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        int count = _all.Count(i => i.IsSelected);
        SelectionText.Text = count == 0 ? "Nothing selected" : $"{count} selected";
        ApplyButton.IsEnabled = RevertButton.IsEnabled = count > 0 && !_busy;
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((FrameworkElement)sender).Tag;
        if (mode == "none")
        {
            foreach (var item in _all) item.IsSelected = false;
            return;
        }

        foreach (var item in VisibleTweaks)
        {
            item.IsSelected = mode switch
            {
                "recommended" => item.Tweak.Risk == RiskLevel.Recommended,
                "limited" => item.Tweak.Risk <= RiskLevel.Limited,
                "aggressive" => item.Tweak.Risk <= RiskLevel.Aggressive,
                "notapplied" => item.Status is TweakStatus.NotApplied or TweakStatus.Partial,
                "applied" => item.Status is TweakStatus.Applied or TweakStatus.Partial,
                _ => true,
            };
        }
    }

    // -------------------------------------------------------------------------------------
    // Apply / revert
    // -------------------------------------------------------------------------------------

    private async void Apply_Click(object sender, RoutedEventArgs e) =>
        await ApplyTweaksAsync(_all.Where(i => i.IsSelected).ToList(), "Apply selected tweaks?", null);

    /// <summary>One-click presets: applies every not-yet-applied tweak up to the given risk level.</summary>
    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var maxRisk = (string)((FrameworkElement)sender).Tag == "limited" ? RiskLevel.Limited : RiskLevel.Recommended;
        if (_busy) return;
        string name = maxRisk == RiskLevel.Limited ? "Recommended + Limited" : "Recommended";

        // Make sure statuses are current so already-applied tweaks are skipped.
        if (_all.Any(i => i.Status == TweakStatus.Unknown)) await RunBusyAsync(RefreshStatusAsync);

        var pending = _all
            .Where(i => i.Tweak.Risk <= maxRisk && i.Status is TweakStatus.NotApplied or TweakStatus.Partial or TweakStatus.Unknown)
            .ToList();

        if (pending.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Success, $"{name} preset already applied",
                "Every tweak in this preset is already in place on this PC.");
            return;
        }

        var byCategory = pending
            .GroupBy(i => i.Tweak.Category)
            .Select(g => $"• {TweakCatalog.Categories.First(c => c.Id == g.Key).Title}: {g.Count()}");
        string summary =
            (maxRisk == RiskLevel.Recommended
                ? "These are the non-risky tweaks: nothing you use stops working.\n"
                : "Recommended tweaks plus Limited ones, which turn off features some people use (check the list in each category first).\n") +
            string.Join("\n", byCategory);

        await ApplyTweaksAsync(pending, $"Apply the {name} preset?", summary);
    }

    private async Task ApplyTweaksAsync(List<TweakItem> selected, string title, string? summary)
    {
        var blocked = selected.Select(i => (Item: i, Reason: i.Tweak.GetBlockReason()))
                              .Where(b => b.Reason is not null).ToList();
        if (blocked.Count > 0)
        {
            selected = selected.Except(blocked.Select(b => b.Item)).ToList();
            foreach (var b in blocked) AppendLog($"Skipped: {b.Item.Title}: {b.Reason}");
            ShowStatus(InfoBarSeverity.Warning,
                $"{blocked.Count} tweak(s) can't be applied on this PC right now",
                string.Join(" ", blocked.Select(b => $"{b.Item.Title}: {b.Reason}")));
        }
        if (selected.Count == 0) return;

        var risky = selected.Where(i => i.Tweak.Risk >= RiskLevel.Aggressive).ToList();
        bool extreme = risky.Any(i => i.Tweak.Risk == RiskLevel.Extreme);

        var restorePoint = new CheckBox { Content = "Create a system restore point first", IsChecked = true };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"FuWindows will apply {selected.Count} tweak(s). Original values are backed up so you can revert later.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (summary is not null)
            body.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap });
        var limited = selected.Where(i => i.Tweak.Risk == RiskLevel.Limited && i.Tweak.Warning is not null).ToList();
        if (limited.Count > 0 && risky.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Worth knowing:\n" + string.Join("\n", limited.Select(i => $"• {i.Title}: {i.Warning}")),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
        }
        if (risky.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "These selections can break things:\n" +
                       string.Join("\n", risky.Select(i => $"• [{i.RiskLabel}] {i.Title}\n   {i.Warning}")),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            });
        }
        body.Children.Add(restorePoint);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = extreme ? "Apply Extreme tweaks?" : title,
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = extreme ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool wantRestorePoint = restorePoint.IsChecked == true;
        await RunBusyAsync(async () =>
        {
            if (wantRestorePoint && !await CreateRestorePointAsync())
            {
                ShowStatus(InfoBarSeverity.Error, "Nothing was changed",
                    "The restore point could not be created (see the activity log). Untick the restore point option to apply anyway.");
                return;
            }

            int failures = await RunTweaksAsync(selected, apply: true);
            await RefreshStatusAsync();
            ShowStatus(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? $"Applied {selected.Count} tweak(s)" : $"Applied with {failures} error(s)",
                "Restart Windows (or at least sign out) so every change takes effect. Details are in the activity log.");
        });
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        var selected = _all.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Revert selected tweaks?",
            Content = $"{selected.Count} tweak(s) will be returned to the values they had before FuWindows changed them, or to the Windows default if FuWindows never changed them.",
            PrimaryButtonText = "Revert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            int failures = await RunTweaksAsync(selected, apply: false);
            await RefreshStatusAsync();
            ShowStatus(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? $"Reverted {selected.Count} tweak(s)" : $"Reverted with {failures} error(s)",
                "Restart Windows so every change takes effect.");
        });
    }

    private Task<int> RunTweaksAsync(IReadOnlyList<TweakItem> items, bool apply)
    {
        var log = _log;
        var ctx = new TweakContext(BackupStore.Instance, log.Report);
        var tweaks = items.Select(i => i.Tweak).ToList();

        return Task.Run(() =>
        {
            int failures = 0;
            foreach (var tweak in tweaks)
            {
                log.Report($"{(apply ? "Applying" : "Reverting")}: {tweak.Title}");
                int failed = apply ? tweak.Apply(ctx) : tweak.Revert(ctx);
                if (failed == 0) log.Report("  ✓ done");
                failures += failed;
            }

            var ids = tweaks.Select(t => t.Id).ToList();
            if (apply) Enforcement.Track(ids, []); else Enforcement.Track([], ids);
            return failures;
        });
    }

    private async Task RefreshStatusAsync()
    {
        foreach (var item in _all) item.Status = TweakStatus.Unknown;

        var tweaks = _all.Select(i => i.Tweak).ToList();
        var statuses = await Task.Run(() =>
            tweaks.AsParallel().AsOrdered().WithDegreeOfParallelism(4).Select(t => t.GetStatus()).ToList());

        for (int i = 0; i < _all.Count; i++) _all[i].Status = statuses[i];
        UpdateHeaderSubtitle();
        UpdateBackupSummary();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RunBusyAsync(RefreshStatusAsync);

    // -------------------------------------------------------------------------------------
    // Tools
    // -------------------------------------------------------------------------------------

    private async void Tool_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "restorepoint":
                await RunBusyAsync(async () =>
                {
                    bool ok = await CreateRestorePointAsync();
                    ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                        ok ? "Restore point created" : "Restore point failed", ok ? "" : "See the activity log for details.");
                });
                break;

            case "revertall":
                await RevertAllAsync();
                break;

            case "explorer":
                await RunBusyAsync(() => Task.Run(SystemTools.RestartExplorer));
                AppendLog("Explorer restarted.");
                break;

            case "hosts":
                SystemTools.OpenInNotepad(HostsFile.FilePath);
                break;

            case "flushdns":
                await Task.Run(HostsFile.FlushDns);
                AppendLog("DNS cache flushed.");
                break;

            case "export":
                await ExportProfileAsync();
                break;

            case "import":
                await ImportProfileAsync();
                break;

            case "resetwu":
                await RunBusyAsync(async () =>
                {
                    AppendLog("Resetting Windows Update…");
                    AppendLog(await Task.Run(RepairTools.ResetWindowsUpdate));
                    ShowStatus(InfoBarSeverity.Success, "Windows Update reset", "Check for updates again in Settings.");
                });
                break;

            case "resetnet":
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "Reset network?",
                    Content = "Winsock and TCP/IP are reset to defaults and DNS is flushed. Static IP addresses and some VPN adapters need to be set up again. Restart Windows afterwards.",
                    PrimaryButtonText = "Reset",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) break;
                await RunBusyAsync(async () =>
                {
                    AppendLog("Resetting network…");
                    AppendLog(await Task.Run(RepairTools.ResetNetwork));
                    ShowStatus(InfoBarSeverity.Success, "Network reset", "Restart Windows to finish.");
                });
                break;
            }

            case "sfc":
                RepairTools.RepairSystemFiles();
                AppendLog("Started DISM + SFC in a separate window.");
                break;

            case "winget":
                RepairTools.ReinstallWinget();
                break;

            case "features-check":
                await RunBusyAsync(CheckFeaturesAsync);
                break;

            case "features-apply":
                await ApplyFeaturesAsync();
                break;

            case "welcome":
                await ShowWelcomeAsync();
                break;

            case "backupfolder":
                SystemTools.OpenFolder(Path.GetDirectoryName(BackupStore.Instance.FilePath)!);
                break;
        }
    }

    // -------------------------------------------------------------------------------------
    // Microsoft Edge
    // -------------------------------------------------------------------------------------

    private void EdgeExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args) => _ = RefreshEdgeAsync();

    private async Task RefreshEdgeAsync()
    {
        EdgeRemoval.Status s;
        try { s = await Task.Run(EdgeRemoval.GetStatus); }
        catch (Exception ex)
        {
            EdgeStatusText.Text = $"Couldn't check: {ex.Message}";
            return;
        }

        string edge = s.EdgeInstalled ? $"Installed ({s.EdgeVersion})" : "Not installed";
        string webView = s.WebView2Version is null ? "WebView2 Runtime not installed" : $"WebView2 Runtime {s.WebView2Version}";
        string block = s.ReinstallBlocked ? " · reinstall blocked" : "";
        string others = s.OtherBrowsers.Count == 0 ? "no other browser found" : $"other browsers: {string.Join(", ", s.OtherBrowsers)}";
        EdgeStatusText.Text = $"{edge} · {webView}{block} · {others}";

        EdgeUninstallButton.IsEnabled = s.EdgeInstalled && s.OtherBrowsers.Count > 0 && !_busy;
        EdgeReinstallButton.IsEnabled = !s.EdgeInstalled && !_busy;
        ToolTipService.SetToolTip(EdgeUninstallButton,
            !s.EdgeInstalled ? "Edge isn't installed."
            : s.OtherBrowsers.Count == 0 ? "Install another browser first, so you're never left without one."
            : null);
    }

    private async void Edge_Click(object sender, RoutedEventArgs e)
    {
        if ((string)((FrameworkElement)sender).Tag == "reinstall")
        {
            await RunBusyAsync(async () =>
            {
                AppendLog("Reinstalling Microsoft Edge…");
                await Task.Run(() => EdgeRemoval.Reinstall(_log.Report));
                ShowStatus(InfoBarSeverity.Success, "Microsoft Edge reinstalled", "Details are in the activity log.");
            });
            await RefreshEdgeAsync();
            return;
        }

        var options = new EdgeRemoval.Options(
            KeepWebView2: EdgeKeepWebViewBox.IsChecked == true,
            DeleteBrowsingData: EdgeDeleteDataBox.IsChecked == true,
            BlockReinstall: EdgeBlockReinstallBox.IsChecked == true);
        bool restorePoint = EdgeRestorePointBox.IsChecked == true;

        var text = "Edge will be uninstalled with its own system-level uninstaller.\n\n" +
                   "• Set another browser as your default afterwards (Settings > Apps > Default apps).\n" +
                   "• Links that force Edge (microsoft-edge:, some Widgets, Search and Help links) won't open.\n" +
                   "• Large Windows feature updates can bring Edge back.\n" +
                   (options.DeleteBrowsingData
                       ? "• Your Edge favorites, passwords and history will be deleted.\n"
                       : "• Your Edge browsing data is kept, so a reinstall picks up where you left off.\n") +
                   (options.KeepWebView2
                       ? "• The WebView2 Runtime and Edge Update stay, so other apps keep working."
                       : "\n⚠ You chose to remove WebView2 too. Teams, new Outlook, Widgets, Office add-ins, many installers and some parts of Windows will stop working, and Windows may reinstall it.");

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = options.KeepWebView2 ? "Uninstall Microsoft Edge?" : "Uninstall Microsoft Edge AND WebView2?",
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, MaxHeight = 420 },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            if (restorePoint && !await CreateRestorePointAsync())
            {
                ShowStatus(InfoBarSeverity.Error, "Nothing was changed",
                    "The restore point could not be created. Untick the restore point option to continue anyway.");
                return;
            }

            AppendLog("Uninstalling Microsoft Edge…");
            await Task.Run(() => EdgeRemoval.Uninstall(options, _log.Report));
            ShowStatus(InfoBarSeverity.Success, "Microsoft Edge uninstalled",
                "Pick your default browser in Settings > Apps > Default apps. You can reinstall Edge from here at any time.");
        });
        await RefreshEdgeAsync();
    }
    // -------------------------------------------------------------------------------------
    // README screenshots (FuWindows.exe --screenshots <folder>)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Opens each page, waits for it to load, renders the window to PNG and exits. Pages that show
    /// personal data (Device identity, App permissions) are deliberately not captured.
    /// </summary>
    private async Task CaptureScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(1360 * Content.XamlRoot.RasterizationScale), (int)(860 * Content.XamlRoot.RasterizationScale)));

        // RenderTargetBitmap can't see the Mica backdrop, so give the capture a solid background.
        if (Content is Panel root) root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        ElevationBar.IsOpen = false;

        var shots = new (string Tag, string File, int WaitMs)[]
        {
            (AllCategory, "tweaks", 6000),
            ("privacy", "privacy", 2500),
            ("ai", "ai", 2500),
            ("performance", "performance", 2500),
            ("gaming", "gaming", 2500),
            ("updates", "windows-update", 2500),
            (AppsCategory, "apps", 15000),
            (InstallCategory, "install", 20000),
            (RepositoriesCategory, "repositories", 20000),
            (ToolsCategory, "tools", 2500),
        };

        foreach (var (tag, file, wait) in shots)
        {
            var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>()
                          .FirstOrDefault(i => i.Tag as string == tag);
            if (item is null) continue;
            Nav.SelectedItem = item;
            await Task.Delay(wait);
            StatusBar.IsOpen = false;
            await Task.Delay(300);
            await SavePngAsync(Content, Path.Combine(folder, file + ".png"));
        }
        Application.Current.Exit();
    }

    private static async Task SavePngAsync(UIElement element, string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96,
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();

        stream.Seek(0);
        using var file = File.Create(path);
        await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream).CopyToAsync(file);
    }

    // -------------------------------------------------------------------------------------
    // First-run welcome
    // -------------------------------------------------------------------------------------

    private const string SettingsKey = @"Software\FuWindows";

    private static bool WelcomeShown
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue("WelcomeShown") is int v && v == 1;
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("WelcomeShown", value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    private static TextBlock Para(string text, bool strong = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (strong) block.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        return block;
    }

    private async Task ShowWelcomeAsync()
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 560 };
        body.Children.Add(Para(
            "FuWindows puts you in control of what Windows shares and runs. It turns off telemetry, ads, Copilot and Recall data collection, " +
            "blocks tracking endpoints, removes bloatware, and tunes Windows for performance and gaming."));
        body.Children.Add(Para("How it works", strong: true));
        body.Children.Add(Para(
            "• Every tweak shows its risk level and, under \"What this changes\", the exact settings it touches.\n" +
            "• Before changing anything, FuWindows saves the original value, so each tweak can be reverted, individually or all at once.\n" +
            "• New here? Press \"Apply recommended\". Those tweaks don't break anything. Read the warning before applying Limited, Aggressive or Extreme tweaks."));
        body.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Title = "Back up before bigger changes",
            Message = "Before Aggressive or Extreme tweaks, uninstalling apps or Edge, or disabling services, create a restore point and back up your files. " +
                      "Some changes, such as blocking Microsoft services or removing apps, can't be fully undone by FuWindows alone.",
        });
        var showAgain = new CheckBox { Content = "Show this again next time" };
        body.Children.Add(showAgain);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Welcome to FuWindows",
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            PrimaryButtonText = "Create a restore point now",
            SecondaryButtonText = "Open Windows Backup",
            CloseButtonText = "Continue",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        WelcomeShown = showAgain.IsChecked != true;

        if (result == ContentDialogResult.Primary)
        {
            await RunBusyAsync(async () =>
            {
                bool ok = await CreateRestorePointAsync();
                ShowStatus(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                    ok ? "Restore point created" : "Restore point failed",
                    ok ? "You can roll back to it from Windows Recovery or System Restore." : "See the activity log for details.");
            });
        }
        else if (result == ContentDialogResult.Secondary)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:backup") { UseShellExecute = true });
        }
    }

    // -------------------------------------------------------------------------------------
    // Install (package managers + app catalog)
    // -------------------------------------------------------------------------------------

    public ObservableCollection<CatalogItem> VisibleCatalog { get; } = [];
    private List<CatalogItem>? _catalog;
    private bool _installChecked;

    private List<CatalogItem> Catalog
    {
        get
        {
            if (_catalog is null)
            {
                _catalog = Installer.Catalog.Select(a => new CatalogItem(a)).ToList();
                foreach (var item in _catalog)
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CatalogItem.IsSelected)) UpdateInstallSelection(); };
            }
            return _catalog;
        }
    }

    private void InitInstallPage()
    {
        if (InstallCategoryBox.Items.Count == 0)
        {
            InstallCategoryBox.Items.Add("All categories");
            foreach (var category in Installer.Catalog.Select(a => a.Category).Distinct()) InstallCategoryBox.Items.Add(category);
            InstallCategoryBox.SelectedIndex = 0;
        }
        FilterCatalog();
        if (!_installChecked) _ = RefreshInstallAsync();
    }

    private void InstallFilter_Changed(object sender, SelectionChangedEventArgs e) => FilterCatalog();

    private void FilterCatalog()
    {
        var category = InstallCategoryBox.SelectedIndex > 0 ? (string)InstallCategoryBox.SelectedItem : null;
        IEnumerable<CatalogItem> items = Catalog;
        if (category is not null) items = items.Where(i => i.Category == category);
        if (_search.Length > 0)
            items = items.Where(i => i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                                     i.Description.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                                     i.Ids.Contains(_search, StringComparison.OrdinalIgnoreCase));

        VisibleCatalog.Clear();
        foreach (var item in items) VisibleCatalog.Add(item);

        int installed = Catalog.Count(i => i.Installed == true);
        HeaderSubtitle.Text =
            "Install package managers, then install popular apps silently, with no bundled offers, using winget or Chocolatey.\n" +
            (_installChecked ? $"{installed} of {Catalog.Count} catalog apps are installed. " : "Checking what's installed… ") +
            $"Showing {VisibleCatalog.Count}.";
        UpdateInstallSelection();
    }

    private void UpdateInstallSelection()
    {
        if (InstallSelectionText is null) return;
        int count = Catalog.Count(i => i.IsSelected);
        InstallSelectionText.Text = count == 0 ? "Nothing selected" : $"{count} selected";
        InstallAppsButton.IsEnabled = count > 0 && !_busy;
    }

    private async void InstallRefresh_Click(object sender, RoutedEventArgs e) => await RefreshInstallAsync();

    private async Task RefreshInstallAsync()
    {
        InstallBusy.IsActive = true;
        try
        {
            if (!_reposLoaded) _ = RefreshRepositoriesAsync();
            var installed = await Task.Run(Installer.InstalledIds);
            foreach (var item in Catalog) item.Installed = installed.Contains(item.App.WingetId);
            _installChecked = true;
            FilterCatalog();
        }
        catch (Exception ex)
        {
            AppendLog($"✗ Couldn't check installed apps: {ex.Message}");
        }
        finally
        {
            InstallBusy.IsActive = _busy;
        }
    }

    private void StartManagerInstall(string id)
    {
        try
        {
            var what = Installer.InstallManager(id);
            AppendLog(what);
            ShowStatus(InfoBarSeverity.Informational, "Installer started", what + " Press Refresh when it has finished.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Couldn't start the installer", ex.Message);
        }
    }

    private async void InstallApps_Click(object sender, RoutedEventArgs e)
    {
        var selected = Catalog.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        var manager = (InstallManagerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "winget";

        if (manager == "choco" && Installer.ChocoPath() is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "Chocolatey isn't installed", "Install it from the Package managers card first, or choose winget.");
            return;
        }
        if (manager == "winget" && SystemTools.FindWinget() is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "winget isn't installed", "Install App Installer from the Package managers card first.");
            return;
        }

        var already = selected.Where(i => i.Installed == true).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Install {selected.Count} app(s)?",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"These will be installed silently with {(manager == "choco" ? "Chocolatey (apps without a Chocolatey package use winget)" : "winget")}:\n\n" +
                           string.Join("\n", selected.Select(i => $"• {i.Name}{(i.Installed == true ? "  (already installed: checks for an update)" : "")}")) +
                           "\n\nInstallers come from each publisher via the package manager's repository. Some take a few minutes; progress is in the activity log.",
                },
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            int failures = 0;
            foreach (var item in selected)
            {
                AppendLog($"Installing: {item.Name}");
                try
                {
                    await Task.Run(() => Installer.InstallApp(item.App, manager, _log.Report));
                    item.IsSelected = false;
                }
                catch (Exception ex)
                {
                    failures++;
                    AppendLog($"  ✗ {ex.Message}");
                }
            }
            ShowStatus(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? $"Installed {selected.Count} app(s)" : $"{selected.Count - failures} installed, {failures} failed",
                "Details are in the activity log.");
        });
        await RefreshInstallAsync();
    }

    // -------------------------------------------------------------------------------------
    // Third-party repositories
    // -------------------------------------------------------------------------------------

    private bool _reposLoaded;

    private void GoToRepositories_Click(object sender, RoutedEventArgs e) =>
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == RepositoriesCategory);

    private void RepoRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshRepositoriesAsync();

    private static Grid RepoCard()
    {
        var card = new Grid
        {
            Padding = new Thickness(16),
            RowSpacing = 10,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
        return card;
    }

    private static void AddRow(Grid card, UIElement element)
    {
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow((FrameworkElement)element, card.RowDefinitions.Count - 1);
        card.Children.Add(element);
    }

    private static Grid TwoColumns(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// A button whose <paramref name="prepare"/> runs on the UI thread (to read text boxes) and
    /// returns the work, which then runs in the background.
    /// </summary>
    private Button RepoButton(string text, Func<Func<string>> prepare)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        button.Click += async (_, _) =>
        {
            await RunBusyAsync(async () =>
            {
                var work = prepare();
                var message = await Task.Run(work);
                AppendLog(message);
            });
            await RefreshRepositoriesAsync();
        };
        return button;
    }

    private async Task RefreshRepositoriesAsync()
    {
        RepoBusy.IsActive = true;
        try
        {
            var managers = await Task.Run(Installer.GetManagers);
            var cards = new List<UIElement>();
            foreach (var m in managers) cards.Add(await BuildRepoCardAsync(m));

            RepoList.Children.Clear();
            foreach (var card in cards) RepoList.Children.Add(card);
            _reposLoaded = true;

            int installed = managers.Count(m => m.Version is not null);
            InstallManagersText.Text = $"Package managers installed: {string.Join(", ", managers.Where(m => m.Version is not null).Select(m => m.Name))}"
                                       .Replace("installed: ", installed == 0 ? "installed: none" : "installed: ");
        }
        catch (Exception ex)
        {
            AppendLog($"✗ Couldn't read repositories: {ex.Message}");
        }
        finally
        {
            RepoBusy.IsActive = false;
        }
    }

    private async Task<UIElement> BuildRepoCardAsync(Installer.ManagerStatus m)
    {
        var card = RepoCard();
        bool installed = m.Version is not null;

        // Header: name, version, description, install / upgrade.
        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(Para(installed ? $"{m.Name}  ·  {m.Version}" : $"{m.Name}  ·  not installed", strong: true));
        info.Children.Add(new TextBlock { Text = m.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        if (m.Note is not null) info.Children.Add(new TextBlock { Text = m.Note, TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        if (!installed)
        {
            var install = new Button
            {
                Content = m.Id == "brew" && m.Note?.StartsWith("WSL isn't") == true ? "Set up WSL" : "Install",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            install.Click += (_, _) => StartManagerInstall(m.Id);
            actions.Children.Add(install);
        }
        else
        {
            var upgrade = new Button { Content = "Upgrade everything" };
            upgrade.Click += (_, _) =>
            {
                try { Installer.UpgradeAll(m.Id); AppendLog($"Started {m.Name} upgrade in a separate window."); }
                catch (Exception ex) { AppendLog($"✗ {ex.Message}"); }
            };
            actions.Children.Add(upgrade);
        }
        AddRow(card, TwoColumns(info, actions));
        if (!installed) return card;

        // Telemetry switch, where the manager has one.
        if (m.Id is "winget" or "brew")
        {
            bool off = await Task.Run(() => m.Id == "winget" ? Repositories.WingetTelemetryDisabled() : Repositories.BrewAnalyticsOff());
            var toggle = new ToggleSwitch { IsOn = !off, OnContent = "On", OffContent = "Off", MinWidth = 0 };
            toggle.Toggled += async (_, _) =>
            {
                bool disable = !toggle.IsOn;
                try
                {
                    await Task.Run(() =>
                    {
                        if (m.Id == "winget") Repositories.SetWingetTelemetryDisabled(disable);
                        else Repositories.SetBrewAnalytics(!disable);
                    });
                    AppendLog($"{m.Name} {(m.Id == "winget" ? "telemetry" : "analytics")} turned {(disable ? "off" : "on")}");
                }
                catch (Exception ex) { AppendLog($"✗ {ex.Message}"); }
            };
            var label = new TextBlock
            {
                Text = m.Id == "winget"
                    ? "winget telemetry (switching it off sets telemetry.disable in winget's settings.json)"
                    : "Homebrew analytics (switching it off runs brew analytics off)",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AddRow(card, TwoColumns(label, toggle));
        }

        // Sources.
        List<Repositories.Source> sources;
        try
        {
            sources = await Task.Run(() => m.Id switch
            {
                "winget" => Repositories.WingetSources(),
                "choco" => Repositories.ChocoSources(),
                "scoop" => Repositories.ScoopBuckets(),
                _ => Repositories.BrewTaps(),
            });
        }
        catch (Exception ex)
        {
            AddRow(card, new TextBlock { Text = $"Couldn't list sources: {ex.Message}", TextWrapping = TextWrapping.Wrap });
            return card;
        }

        string noun = m.Id switch { "scoop" => "Buckets", "brew" => "Taps", _ => "Sources" };
        AddRow(card, Para(sources.Count == 0 ? $"{noun}: none" : $"{noun} ({sources.Count})", strong: true));

        foreach (var s in sources)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = s.Name + (s.IsDefault ? "  (default)" : "") + (s.Enabled ? "" : "  · disabled"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = s.Location, FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var name = s.Name;
            if (s.CanDisable)
                buttons.Children.Add(RepoButton(s.Enabled ? "Disable" : "Enable", () => () =>
                {
                    Repositories.ChocoSetEnabled(name, !s.Enabled);
                    return $"Chocolatey source {name} {(s.Enabled ? "disabled" : "enabled")}";
                }));
            buttons.Children.Add(RepoButton(m.Id == "brew" ? "Untap" : "Remove", () => () =>
            {
                switch (m.Id)
                {
                    case "winget": Repositories.WingetRemove(name); break;
                    case "choco": Repositories.ChocoRemove(name); break;
                    case "scoop": Repositories.ScoopRemove(name); break;
                    default: Repositories.BrewUntap(name); break;
                }
                return $"{m.Name}: removed {name}";
            }));
            AddRow(card, TwoColumns(text, buttons));
        }

        // Add new.
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        switch (m.Id)
        {
            case "winget":
            {
                foreach (var missing in Repositories.MissingWingetDefaults(sources))
                {
                    var n = missing;
                    add.Children.Add(RepoButton($"Add back {n}", () => () => { Repositories.WingetAdd(n, "", null); return $"winget: added {n}"; }));
                }
                var nameBox = new TextBox { PlaceholderText = "Name", Width = 120 };
                var urlBox = new TextBox { PlaceholderText = "Source URL", Width = 260 };
                var typeBox = new TextBox { PlaceholderText = "Type (optional, e.g. Microsoft.Rest)", Width = 220 };
                add.Children.Add(nameBox); add.Children.Add(urlBox); add.Children.Add(typeBox);
                add.Children.Add(RepoButton("Add", () =>
                {
                    string n = nameBox.Text.Trim(), url = urlBox.Text.Trim(), type = typeBox.Text.Trim();
                    return () => { Repositories.WingetAdd(n, url, type); return $"winget: added {n}"; };
                }));
                add.Children.Add(RepoButton("Reset to defaults", () => () => { Repositories.WingetReset(); return "winget: sources reset to defaults"; }));
                break;
            }
            case "choco":
            {
                var nameBox = new TextBox { PlaceholderText = "Name", Width = 140 };
                var urlBox = new TextBox { PlaceholderText = "Feed URL or folder", Width = 320 };
                add.Children.Add(nameBox); add.Children.Add(urlBox);
                add.Children.Add(RepoButton("Add source", () =>
                {
                    string n = nameBox.Text.Trim(), url = urlBox.Text.Trim();
                    return () => { Repositories.ChocoAdd(n, url); return $"Chocolatey: added {n}"; };
                }));
                break;
            }
            case "scoop":
            {
                var known = new ComboBox { MinWidth = 260, PlaceholderText = "Add a known bucket" };
                foreach (var (bucket, description) in Repositories.KnownBuckets.Where(k => !sources.Any(s => s.Name == k.Name)))
                    known.Items.Add(new ComboBoxItem { Content = $"{bucket}: {description}", Tag = bucket });
                add.Children.Add(known);
                add.Children.Add(RepoButton("Add", () =>
                {
                    var bucket = (known.SelectedItem as ComboBoxItem)?.Tag as string
                                 ?? throw new InvalidOperationException("Pick a bucket first.");
                    return () => { Repositories.ScoopAdd(bucket, null); return $"Scoop: added bucket {bucket}"; };
                }));
                var nameBox = new TextBox { PlaceholderText = "Custom name", Width = 120 };
                var urlBox = new TextBox { PlaceholderText = "Git URL", Width = 240 };
                add.Children.Add(nameBox); add.Children.Add(urlBox);
                add.Children.Add(RepoButton("Add custom", () =>
                {
                    string n = nameBox.Text.Trim(), url = urlBox.Text.Trim();
                    return () => { Repositories.ScoopAdd(n, url); return $"Scoop: added bucket {n}"; };
                }));
                break;
            }
            default:
            {
                var tapBox = new TextBox { PlaceholderText = "user/repo", Width = 220 };
                add.Children.Add(tapBox);
                add.Children.Add(RepoButton("Tap", () =>
                {
                    var tap = tapBox.Text.Trim();
                    return () => { Repositories.BrewTap(tap); return $"Homebrew: tapped {tap}"; };
                }));
                break;
            }
        }
        AddRow(card, new ScrollViewer
        {
            Content = add,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        });
        return card;
    }

    // -------------------------------------------------------------------------------------
    // App permissions
    // -------------------------------------------------------------------------------------

    public ObservableCollection<PermissionItem> VisiblePermissions { get; } = [];
    private bool _suppressPermSwitch;

    private void InitPermissionsPage()
    {
        if (PermCapabilityBox.Items.Count == 0)
        {
            foreach (var cap in AppPermissions.Capabilities) PermCapabilityBox.Items.Add(cap.Name);
            PermCapabilityBox.SelectedIndex = 0; // triggers a load
        }
        else
        {
            _ = LoadPermissionsAsync();
        }
    }

    private AppPermissions.Capability? SelectedCapability =>
        PermCapabilityBox.SelectedIndex >= 0 ? AppPermissions.Capabilities[PermCapabilityBox.SelectedIndex] : null;

    private void PermCapabilityBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = LoadPermissionsAsync();

    private void PermRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadPermissionsAsync();

    private async Task LoadPermissionsAsync()
    {
        if (SelectedCapability is not { } cap) return;
        AppPermissions.State state;
        try { state = await Task.Run(() => AppPermissions.Load(cap.Id)); }
        catch (Exception ex)
        {
            PermStatusText.Text = $"Couldn't read permissions: {ex.Message}";
            return;
        }

        _suppressPermSwitch = true;
        PermUserSwitchTitle.Text = $"Let apps use: {cap.Name}";
        PermUserSwitch.IsOn = state.UserSwitchOn;
        PermUserSwitch.IsEnabled = !state.BlockedForAllUsers;
        var desktop = state.Apps.Where(a => a.IsDesktopApp).ToList();
        PermDesktopSwitch.IsOn = desktop.Count == 0 || desktop.Any(a => a.Allowed);
        PermDesktopSwitch.IsEnabled = !state.BlockedForAllUsers;
        _suppressPermSwitch = false;

        ClearLocationLink.Visibility = cap.Id == "location" ? Visibility.Visible : Visibility.Collapsed;

        VisiblePermissions.Clear();
        foreach (var entry in state.Apps) VisiblePermissions.Add(new PermissionItem(cap.Id, entry, AppendLog));

        int inUse = state.Apps.Count(a => a.InUse);
        PermStatusText.Text = state.BlockedForAllUsers
            ? $"{cap.Name} is blocked for every app on this PC by a FuWindows tweak (or policy). Revert that tweak to manage apps one by one."
            : $"{state.Apps.Count} app(s) asked for {cap.Name.ToLowerInvariant()} access" +
              (inUse > 0 ? $", {inUse} using it right now." : ".");
    }

    private void PermUserSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPermSwitch || SelectedCapability is not { } cap) return;
        AppPermissions.SetUserSwitch(cap.Id, PermUserSwitch.IsOn);
        AppendLog($"{cap.Name}: apps {(PermUserSwitch.IsOn ? "allowed" : "blocked")} for your account");
    }

    private void PermDesktopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPermSwitch || SelectedCapability is not { } cap) return;
        AppPermissions.SetDesktopApps(cap.Id, PermDesktopSwitch.IsOn);
        AppendLog($"{cap.Name}: desktop apps {(PermDesktopSwitch.IsOn ? "allowed" : "blocked")}");
    }

    // -------------------------------------------------------------------------------------
    // DNS provider
    // -------------------------------------------------------------------------------------

    private void InitDnsCard()
    {
        if (DnsProviderBox.Items.Count == 0)
        {
            foreach (var provider in DnsTools.Providers) DnsProviderBox.Items.Add(provider.Name);
            DnsProviderBox.SelectedIndex = 0;
        }
        _ = RefreshDnsCurrentAsync();
    }

    private async Task RefreshDnsCurrentAsync()
    {
        DnsCurrentText.Text = "Checking current DNS servers…";
        DnsCurrentText.Text = "Now: " + (await Task.Run(DnsTools.Current)).Replace("\r\n", "; ").Replace("\n", "; ");
    }

    private void DnsProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DnsProviderBox.SelectedIndex < 0) return;
        var provider = DnsTools.Providers[DnsProviderBox.SelectedIndex];
        DnsDescriptionText.Text = provider.Description +
            (provider.Servers.Length > 0 ? $" ({string.Join(", ", provider.Servers.Take(2))}, plus IPv6)" : "") +
            " Applies to every connected adapter. Combine with \"Encrypt DNS lookups\" below.";
    }

    private async void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        if (DnsProviderBox.SelectedIndex < 0) return;
        var provider = DnsTools.Providers[DnsProviderBox.SelectedIndex];
        await RunBusyAsync(async () =>
        {
            AppendLog($"Setting DNS to {provider.Name}…");
            var output = await Task.Run(() => DnsTools.Apply(provider));
            AppendLog(output);
            ShowStatus(InfoBarSeverity.Success, $"DNS set to {provider.Name}", "Choose \"Automatic\" to go back to your router's DNS.");
        });
        await RefreshDnsCurrentAsync();
    }

    // -------------------------------------------------------------------------------------
    // OneDrive
    // -------------------------------------------------------------------------------------

    private void OneDriveExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args) => _ = RefreshOneDriveAsync();

    private async Task RefreshOneDriveAsync()
    {
        var path = await Task.Run(OneDriveRemoval.InstalledPath);
        OneDriveStatusText.Text = path is null ? "Not installed" : $"Installed ({path})";
        OneDriveUninstallButton.IsEnabled = path is not null && !_busy;
        OneDriveReinstallButton.IsEnabled = path is null && !_busy;
    }

    private async void OneDrive_Click(object sender, RoutedEventArgs e)
    {
        bool uninstall = (string)((FrameworkElement)sender).Tag == "uninstall";
        if (uninstall)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Uninstall OneDrive?",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "OneDrive stops syncing and is uninstalled. Files already on this PC stay in your OneDrive folder, " +
                           "but online-only files (cloud icon) won't be reachable here any more. They stay safe in your OneDrive account online.\n\n" +
                           "You can reinstall it from here at any time.",
                },
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await RunBusyAsync(async () =>
        {
            AppendLog(uninstall ? "Uninstalling OneDrive…" : "Reinstalling OneDrive…");
            await Task.Run(() => { if (uninstall) OneDriveRemoval.Uninstall(_log.Report); else OneDriveRemoval.Reinstall(_log.Report); });
            ShowStatus(InfoBarSeverity.Success, uninstall ? "OneDrive uninstalled" : "OneDrive reinstalled", "Details are in the activity log.");
        });
        await RefreshOneDriveAsync();
    }

    // -------------------------------------------------------------------------------------
    // Windows features & classic panels
    // -------------------------------------------------------------------------------------

    private Dictionary<string, bool> _featureStates = [];
    private readonly Dictionary<RepairTools.Feature, (CheckBox Box, bool Original)> _featureBoxes = [];

    private async Task CheckFeaturesAsync()
    {
        FeaturesText.Text = "Reading Windows features…";
        _featureStates = await Task.Run(RepairTools.FeatureStates);
        FeaturesList.Children.Clear();
        _featureBoxes.Clear();

        foreach (var feature in RepairTools.Features)
        {
            var available = feature.Names.Where(_featureStates.ContainsKey).ToList();
            bool on = available.Count > 0 && available.All(n => _featureStates[n]);
            var box = new CheckBox
            {
                IsChecked = on,
                IsEnabled = available.Count > 0,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{feature.Title}: {feature.Description}" + (available.Count == 0 ? " (not available on this edition)" : ""),
                },
            };
            box.Checked += (_, _) => FeaturesApplyButton.IsEnabled = FeaturesChanged();
            box.Unchecked += (_, _) => FeaturesApplyButton.IsEnabled = FeaturesChanged();
            _featureBoxes[feature] = (box, on);
            FeaturesList.Children.Add(box);
        }
        FeaturesText.Text = "Ticked = turned on. Change what you need, then press Apply changes. Some changes need a restart.";
        FeaturesApplyButton.IsEnabled = false;
    }

    private bool FeaturesChanged() => _featureBoxes.Values.Any(f => (f.Box.IsChecked == true) != f.Original);

    private async Task ApplyFeaturesAsync()
    {
        var changes = _featureBoxes.Where(f => (f.Value.Box.IsChecked == true) != f.Value.Original)
                                   .Select(f => (Feature: f.Key, Enable: f.Value.Box.IsChecked == true)).ToList();
        if (changes.Count == 0) return;

        await RunBusyAsync(async () =>
        {
            bool restart = false;
            foreach (var (feature, enable) in changes)
            {
                AppendLog($"{(enable ? "Turning on" : "Turning off")}: {feature.Title}…");
                restart |= await Task.Run(() => RepairTools.SetFeature(feature, enable, _featureStates, _log.Report));
            }
            ShowStatus(InfoBarSeverity.Success, $"Changed {changes.Count} Windows feature(s)",
                restart ? "Restart Windows to finish." : "Done.");
        });
        await CheckFeaturesAsync();
    }

    private void BuildPanelsMenu()
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var (title, file, args) in RepairTools.Panels)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += (_, _) =>
            {
                try { RepairTools.OpenPanel(file, args); }
                catch (Exception ex) { AppendLog($"✗ {title}: {ex.Message}"); }
            };
            flyout.Items.Add(item);
        }
        PanelsButton.Flyout = flyout;
    }

    // -------------------------------------------------------------------------------------
    // Active hours
    // -------------------------------------------------------------------------------------

    private bool _activeHoursReady;

    private void InitActiveHoursCard()
    {
        if (!_activeHoursReady)
        {
            for (int h = 0; h < 24; h++)
            {
                ActiveStartBox.Items.Add($"{h:00}:00");
                ActiveEndBox.Items.Add($"{h:00}:00");
            }
            _activeHoursReady = true;
        }

        var (start, end) = ActiveHours.Chosen;
        ActiveStartBox.SelectedIndex = start;
        ActiveEndBox.SelectedIndex = end;
        UpdateActiveHoursText();
    }

    private void ActiveHours_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_activeHoursReady) UpdateActiveHoursText();
    }

    private void UpdateActiveHoursText()
    {
        int start = ActiveStartBox.SelectedIndex, end = ActiveEndBox.SelectedIndex;
        if (start < 0 || end < 0) return;

        bool valid = ActiveHours.IsValid(start, end);
        bool applied = _all.FirstOrDefault(i => i.Tweak.Id == "updates.activehours")?.Status == TweakStatus.Applied;
        int span = ActiveHours.Span(start, end);
        ActiveHoursText.Text = valid
            ? $"Windows won't restart for updates between {start:00}:00 and {end:00}:00 ({span} hours). " +
              (applied ? "Active-hours tweak is applied: Save updates it straight away."
                       : "Save the hours, then apply \"Set active hours\" below.")
            : $"Choose a window of 1 to {ActiveHours.MaxSpan} hours (Windows' limit). The current choice is {(span == 0 ? 24 : span)} hours.";
        ActiveHoursSaveButton.IsEnabled = valid && !_busy;
    }

    private async void ActiveHoursSave_Click(object sender, RoutedEventArgs e)
    {
        int start = ActiveStartBox.SelectedIndex, end = ActiveEndBox.SelectedIndex;
        if (!ActiveHours.IsValid(start, end)) return;
        ActiveHours.Chosen = (start, end);
        AppendLog($"Active hours set to {ActiveHours.Format((start, end))}");

        var item = _all.FirstOrDefault(i => i.Tweak.Id == "updates.activehours");
        // Already applied by FuWindows (it has a backup): write the new hours now.
        if (item is not null && item.Tweak.IsOwnedBy(BackupStore.Instance))
        {
            await RunBusyAsync(async () =>
            {
                await RunTweaksAsync([item], apply: true);
                await RefreshStatusAsync();
            });
            ShowStatus(InfoBarSeverity.Success, "Active hours updated", ActiveHours.Format((start, end)));
        }
        else
        {
            ShowStatus(InfoBarSeverity.Informational, "Active hours saved",
                $"{ActiveHours.Format((start, end))}. Apply \"Set active hours\" below to turn them on.");
        }
        UpdateActiveHoursText();
    }

    // -------------------------------------------------------------------------------------
    // Performance clean-up
    // -------------------------------------------------------------------------------------

    private async void Perf_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "cleantemp":
                await RunBusyAsync(async () =>
                {
                    AppendLog("Cleaning temporary files…");
                    long freed = await Task.Run(() => SystemTools.CleanTempFiles(_log.Report));
                    ShowStatus(InfoBarSeverity.Success, $"Freed {freed / 1024.0 / 1024.0:N0} MB",
                        "Temp files older than a day were deleted. Files in use were skipped.");
                });
                break;

            case "componentcleanup":
                SystemTools.StartComponentCleanup();
                AppendLog("Started Windows Update clean-up (DISM) in a separate window.");
                break;
        }
    }

    // -------------------------------------------------------------------------------------
    // Device identity (GDID)
    // -------------------------------------------------------------------------------------

    private async Task RefreshGdidAsync()
    {
        DeviceIdentity.Status s;
        try { s = await Task.Run(DeviceIdentity.GetStatus); }
        catch (Exception ex)
        {
            GdidValueText.Text = $"Couldn't read: {ex.Message}";
            return;
        }

        GdidValueText.Text = s.Gdid is null ? "none stored for this account" : DeviceIdentity.Mask(s.Gdid);
        GdidAccountText.Text = s.SignedInWithMicrosoftAccount
            ? "Microsoft account: the GDID is tied to your identity"
            : "Local account";
        GdidAppAccountsText.Text = s.MicrosoftAccountsInApps == 0 ? "none" : s.MicrosoftAccountsInApps.ToString();
        GdidWlidText.Text = s.AccountService;
        GdidCdpText.Text = s.ConnectedDevicesPlatform;
        GdidDoText.Text = s.DeliveryOptimization;
        GdidTelemetryText.Text = s.TelemetryService;
        GdidRestoreButton.IsEnabled = s.HasBackup;
    }

    private async void Gdid_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "gdid-refresh":
                await RefreshGdidAsync();
                break;

            case "gdid-reset":
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "Reset local device identity?",
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "FuWindows backs up your identity cache, then clears the stored GDID and device tickets so Windows requests a new device identity.\n\n" +
                               "Be aware:\n" +
                               "• This changes the ID stored on this PC. Microsoft may still link the new one to the old one through your hardware, Microsoft account and activation history. It is not a way to become anonymous.\n" +
                               "• Store, Xbox, Office and other apps signed in with a Microsoft account may ask you to sign in again.\n" +
                               "• Combine it with the tweaks below (and a local account) to stop the new ID being reported.\n\n" +
                               "You can undo this with \"Restore previous identity\".",
                    },
                    PrimaryButtonText = "Reset",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                await RunBusyAsync(async () =>
                {
                    AppendLog("Resetting local device identity…");
                    await Task.Run(() => DeviceIdentity.ResetLocal(_log.Report));
                    await RefreshGdidAsync();
                    ShowStatus(InfoBarSeverity.Success, "Local device identity reset",
                        "Restart Windows. A new GDID is requested the next time a Microsoft service needs one.");
                });
                break;
            }

            case "gdid-restore":
                await RunBusyAsync(async () =>
                {
                    AppendLog("Restoring device identity…");
                    await Task.Run(() => DeviceIdentity.RestoreLatest(_log.Report));
                    await RefreshGdidAsync();
                    ShowStatus(InfoBarSeverity.Success, "Previous device identity restored", "Restart Windows so every service picks it up.");
                });
                break;
        }
    }

    // -------------------------------------------------------------------------------------
    // Profiles & enforcement
    // -------------------------------------------------------------------------------------

    private async Task ExportProfileAsync()
    {
        var chosen = _all.Where(i => i.IsSelected).ToList();
        string source = "ticked";
        if (chosen.Count == 0)
        {
            chosen = _all.Where(i => i.Status is TweakStatus.Applied or TweakStatus.Partial).ToList();
            source = "currently applied";
        }
        if (chosen.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing to export", "Tick some tweaks, or apply some, then export again.");
            return;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = $"FuWindows-{Environment.MachineName}",
            DefaultFileExtension = ".json",
        };
        picker.FileTypeChoices.Add("FuWindows profile", [".json"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null || string.IsNullOrEmpty(result.Path)) return;

        try
        {
            var apps = _apps.Where(a => a.IsSelected && a.Info.CanUninstall).Select(a => a.Info.Name).Distinct().ToList();
            new FuProfile
            {
                Name = Path.GetFileNameWithoutExtension(result.Path),
                Tweaks = chosen.Select(i => i.Tweak.Id).ToList(),
                RemoveApps = apps,
                RemoveAppsForAllUsers = AllUsersBox.IsChecked == true,
                DeprovisionApps = DeprovisionBox.IsChecked == true,
            }.Save(result.Path);
            AppendLog($"Exported {chosen.Count} {source} tweak(s) and {apps.Count} app(s) to remove to {result.Path}");
            ShowStatus(InfoBarSeverity.Success, $"Exported {chosen.Count} {source} tweak(s), {apps.Count} app(s)", result.Path);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Export failed", ex.Message);
        }
    }

    private async Task ImportProfileAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(".json");
        var result = await picker.PickSingleFileAsync();
        if (result is null || string.IsNullOrEmpty(result.Path)) return;

        FuProfile profile;
        try { profile = FuProfile.Load(result.Path); }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Not a FuWindows profile", ex.Message);
            return;
        }

        var wanted = new HashSet<string>(profile.Tweaks, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all) item.IsSelected = wanted.Contains(item.Tweak.Id);
        int matched = _all.Count(i => i.IsSelected);
        var unknown = wanted.Except(_all.Select(i => i.Tweak.Id), StringComparer.OrdinalIgnoreCase).ToList();

        int appsTicked = 0;
        if (profile.RemoveApps.Count > 0)
        {
            if (!_appsLoaded) await RunBusyAsync(LoadAppsAsync);
            foreach (var app in _apps)
                app.IsSelected = app.Info.CanUninstall && profile.RemoveApps.Any(pattern => FuProfile.MatchesApp(pattern, app.Info.Name));
            appsTicked = _apps.Count(a => a.IsSelected);
            AllUsersBox.IsChecked = profile.RemoveAppsForAllUsers;
            DeprovisionBox.IsChecked = profile.DeprovisionApps;
            AppendLog($"Imported: {appsTicked} installed app(s) ticked for removal on the Apps page.");
        }

        Nav.SelectedItem = Nav.MenuItems[0];
        AppendLog($"Imported {result.Path}: {matched} tweak(s) ticked" +
                  (unknown.Count > 0 ? $", unknown IDs skipped: {string.Join(", ", unknown)}" : ""));
        ShowStatus(InfoBarSeverity.Informational,
            $"Imported {matched} tweak(s) from \"{profile.Name ?? Path.GetFileName(result.Path)}\"",
            "They're ticked below. Review them, then press \"Apply selected\"." +
            (appsTicked > 0 ? $" {appsTicked} app(s) are ticked on the Apps page; press Uninstall selected there." : "") +
            (unknown.Count > 0 ? $" {unknown.Count} unknown tweak(s) were skipped (made with a newer FuWindows?)." : ""));
    }

    private bool _suppressEnforceToggle;

    private async Task InitEnforcementToggleAsync()
    {
        bool enabled = await Task.Run(Enforcement.IsEnabled);
        _suppressEnforceToggle = true;
        EnforceToggle.IsOn = enabled;
        _suppressEnforceToggle = false;
    }

    private async void EnforceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEnforceToggle) return;
        bool on = EnforceToggle.IsOn;

        try
        {
            if (on)
            {
                var ids = _all.Where(i => i.Status is TweakStatus.Applied or TweakStatus.Partial)
                              .Select(i => i.Tweak.Id).ToList();
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find FuWindows.exe");
                await Task.Run(() => Enforcement.Enable(exe, ids));
                AppendLog($"Keep settings applied: on ({ids.Count} tweaks tracked, task {Enforcement.TaskName})");
                ShowStatus(InfoBarSeverity.Success, "Keep settings applied is on",
                    $"{ids.Count} applied tweak(s) will be checked at every sign-in. Tweaks you apply or revert later are tracked automatically. " +
                    "If you move the FuWindows folder, turn this off and on again.");
            }
            else
            {
                await Task.Run(Enforcement.Disable);
                AppendLog("Keep settings applied: off");
                ShowStatus(InfoBarSeverity.Informational, "Keep settings applied is off", "The sign-in task was removed.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"✗ Keep settings applied: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Couldn't change Keep settings applied", ex.Message);
            _suppressEnforceToggle = true;
            EnforceToggle.IsOn = !on;
            _suppressEnforceToggle = false;
        }
    }

    private async Task RevertAllAsync()
    {
        var owned = await Task.Run(() => _all.Where(i => i.Tweak.IsOwnedBy(BackupStore.Instance)).ToList());
        if (owned.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing to revert", "FuWindows hasn't changed anything on this PC.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Revert everything?",
            Content = $"{owned.Count} tweak(s) changed by FuWindows will be restored to their original values.",
            PrimaryButtonText = "Revert all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            int failures = await RunTweaksAsync(owned, apply: false);
            await RefreshStatusAsync();
            ShowStatus(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? "Everything reverted" : $"Reverted with {failures} error(s)",
                "Restart Windows so every change takes effect.");
        });
    }

    private async Task<bool> CreateRestorePointAsync()
    {
        AppendLog("Creating system restore point…");
        var result = await Task.Run(SystemTools.CreateRestorePoint);
        AppendLog(result.Success ? "  ✓ restore point created" : $"  ✗ restore point failed: {result.Message}");
        return result.Success;
    }

    private void UpdateBackupSummary()
    {
        int count = BackupStore.Instance.Count;
        BackupSummary.Text = count == 0
            ? "No backups recorded yet. Hosts-file and firewall changes are also undone by this button."
            : $"{count} original value(s) are backed up. Restores all of them, removes FuWindows hosts-file blocks and deletes FuWindows firewall rules.";
    }

    // -------------------------------------------------------------------------------------
    // Apps
    // -------------------------------------------------------------------------------------

    private async Task LoadAppsAsync()
    {
        AppsSelectionText.Text = "Loading installed apps…";
        var infos = await Task.Run(AppPackages.Load);

        foreach (var old in _apps) old.PropertyChanged -= AppItem_PropertyChanged;
        _apps = infos.Select(i => new AppItem(i)).ToList();
        foreach (var app in _apps) app.PropertyChanged += AppItem_PropertyChanged;

        _removedFamilies = new(RemovedApps.Load().Select(r => r.FamilyName), StringComparer.OrdinalIgnoreCase);
        _appsLoaded = true;
        FilterApps();
        UpdateAppSelectionText();
    }

    private void FilterApps()
    {
        if (!_appsLoaded) return;

        var mode = (AppFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "removable";
        bool installed(AppItem a) => a.Info.State == AppInstallState.Installed;
        IEnumerable<AppItem> items = _apps.Where(a => mode switch
        {
            "suggested" => installed(a) && a.Info.Group == AppGroup.Suggested,
            "microsoft" => installed(a) && a.Info.Group == AppGroup.Microsoft,
            "thirdparty" => installed(a) && a.Info.Group == AppGroup.ThirdParty,
            "system" => installed(a) && a.Info.Group == AppGroup.System,
            "hidden" => a.Info.State is AppInstallState.OtherUsersOnly or AppInstallState.ProvisionedOnly,
            "removed" => a.Info.State == AppInstallState.Removed || IsRemovedByUs(a),
            "available" => a.Info.State == AppInstallState.Available,
            _ => installed(a) && a.Info.Group != AppGroup.System,
        });

        if (_search.Length > 0)
        {
            items = items.Where(a =>
                a.Info.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                a.Info.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                a.Info.Publisher.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        VisibleApps.Clear();
        foreach (var app in items) VisibleApps.Add(app);

        int installedCount = _apps.Count(a => a.Info.State == AppInstallState.Installed && !a.Info.IsProtected);
        int suggested = _apps.Count(a => a.Info.State == AppInstallState.Installed && a.Info.Group == AppGroup.Suggested);
        int hidden = _apps.Count(a => a.Info.State is AppInstallState.OtherUsersOnly or AppInstallState.ProvisionedOnly);
        int removed = _apps.Count(a => a.Info.State == AppInstallState.Removed || IsRemovedByUs(a));
        int available = _apps.Count(a => a.Info.State == AppInstallState.Available);
        HeaderSubtitle.Text =
            "Uninstall Store and built-in apps, find hidden ones, and reinstall anything FuWindows removed.\n" +
            $"{installedCount} removable installed ({suggested} suggested to remove) · {hidden} hidden · {removed} removed by FuWindows · {available} optional to install. Showing {VisibleApps.Count}.";
        UndoAppsLink.Visibility = removed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private HashSet<string> _removedFamilies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps FuWindows removed for you that are still staged (hidden) count as removed too.</summary>
    private bool IsRemovedByUs(AppItem a) =>
        a.Info.State != AppInstallState.Installed && _removedFamilies.Contains(a.Info.FamilyName);

    private void AppFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilterApps();

    private void AppItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItem.IsSelected)) UpdateAppSelectionText();
    }

    private void UpdateAppSelectionText()
    {
        if (AppsSelectionText is null) return;
        var selected = _apps.Where(a => a.IsSelected).ToList();
        if (_appsLoaded) AppsSelectionText.Text = selected.Count == 0 ? "Nothing selected" : $"{selected.Count} selected";
        UninstallButton.IsEnabled = !_busy && selected.Any(a => a.Info.CanUninstall);
        InstallButton.IsEnabled = !_busy && selected.Any(a => a.Info.CanInstall);
    }

    private void AppSelect_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "suggested":
                foreach (var app in VisibleApps)
                    app.IsSelected = app.Info.Group == AppGroup.Suggested && app.Info.CanUninstall;
                break;
            case "all":
                foreach (var app in VisibleApps) app.IsSelected = true;
                break;
            case "removed":
                AppFilterBox.SelectedIndex = AppFilterIndex("removed");
                foreach (var app in _apps) app.IsSelected = app.Info.State == AppInstallState.Removed || IsRemovedByUs(app);
                break;
            default:
                foreach (var app in _apps) app.IsSelected = false;
                break;
        }
    }

    private int AppFilterIndex(string tag) =>
        AppFilterBox.Items.OfType<ComboBoxItem>().Select((item, i) => (item, i))
            .FirstOrDefault(x => (string)x.item.Tag == tag).i;

    private async void AppsRefresh_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(LoadAppsAsync);

    private static string ListApps(IReadOnlyList<AppItem> apps, bool withNotes)
    {
        const int maxListed = 30;
        var list = string.Join("\n", apps.Take(maxListed).Select(a =>
            withNotes && a.Info.Note is not null ? $"• {a.DisplayName}  ({a.Info.Note})" : $"• {a.DisplayName}"));
        if (apps.Count > maxListed) list += $"\n…and {apps.Count - maxListed} more";
        return list;
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = _apps.Where(a => a.IsSelected && a.Info.CanUninstall).ToList();
        if (selected.Count == 0) return;

        bool allUsers = AllUsersBox.IsChecked == true;
        bool deprovision = DeprovisionBox.IsChecked == true;

        var restorePoint = new CheckBox { Content = "Create a system restore point first", IsChecked = true };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"{selected.Count} app(s) will be uninstalled {(allUsers ? "for every user on this PC" : "for your account")}" +
                   (deprovision ? " and won't be reinstalled for new users." : ".") +
                   " FuWindows remembers them, so you can reinstall them later from \"Previously removed\".",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock { Text = ListApps(selected, withNotes: true), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(restorePoint);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Uninstall selected apps?",
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool wantRestorePoint = restorePoint.IsChecked == true;

        await RunBusyAsync(async () =>
        {
            if (wantRestorePoint && !await CreateRestorePointAsync())
            {
                ShowStatus(InfoBarSeverity.Error, "Nothing was uninstalled",
                    "The restore point could not be created (see the activity log). Untick the restore point option to continue anyway.");
                return;
            }

            int failures = 0;
            foreach (var app in selected)
            {
                AppendLog($"Uninstalling: {app.DisplayName} ({app.Info.FullName})");
                try
                {
                    await AppPackages.UninstallAsync(app.Info, allUsers, deprovision, AppendLog);
                    AppendLog("  ✓ removed");
                }
                catch (Exception ex)
                {
                    failures++;
                    AppendLog($"  ✗ {ex.Message}");
                }
            }

            await LoadAppsAsync();
            ShowStatus(failures == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failures == 0 ? $"Uninstalled {selected.Count} app(s)" : $"{selected.Count - failures} removed, {failures} failed",
                "Reinstall any of them from \"Previously removed\". Details are in the activity log.");
        });
    }

    private async void Install_Click(object sender, RoutedEventArgs e) =>
        await InstallAppsAsync(_apps.Where(a => a.IsSelected && a.Info.CanInstall).ToList(), "Install selected apps?");

    private async void UndoApps_Click(object sender, RoutedEventArgs e) =>
        await InstallAppsAsync(
            _apps.Where(a => a.Info.State == AppInstallState.Removed || IsRemovedByUs(a)).ToList(),
            "Reinstall everything FuWindows removed?");

    private async Task InstallAppsAsync(List<AppItem> apps, string title)
    {
        if (apps.Count == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{apps.Count} app(s) will be installed for your account:\n\n{ListApps(apps, withNotes: false)}\n\n" +
                           "FuWindows first re-registers the copy Windows still has on disk; otherwise it downloads the app from the Microsoft Store with winget. " +
                           "If neither works, the app's Store page opens so you can press Get.",
                },
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBusyAsync(async () =>
        {
            int installed = 0, store = 0, failures = 0;
            foreach (var app in apps)
            {
                AppendLog($"Installing: {app.DisplayName} ({app.Info.FamilyName})");
                try
                {
                    if (await AppPackages.InstallAsync(app.Info, AppendLog)) installed++; else store++;
                }
                catch (Exception ex)
                {
                    failures++;
                    AppendLog($"  ✗ {ex.Message}");
                }
            }

            await LoadAppsAsync();
            ShowStatus(failures == 0 && store == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                $"{installed} app(s) installed",
                (store > 0 ? $"{store} Store page(s) opened; finish those in the Store. " : "") +
                (failures > 0 ? $"{failures} failed. " : "") + "Details are in the activity log.");
        });
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        Busy.IsActive = AppsBusy.IsActive = true;
        TweakList.IsEnabled = ToolsPanel.IsEnabled = AppList.IsEnabled = AppFilterBox.IsEnabled = UndoAppsLink.IsEnabled = PermList.IsEnabled = InstallList.IsEnabled = RepositoriesPanel.IsEnabled = false;
        UpdateSelectionText();
        UpdateAppSelectionText();
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppendLog($"✗ {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Something went wrong", ex.Message);
        }
        finally
        {
            _busy = false;
            Busy.IsActive = AppsBusy.IsActive = false;
            TweakList.IsEnabled = ToolsPanel.IsEnabled = AppList.IsEnabled = AppFilterBox.IsEnabled = UndoAppsLink.IsEnabled = PermList.IsEnabled = InstallList.IsEnabled = RepositoriesPanel.IsEnabled = true;
            UpdateSelectionText();
            UpdateAppSelectionText();
            if (_catalog is not null) UpdateInstallSelection();
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void AppendLog(string line)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
        if (FindChild<ScrollViewer>(LogBox) is { } scroller)
            scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}

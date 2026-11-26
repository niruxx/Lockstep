using Lockstep.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Lockstep;

public sealed partial class MainWindow
{
    private bool _networkBusy;
    private bool _loadingPrivacyHistory;

    private void FindPrivacyTweaks_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(item => Equals(item.Tag, AllCategory));
        SearchBox.Text = (string)((HyperlinkButton)sender).Tag;
    }

    private async Task ShowPrivacyReportAsync()
    {
        if (!PrivacyHistory.IsReportOnly) return;
        try
        {
            var latest = await Task.Run(() => PrivacyHistory.Read().FirstOrDefault());
            if (latest?.Outcome == "Reported")
                ShowStatus(InfoBarSeverity.Informational, "Privacy settings need review",
                    $"A change was recorded on {latest.DetectedUtc.ToLocalTime():g}. Open Privacy controls to review the change history.");
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Warning, "Could not read privacy history", ex.Message); }
    }

    private async void NetworkRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunNetworkAsync();

    private void NetworkSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        NetworkBlockButton.IsEnabled = NetworkConnections.SelectedItem is AppNetworkConnection { Path: not null };
        NetworkRemoveButton.IsEnabled = NetworkRules.SelectedItem is AppNetworkBlockRule;
    }

    private async void NetworkBlock_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkConnections.SelectedItem is not AppNetworkConnection { Path: { } path }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Block outbound traffic?",
            Content = new TextBlock
            {
                Text = $"This rule applies to {path} for all users and network profiles. Blocking a shared executable may affect several apps or Windows services.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Block outbound traffic",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await RunNetworkAsync(() => AppNetwork.Block(path), "Outbound block saved");
    }

    private async void NetworkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkRules.SelectedItem is AppNetworkBlockRule rule)
            await RunNetworkAsync(() => AppNetwork.Remove(rule), "Outbound block removed");
    }

    private async Task RunNetworkAsync(Action? change = null, string? success = null)
    {
        if (_networkBusy) return;
        _networkBusy = true;
        AppNetworkCommands.IsEnabled = false;
        try
        {
            if (change is not null)
            {
                await Task.Run(change);
                ShowStatus(InfoBarSeverity.Success, success!, "");
            }

            var rules = await Task.Run(AppNetwork.GetRules);
            if (_category == PrivacyControlsCategory) NetworkRules.ItemsSource = rules;
            var connections = await Task.Run(AppNetwork.GetConnections);
            if (_category == PrivacyControlsCategory)
            {
                NetworkConnections.ItemsSource = connections;
                if (change is null) ShowStatus(InfoBarSeverity.Informational,
                    $"{connections.Count} established TCP connection(s)", $"{rules.Count} Lockstep app block(s).");
            }
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Error, "App network request failed", ex.Message); }
        finally
        {
            _networkBusy = false;
            AppNetworkCommands.IsEnabled = true;
        }
    }

    private async void ClearClipboardHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Clear clipboard history?",
            Content = "This deletes unpinned history entries. Pinned items stay in Win+V.",
            PrimaryButtonText = "Clear history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (!Clipboard.ClearHistory()) throw new InvalidOperationException("Windows declined the request.");
            ShowStatus(InfoBarSeverity.Success, "Clipboard history cleared", "Pinned items stay in Win+V.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Warning, "Clipboard history could not be cleared",
                $"{ex.Message} Use Clear clipboard data under Clipboard settings.");
        }
    }

    private async void WifiPrivacyRefresh_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => ProcessRunner.Run(ProcessRunner.System32("netsh.exe"), "wlan show randomization"));
            if (_category != PrivacyControlsCategory) return;
            if (!result.Success) throw new InvalidOperationException(result.Message);
            WifiPrivacyStatus.Text = string.IsNullOrWhiteSpace(result.Message)
                ? "Windows returned no adapter information."
                : result.Message;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not read Wi-Fi adapter status", ex.Message);
        }
        finally { button.IsEnabled = true; }
    }

    private void LoadPrivacyHistory()
    {
        _loadingPrivacyHistory = true;
        try
        {
            PrivacyReportOnly.IsOn = PrivacyHistory.IsReportOnly;
            PrivacyHistoryList.ItemsSource = PrivacyHistory.Read().Select(change =>
                $"{(TweakCatalog.All.FirstOrDefault(tweak => tweak.Id == change.TweakId)?.Title ?? change.TweakId)}\n" +
                $"{change.DetectedUtc.ToLocalTime():g}  ·  {change.Status}  ·  {change.Outcome}").ToArray();
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Error, "Could not read privacy history", ex.Message); }
        finally { _loadingPrivacyHistory = false; }
    }

    private void PrivacyReportOnly_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingPrivacyHistory || _category != PrivacyControlsCategory) return;
        try { PrivacyHistory.SetReportOnly(PrivacyReportOnly.IsOn); }
        catch (Exception ex)
        {
            LoadPrivacyHistory();
            ShowStatus(InfoBarSeverity.Error, "Could not change reporting mode", ex.Message);
        }
    }

    private void PrivacyHistoryRefresh_Click(object sender, RoutedEventArgs e) => LoadPrivacyHistory();

    private async void PrivacyCheck_Click(object sender, RoutedEventArgs e)
    {
        PrivacyCheckButton.IsEnabled = false;
        try
        {
            int count = await Task.Run(() => Enforcement.Report(_log.Report));
            if (_category == PrivacyControlsCategory)
            {
                LoadPrivacyHistory();
                ShowStatus(InfoBarSeverity.Informational, "Privacy check finished",
                    count == 0 ? "Tracked settings match." : $"{count} tracked setting(s) need review.");
            }
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Error, "Privacy check failed", ex.Message); }
        finally { PrivacyCheckButton.IsEnabled = true; }
    }

    private async void PrivacyHistoryClear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete privacy change history?",
            PrimaryButtonText = "Delete history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Task.Run(PrivacyHistory.Clear);
            LoadPrivacyHistory();
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Error, "Could not clear privacy history", ex.Message); }
    }
}

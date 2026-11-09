using System.ComponentModel;
using System.Runtime.CompilerServices;
using FuWindows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace FuWindows.ViewModels;

public sealed class AppItem(AppPackageInfo info) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public AppPackageInfo Info { get; } = info;

    public string DisplayName => Info.DisplayName;

    public string Subtitle => Info.State is AppInstallState.Available or AppInstallState.Removed
        ? $"{Info.Publisher}  ·  {Info.Name}"
        : $"{Info.Publisher}  ·  {Info.Version}  ·  {Info.Name}";

    /// <summary>Protected system apps can't be uninstalled, but anything not installed can be installed.</summary>
    public bool IsSelectable => Info.CanUninstall || Info.CanInstall;

    public string Note => Info.Note ?? "";
    public Visibility NoteVisibility => Info.Note is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LockVisibility => Info.IsProtected && Info.State == AppInstallState.Installed ? Visibility.Visible : Visibility.Collapsed;

    public string StateText => Info.State switch
    {
        AppInstallState.Installed => Info.IsProvisioned ? "Also installed for new users" : "",
        AppInstallState.OtherUsersOnly => "Hidden: installed for other accounts only",
        AppInstallState.ProvisionedOnly => "Hidden: staged for new users, not installed",
        AppInstallState.Removed => Info.RemovedUtc is { } d ? $"Removed by FuWindows {d.ToLocalTime():d}" : "Removed by FuWindows",
        _ => "Not installed",
    };
    public Visibility StateVisibility => StateText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string GroupLabel => Info.State switch
    {
        AppInstallState.Removed => "Removed",
        AppInstallState.Available => "Optional",
        _ => Info.Group switch
        {
            AppGroup.Suggested => "Suggested to remove",
            AppGroup.Microsoft => "Microsoft",
            AppGroup.ThirdParty => "Third-party",
            _ => Info.IsProtected ? "Protected" : "System",
        },
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];
    public SolidColorBrush GroupBrush
    {
        get
        {
            var label = GroupLabel;
            if (!Brushes.TryGetValue(label, out var brush))
            {
                brush = new SolidColorBrush(label switch
                {
                    "Suggested to remove" => Color.FromArgb(255, 0x0F, 0x7B, 0x0F),
                    "Microsoft" => Color.FromArgb(255, 0x00, 0x5F, 0xB8),
                    "Third-party" => Color.FromArgb(255, 0x6B, 0x1F, 0x8F),
                    "Removed" => Color.FromArgb(255, 0x9D, 0x5D, 0x00),
                    "Optional" => Color.FromArgb(255, 0x00, 0x7A, 0x7A),
                    _ => Color.FromArgb(255, 0x60, 0x60, 0x60),
                });
                Brushes[label] = brush;
            }
            return brush;
        }
    }

    private BitmapImage? _logo;
    public ImageSource? Logo
    {
        get
        {
            if (_logo is null && Info.LogoPath is { } path && File.Exists(path))
            {
                try { _logo = new BitmapImage(new Uri(path)) { DecodePixelWidth = 64 }; } catch { }
            }
            return _logo;
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            value &= IsSelectable;
            if (_isSelected != value) { _isSelected = value; Raise(); }
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

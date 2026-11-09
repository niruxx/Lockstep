using System.ComponentModel;
using System.Runtime.CompilerServices;
using FuWindows.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FuWindows.ViewModels;

public sealed class TweakItem(Tweak tweak) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Tweak Tweak { get; } = tweak;

    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Warning => Tweak.Warning ?? "";
    public Visibility WarningVisibility => Tweak.Warning is null ? Visibility.Collapsed : Visibility.Visible;
    public string Details => string.Join("\n", Tweak.Actions.Select(a => a.Describe()));

    public string RiskLabel => Tweak.Risk.ToString();
    public SolidColorBrush RiskBrush => Palette.Risk(Tweak.Risk);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    private TweakStatus _status = TweakStatus.Unknown;
    public TweakStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            Raise(nameof(StatusLabel));
            Raise(nameof(StatusBrush));
        }
    }

    public string StatusLabel => Status switch
    {
        TweakStatus.Applied => "Applied",
        TweakStatus.Partial => "Partially applied",
        TweakStatus.NotApplied => "Not applied",
        TweakStatus.NotApplicable => "Not on this PC",
        _ => "Checking…",
    };

    public SolidColorBrush StatusBrush => Palette.Status(Status);

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Brushes are created lazily because XAML objects must be created on the UI thread.</summary>
internal static class Palette
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    private static SolidColorBrush Get(string key, Color color)
    {
        if (!Cache.TryGetValue(key, out var brush)) Cache[key] = brush = new SolidColorBrush(color);
        return brush;
    }

    public static SolidColorBrush Risk(RiskLevel risk) => risk switch
    {
        RiskLevel.Recommended => Get("r0", Color.FromArgb(255, 0x0F, 0x7B, 0x0F)),
        RiskLevel.Limited => Get("r1", Color.FromArgb(255, 0x9D, 0x5D, 0x00)),
        RiskLevel.Aggressive => Get("r2", Color.FromArgb(255, 0xC4, 0x2B, 0x1C)),
        _ => Get("r3", Color.FromArgb(255, 0x6B, 0x1F, 0x8F)),
    };

    public static SolidColorBrush Status(TweakStatus status) => status switch
    {
        TweakStatus.Applied => Get("s-applied", Color.FromArgb(255, 0x6C, 0xCB, 0x5F)),
        TweakStatus.Partial => Get("s-partial", Color.FromArgb(255, 0xFC, 0xE1, 0x00)),
        TweakStatus.NotApplied => Get("s-not", Color.FromArgb(255, 0x9A, 0x9A, 0x9A)),
        _ => Get("s-none", Colors.Transparent),
    };
}

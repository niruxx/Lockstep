using System.ComponentModel;
using System.Runtime.CompilerServices;
using FuWindows.Core;

namespace FuWindows.ViewModels;

public sealed class CatalogItem(Installer.CatalogApp app) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public Installer.CatalogApp App { get; } = app;
    public string Name => App.Name;
    public string Description => App.Description;
    public string Category => App.Category;
    public string Ids => App.ChocoId is null
        ? $"winget: {App.WingetId}" + (App.WingetSource == "msstore" ? " (Microsoft Store)" : "")
        : $"winget: {App.WingetId}  ·  choco: {App.ChocoId}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    private bool? _installed;
    /// <summary>null = not checked yet.</summary>
    public bool? Installed
    {
        get => _installed;
        set { _installed = value; Raise(); Raise(nameof(StatusText)); }
    }

    public string StatusText => Installed switch
    {
        true => "Installed",
        false => "",
        null => "",
    };

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

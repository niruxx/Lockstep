using System.ComponentModel;
using Lockstep.Core;
using Microsoft.UI.Xaml;

namespace Lockstep.ViewModels;

/// <summary>One app in the App permissions list. Flipping the switch writes the same value Settings does.</summary>
public sealed class PermissionItem(string capabilityId, AppPermissions.Entry entry, bool canManage, Action<string> log) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName => entry.DisplayName;
    private bool IsEditable => !entry.IsDesktopApp && canManage;
    public Visibility ToggleVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;

    public string Detail =>
        (entry.IsDesktopApp ? "Desktop app · Observed access · " : "") +
        (entry.InUse ? "In use right now"
         : entry.LastUsed is { } t ? $"Last used {t.ToLocalTime():g}"
         : "No recorded use");

    private bool _allowed = entry.Allowed;
    public bool Allowed
    {
        get => _allowed;
        set
        {
            if (_allowed == value || !IsEditable) return;
            try
            {
                AppPermissions.SetAllowed(capabilityId, entry.Key, value);
                _allowed = value;
                log($"{(value ? "Allowed" : "Denied")} {DisplayName}: {capabilityId}");
            }
            catch (Exception ex)
            {
                log($"✗ {DisplayName}: {ex.Message}");
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Allowed)));
        }
    }
}

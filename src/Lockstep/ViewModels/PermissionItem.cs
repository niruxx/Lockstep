using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lockstep.Core;

namespace Lockstep.ViewModels;

/// <summary>One app in the App permissions list. Flipping the switch writes the same value Settings does.</summary>
public sealed class PermissionItem(string capabilityId, AppPermissions.Entry entry, Action<string> log) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName => entry.DisplayName;
    public bool IsEditable => !entry.IsDesktopApp;

    public string Detail =>
        (entry.IsDesktopApp ? "Desktop app (controlled by the desktop-apps switch)  ·  " : "") +
        (entry.InUse ? "In use right now"
         : entry.LastUsed is { } t ? $"Last used {t.ToLocalTime():g}"
         : "Never used");

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

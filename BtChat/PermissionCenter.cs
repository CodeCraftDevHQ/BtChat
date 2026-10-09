using CommunityToolkit.Mvvm.ComponentModel;

namespace BtChat;

// One line of the Settings > Permissions list.
// CanToggle = false: the permission is granted automatically when the app is installed (nothing to switch).
public sealed record PermissionRow(PermissionKind Kind, bool Granted, bool CanToggle);

public interface IPermissionCenter
{
    Task<IReadOnlyList<PermissionRow>> GetRowsAsync();

    // enable = true: ask for the permission (or open the system settings when it was refused for good).
    // enable = false: an app can not take back its own permission, so the system settings are opened.
    Task SetAsync(PermissionKind kind, bool enable);
}

public sealed partial class PermissionItem : ObservableObject
{
    public PermissionItem(PermissionRow row)
    {
        Kind = row.Kind;
        CanToggle = row.CanToggle;
        granted = row.Granted;
    }

    public PermissionKind Kind { get; }
    public bool CanToggle { get; }
    public string Title => Loc.Instance["perm" + Kind + "Title"];
    public string Info => Loc.Instance["permInfo" + Kind];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    bool granted;

    public string StateText => !CanToggle ? Loc.Instance["permAutomatic"] : Granted ? Loc.Instance["permOn"] : Loc.Instance["permOff"];

    // Tells the switch to show the real state again (after the user flipped it but nothing changed).
    public void Resync() => OnPropertyChanged(nameof(Granted));
}

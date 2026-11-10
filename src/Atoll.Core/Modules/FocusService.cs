using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Modules;

/// <summary>
/// ATOLL's quiet mode: a timed Do Not Disturb window, plus an optional mirror of Windows Focus.
/// Modules check <see cref="IsQuietNow"/> before popping an alert or playing a sound.
/// </summary>
public sealed class FocusService(TimeProvider time) : ObservableObject
{
    public static FocusService? Current { get; set; }

    /// <summary>True while ATOLL should stay quiet, from either source.</summary>
    public static bool IsQuietNow => Current?.IsActive == true;

    private DateTimeOffset? _until;
    private bool _windowsFocus;
    private bool _enabled = true;
    private bool _followWindowsFocus = true;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool FollowWindowsFocus
    {
        get => _followWindowsFocus;
        set => Set(ref _followWindowsFocus, value);
    }

    public bool WindowsFocus => _windowsFocus;

    public DateTimeOffset? Until => _until;

    public bool IsActive => _enabled &&
        (_until is { } until && until > time.GetUtcNow() || _followWindowsFocus && _windowsFocus);

    public string Status => IsActive
        ? _until is { } until
            ? until == DateTimeOffset.MaxValue ? "Do Not Disturb" : $"Do Not Disturb until {until.ToLocalTime():t}"
            : "Do Not Disturb with Windows Focus"
        : "Do Not Disturb";

    public void Configure(bool enabled, bool followWindows)
    {
        var changed = _enabled != enabled || _followWindowsFocus != followWindows;
        _enabled = enabled;
        _followWindowsFocus = followWindows;
        if (changed) Raise();
    }

    public void FocusFor(TimeSpan duration) => FocusUntil(time.GetUtcNow() + duration);

    public void FocusUntil(DateTimeOffset at)
    {
        _until = at;
        Raise();
    }

    public void FocusIndefinitely() => FocusUntil(DateTimeOffset.MaxValue);

    public void Clear()
    {
        if (_until is null) return;
        _until = null;
        Raise();
    }

    public void SetWindowsFocus(bool active)
    {
        if (_windowsFocus == active) return;
        _windowsFocus = active;
        Raise();
    }

    /// <summary>Expires a timed window. Call from a slow shell timer.</summary>
    public void Tick()
    {
        if (_until is not { } until || until > time.GetUtcNow()) return;
        _until = null;
        Raise();
    }

    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Until));
        OnPropertyChanged(nameof(WindowsFocus));
    }
}

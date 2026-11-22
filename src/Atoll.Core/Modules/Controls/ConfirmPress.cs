namespace Atoll.Modules.Controls;

/// <summary>
/// Two-press arming for actions that are hard to undo (sleep, restart, shut down): the first press arms one
/// key, a second press of the same key within the window runs it. Pressing another key moves the arming.
/// </summary>
public sealed class ConfirmPress(TimeProvider time, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(3);
    private string? _armed;
    private DateTimeOffset _armedAt;

    /// <summary>The armed key, or null when nothing is armed or the window has passed.</summary>
    public string? Armed => _armed is not null && time.GetUtcNow() - _armedAt <= _window ? _armed : null;

    /// <summary>True when this press confirms an armed action and it should run now.</summary>
    public bool Press(string key)
    {
        if (Armed == key)
        {
            _armed = null;
            return true;
        }
        _armed = key;
        _armedAt = time.GetUtcNow();
        return false;
    }

    public void Reset() => _armed = null;
}

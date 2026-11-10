namespace Atoll.Shell;

/// <summary>Asks every island window to hide from screen capture while a private feature runs.</summary>
public static class CapturePrivacy
{
    public static event Action<bool>? Changed;

    public static void Set(bool hidden) => Changed?.Invoke(hidden);
}

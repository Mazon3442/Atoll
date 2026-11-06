#if AVALONIA
using Avalonia.Threading;
using Atoll.Desktop;
#else
using System.Windows.Threading;
#endif
using Atoll.Activities;
using Atoll.Settings;

namespace Atoll.Modules;

/// <summary>A feature of the island (clock, media, Claude, ...). Created when enabled, disposed when disabled.</summary>
public interface IIslandModule : IDisposable
{
    /// <summary>View model for this module's tab in the expanded panel ("{TypeName}.Panel" template). Null = no tab.</summary>
    object? Panel { get; }

    void Start(ModuleContext context);
}

public sealed record ModuleDescriptor(string Id, string Name, string Glyph, Func<IIslandModule> Create);

public sealed class ModuleContext(string moduleId, ActivityHost activities, Dispatcher dispatcher, SettingsService settings)
{
    public string ModuleId { get; } = moduleId;
    public ActivityHost Activities { get; } = activities;

    /// <summary>Live app settings (read on use; they change while the module runs).</summary>
    public SettingsService Settings { get; } = settings;

    /// <summary>UI thread dispatcher, for modules whose sources raise events on other threads.</summary>
    public Dispatcher Dispatcher { get; } = dispatcher;

    /// <summary>Posts an activity owned by this module. <paramref name="key"/> is unique within the module.</summary>
    public void Post(string key, object content, ActivityPriority priority, TimeSpan? duration = null, bool alert = false) =>
        Activities.Post(new Activity($"{ModuleId}.{key}", ModuleId, content, priority, duration, alert));

    public void Remove(string key) => Activities.Remove($"{ModuleId}.{key}");
}

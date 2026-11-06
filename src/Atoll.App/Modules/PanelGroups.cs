using Atoll.Modules.Actions;
using Atoll.Modules.Calendar;
using Atoll.Modules.Clipboard;
using Atoll.Modules.Clock;
using Atoll.Modules.Indicators;
using Atoll.Modules.Media;
using Atoll.Modules.Shelf;
using Atoll.Modules.Stats;

namespace Atoll.Modules;

public sealed record MediaPanelViewModel(MediaViewModel? Player, SystemPanelViewModel? Sound);
public sealed record TimePanelViewModel(ClockPanelViewModel? Clock, CalendarViewModel? Calendar);
public sealed record FilesPanelViewModel(ShelfViewModel? Shelf, ClipboardViewModel? Clipboard);
public sealed record SystemGroupViewModel(SystemPanelViewModel? Devices, StatsViewModel? Stats,
    QuickActionsViewModel? Actions, SystemPanelViewModel? Sound);

internal static class PanelGroups
{
    internal static string TabId(string id) => id switch
    {
        "agents" => "claude",
        "calendar" => "clock",
        "files" or "clipboard" => "shelf",
        "stats" or "actions" => "system",
        _ => id,
    };

    public static List<ModuleTab> Combine(IReadOnlyList<ModuleTab> modules)
    {
        T? Panel<T>(string id) where T : class => modules.FirstOrDefault(t => t.Descriptor.Id == id)?.Panel as T;
        var result = new List<ModuleTab>();
        var added = new HashSet<string>();
        foreach (var module in modules)
        {
            var id = module.Descriptor.Id;
            var group = TabId(id);
            if (!added.Add(group)) continue;
            (string Name, object Panel) combined = group switch
            {
                "media" => ("Media", new MediaPanelViewModel(Panel<MediaViewModel>("media"), Panel<SystemPanelViewModel>("system"))),
                "clock" => ("Clock & calendar", new TimePanelViewModel(Panel<ClockPanelViewModel>("clock"), Panel<CalendarViewModel>("calendar"))),
                "shelf" => ("Files", new FilesPanelViewModel(Panel<ShelfViewModel>("shelf"), Panel<ClipboardViewModel>("clipboard"))),
                "system" => ("System", new SystemGroupViewModel(Panel<SystemPanelViewModel>("system"), Panel<StatsViewModel>("stats"),
                    Panel<QuickActionsViewModel>("actions"), Panel<MediaViewModel>("media") is null ? Panel<SystemPanelViewModel>("system") : null)),
                _ => (module.Descriptor.Name, module.Panel),
            };
            result.Add(new ModuleTab(module.Descriptor with { Id = group, Name = combined.Name }, combined.Panel));
        }
        return result;
    }
}

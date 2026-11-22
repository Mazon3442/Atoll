using Atoll.Settings;
using Atoll.Shell;

namespace Atoll.Tests;

public class ShortcutCatalogTests
{
    [Fact]
    public void Defaults_are_valid_global_shortcuts_and_never_collide()
    {
        var settings = new AppSettings();
        var seen = new List<string> { settings.OpenShortcut, settings.HideShortcut, settings.PaletteShortcut };
        foreach (var def in ShortcutCatalog.All.Where(def => def.Default.Length > 0))
        {
            Assert.True(HotkeyService.TryParse(def.Default, out _, out _), def.Id);
            seen.Add(def.Default);
        }
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(def => def.Id).Distinct().Count());
    }

    [Fact]
    public void A_saved_value_wins_and_an_empty_one_turns_the_shortcut_off()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl+Alt+D", ShortcutCatalog.Resolve(settings, ShortcutCatalog.Dictate));
        settings.Shortcuts[ShortcutCatalog.Dictate] = " Ctrl+Alt+F ";
        Assert.Equal("Ctrl+Alt+F", ShortcutCatalog.Resolve(settings, ShortcutCatalog.Dictate));
        settings.Shortcuts[ShortcutCatalog.Dictate] = "";
        Assert.Equal("", ShortcutCatalog.Resolve(settings, ShortcutCatalog.Dictate));
        Assert.Equal("", ShortcutCatalog.Resolve(settings, "unknown"));
    }

    [Fact]
    public void Changed_shortcuts_survive_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.Shortcuts[ShortcutCatalog.Ocr] = "Ctrl+Alt+J";
            store.Save(settings);
            Assert.Equal("Ctrl+Alt+J", ShortcutCatalog.Resolve(store.Load(), ShortcutCatalog.Ocr));
        }
        finally { File.Delete(path); }
    }
}

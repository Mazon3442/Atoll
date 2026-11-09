using System.Collections.ObjectModel;
using System.ComponentModel;
using Atoll.Modules;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Onboarding;

/// <summary>
/// First-run welcome: explains the island, lets the user pick the top row and points at Settings.
/// The picker edits bookmarks only; everything left unstarred stays enabled in the ⋯ menu.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Action<bool>? _applyStartup;

    public OnboardingViewModel(AppSettings settings, Action save, Action<bool>? applyStartup = null)
    {
        _settings = settings;
        _save = save;
        _applyStartup = applyStartup;
        StartWithWindows = settings.StartWithWindows;

        Tabs.Add(new OnboardingTabRow("home", "Home",
            "Your day at a glance: now playing, weather, timer, this month and what's next.",
            "\uE80F", [], settings.Home.Bookmarked ?? true, true, OnRowToggled));

        foreach (var info in SettingsViewModel.TabInfo)
        {
            var members = ModuleCatalog.All
                .Where(d => d.EnabledByDefault && PanelGroups.TabId(d.Id) == info.Id)
                .ToList();
            if (members.Count == 0) continue;
            var bookmarked = BookmarkedInSettings(members);
            var fallback = members.Any(d => d.BookmarkedByDefault);
            Tabs.Add(new OnboardingTabRow(info.Id, info.Name, info.Description, members[0].Glyph, members,
                bookmarked, fallback, OnRowToggled));
        }
        RefreshPreview();
    }

    public ObservableCollection<OnboardingTabRow> Tabs { get; } = [];

    /// <summary>The chips shown in the live top-row preview, in island order.</summary>
    public ObservableCollection<OnboardingTabRow> TopRowPreview { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsPicker), nameof(IsReady), nameof(CanGoBack),
        nameof(CanSkip), nameof(StepLabel), nameof(PrimaryLabel))]
    private int _stepIndex;

    public bool IsWelcome => StepIndex == 0;
    public bool IsPicker => StepIndex == 1;
    public bool IsReady => StepIndex == 2;
    public bool CanGoBack => StepIndex > 0;
    public bool CanSkip => StepIndex == 0;

    public string StepLabel => StepIndex switch
    {
        0 => "Takes about a minute",
        1 => "Step 1 of 2",
        _ => "Step 2 of 2",
    };

    public string PrimaryLabel => StepIndex switch
    {
        0 => "Choose my tabs",
        1 => "Review top row",
        _ => "Finish setup",
    };

    [ObservableProperty] private bool _startWithWindows;

    public int BookmarkedCount => Tabs.Count(tab => tab.Bookmarked);
    public bool HasTopRow => TopRowPreview.Count > 0;

    public string TopRowSummary => BookmarkedCount switch
    {
        0 => "No tabs in the top row yet. Open the ⋯ menu to reach everything.",
        1 => "1 tab in your top row. Everything else stays on and waits in the ⋯ menu.",
        _ => $"{BookmarkedCount} tabs in your top row. Everything else stays on and waits in the ⋯ menu.",
    };

    /// <summary>Raised when the user finishes or skips; the window closes and settings are already saved.</summary>
    public event Action? Completed;

    [RelayCommand]
    private void Next()
    {
        if (StepIndex < 2) StepIndex++;
    }

    [RelayCommand]
    private void Primary()
    {
        if (IsReady) Finish();
        else Next();
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private void Skip()
    {
        // "Use defaults" is honest even after the user has edited the picker and gone back.
        foreach (var tab in Tabs)
            if (tab.Bookmarked != tab.DefaultBookmarked) tab.Bookmarked = tab.DefaultBookmarked;
        Finish();
    }

    [RelayCommand]
    private void Finish()
    {
        foreach (var tab in Tabs) tab.Apply(_settings);
        _settings.StartWithWindows = StartWithWindows;
        _settings.OnboardingComplete = true;
        _applyStartup?.Invoke(StartWithWindows);
        _save();
        Completed?.Invoke();
    }

    /// <summary>Closing the window counts as skipping, so the welcome does not return next launch.</summary>
    public void Dismiss()
    {
        if (_settings.OnboardingComplete) return;
        _settings.OnboardingComplete = true;
        _save();
    }

    private bool BookmarkedInSettings(IReadOnlyList<ModuleDescriptor> members)
    {
        var entries = _settings.Modules.Where(entry => members.Any(d => d.Id == entry.Id)).ToList();
        if (entries.Count == 0) return members.Any(d => d.BookmarkedByDefault);
        return entries.Any(entry => entry.Enabled &&
            (entry.Bookmarked ?? members.FirstOrDefault(d => d.Id == entry.Id)?.BookmarkedByDefault ?? true));
    }

    private void OnRowToggled() => RefreshPreview();

    private void RefreshPreview()
    {
        TopRowPreview.Clear();
        foreach (var tab in Tabs.Where(tab => tab.Bookmarked)) TopRowPreview.Add(tab);
        OnPropertyChanged(nameof(BookmarkedCount));
        OnPropertyChanged(nameof(HasTopRow));
        OnPropertyChanged(nameof(TopRowSummary));
    }
}

/// <summary>One tab card in the welcome picker.</summary>
public sealed partial class OnboardingTabRow(
    string id, string name, string description, string glyph, IReadOnlyList<ModuleDescriptor> members,
    bool bookmarked, bool defaultBookmarked, Action toggled) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public bool DefaultBookmarked { get; } = defaultBookmarked;

    [ObservableProperty] private bool _bookmarked = bookmarked;

    partial void OnBookmarkedChanged(bool value) => toggled();

    /// <summary>Writes the picker's choice back. A starred tab is also switched on.</summary>
    public void Apply(AppSettings settings)
    {
        if (Id == "home")
        {
            settings.Home.Bookmarked = Bookmarked;
            if (Bookmarked) settings.Home.Enabled = true;
            return;
        }

        foreach (var descriptor in members)
        {
            var entry = settings.Modules.FirstOrDefault(e => e.Id == descriptor.Id);
            if (entry is null)
            {
                entry = new ModuleEntry { Id = descriptor.Id, Enabled = descriptor.EnabledByDefault };
                settings.Modules.Add(entry);
            }
            entry.Bookmarked = Bookmarked;
            if (Bookmarked) entry.Enabled = true;
        }
    }
}

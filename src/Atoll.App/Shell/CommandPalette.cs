using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Shell;

public enum CommandKind
{
    Tab,
    Action,
    Timer,
    App,
}

/// <summary>One row in the command palette. <see cref="Run"/> closes the palette first.</summary>
public sealed record CommandItem(string Title, string Detail, string Glyph, string Initial, CommandKind Kind, Action Run)
{
    public bool ShowGlyph => Glyph.Length > 0;
    public bool ShowInitial => !ShowGlyph;
    public int Rank => Kind switch { CommandKind.Tab => 0, CommandKind.Action => 1, CommandKind.Timer => 2, _ => 3 };
}

/// <summary>Search over tabs, quick actions, timers and Start Menu apps. UI thread only.</summary>
public sealed partial class CommandPaletteViewModel(Func<IReadOnlyList<CommandItem>> build) : ObservableObject
{
    private IReadOnlyList<CommandItem> _all = [];

    public ObservableCollection<CommandItem> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private CommandItem? _selected;
    [ObservableProperty] private bool _hasResults;

    /// <summary>Raised when the palette should close, before the chosen command runs.</summary>
    public event Action? CloseRequested;

    public void Refresh()
    {
        _all = build();
        Query = "";
        Rebuild();
    }

    public void Close() => CloseRequested?.Invoke();

    public void RunSelected()
    {
        if (Selected is not { } item) return;
        CloseRequested?.Invoke();
        item.Run();
    }

    partial void OnQueryChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var query = Query.Trim();
        var matches = query.Length == 0
            ? _all.Take(16)
            : _all.Select(item => (item, score: Score(item, query)))
                .Where(result => result.score >= 0)
                .OrderBy(result => result.score)
                .ThenBy(result => result.item.Rank)
                .ThenBy(result => result.item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(16)
                .Select(result => result.item);

        Results.Clear();
        foreach (var item in matches) Results.Add(item);
        Selected = Results.FirstOrDefault();
        HasResults = Results.Count > 0;
    }

    private static int Score(CommandItem item, string query)
    {
        if (item.Title.Equals(query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (item.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 1;
        if (item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 2;
        return item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 3 : -1;
    }
}

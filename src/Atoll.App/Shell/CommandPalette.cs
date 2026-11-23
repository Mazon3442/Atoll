using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atoll.Shell;

public enum CommandKind
{
    /// <summary>Answers to what was typed (a sum, a conversion, a timer): always on top.</summary>
    Result,
    Tab,
    Action,
    Timer,
    File,
    App,
    /// <summary>"Ask AI" and "Search the web": always last, so a query never ends in an empty list.</summary>
    Fallback,
}

/// <summary>
/// One row in the command palette. <see cref="Run"/> closes the palette first. <see cref="RunFolder"/> (Ctrl+Enter)
/// opens the folder of a file or program, and <see cref="RunElevated"/> (Ctrl+Shift+Enter) starts a program as administrator.
/// </summary>
public sealed record CommandItem(string Title, string Detail, string Glyph, string Initial, CommandKind Kind, Action Run,
    Action? RunFolder = null, Action? RunElevated = null)
{
    public bool ShowGlyph => Glyph.Length > 0;
    public bool ShowInitial => !ShowGlyph;
    public int Rank => Kind switch
    {
        CommandKind.Result => -1, CommandKind.Tab => 0, CommandKind.Action => 1, CommandKind.Timer => 2, CommandKind.File => 3, CommandKind.App => 4, _ => 5,
    };
}

/// <summary>What typing a query adds on top of the matched rows.</summary>
public sealed record QueryItems(IReadOnlyList<CommandItem> Results, IReadOnlyList<CommandItem> Fallbacks)
{
    public static QueryItems None { get; } = new([], []);
}

/// <summary>Search over tabs, quick actions, timers, files and Start Menu apps. UI thread only.</summary>
public sealed partial class CommandPaletteViewModel(Func<IReadOnlyList<CommandItem>> build, Func<string, QueryItems>? query = null) : ObservableObject
{
    private const int MaxResults = 16;
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

    /// <summary>Rebuilds for the same text, for answers that arrive late (exchange rates, the file index).</summary>
    public void Requery() => Rebuild();

    public void Close() => CloseRequested?.Invoke();

    /// <summary>Runs the selected row. <paramref name="folder"/> is Ctrl+Enter, <paramref name="elevated"/> Ctrl+Shift+Enter.</summary>
    public void RunSelected(bool folder = false, bool elevated = false)
    {
        if (Selected is not { } item) return;
        var run = elevated ? item.RunElevated ?? item.Run : folder ? item.RunFolder ?? item.Run : item.Run;
        CloseRequested?.Invoke();
        run();
    }

    partial void OnQueryChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var text = Query.Trim();
        var typed = text.Length == 0 || query is null ? QueryItems.None : query(text);
        var matches = text.Length == 0
            ? _all.Take(MaxResults)
            : _all.Select(item => (item, score: Score(item, text)))
                .Where(result => result.score >= 0)
                .OrderBy(result => result.score)
                .ThenBy(result => result.item.Rank)
                .ThenBy(result => result.item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(result => result.item);

        var rows = typed.Results.Concat(matches).Take(MaxResults - typed.Fallbacks.Count).Concat(typed.Fallbacks).ToList();
        Results.Clear();
        foreach (var item in rows) Results.Add(item);
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

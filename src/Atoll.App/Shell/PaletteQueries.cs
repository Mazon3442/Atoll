using System.IO;
using System.Windows;
using Atoll.Modules;
using Atoll.Modules.Claude;
using Atoll.Modules.Clock;
using Atoll.Modules.Notes;
using Atoll.Modules.Palette;
using Atoll.Settings;

namespace Atoll.Shell;

/// <summary>
/// Turns what was typed into the rows the palette adds on top of its normal matches: a sum, a conversion, a timer,
/// a reminder, a shell line, files by name, and the "Ask AI" and "Search the web" rows at the end.
/// </summary>
internal sealed class PaletteQueries(SettingsService settings, ModuleManager modules, Action<Action> runAndTuck, Action requery)
{
    private readonly FileIndex _files = new();
    private readonly CurrencyRates _rates = new();

    public QueryItems Build(string text)
    {
        var results = new List<CommandItem>();
        var fallbacks = new List<CommandItem>();

        if (ShellPhrase.TryParse(text, out var shell, out var command))
        {
            var name = shell == ShellPhrase.Shell.Cmd ? "Command Prompt" : "PowerShell";
            results.Add(new CommandItem($"Run in {name}: {command}", "Shell", "", "", CommandKind.Result,
                () => runAndTuck(() => PaletteActions.RunShell(shell, command))));
            return new QueryItems(results, []);
        }

        AddAnswer(text, results);
        AddTimer(text, results);
        AddReminder(text, results);
        AddFiles(text, results);

        if (modules.Tabs.Select(tab => tab.Panel).OfType<ClaudeViewModel>().FirstOrDefault() is { } claude)
            fallbacks.Add(new CommandItem($"Ask AI: “{text}”", "Ask", "", "", CommandKind.Fallback, () => runAndTuck(() => Ask(claude, text))));
        fallbacks.Add(new CommandItem($"Search the web for “{text}”", "Browser", "", "", CommandKind.Fallback,
            () => runAndTuck(() => PaletteActions.WebSearch(text))));
        return new QueryItems(results, fallbacks);
    }

    private void AddAnswer(string text, List<CommandItem> results)
    {
        if (Calculator.TryEvaluate(text, out var value))
        {
            var answer = Calculator.Format(value);
            results.Add(Copy("= " + answer, "Calculator · copies the answer", answer));
            return;
        }
        if (UnitConverter.TryConvert(text, out var converted))
        {
            results.Add(Copy(converted, "Conversion · copies the answer", converted));
            return;
        }
        if (!settings.Current.Palette.Currency || !CurrencyConverter.TryParse(text, out var amount, out var from, out var to)) return;
        if (_rates.Current is { } rates)
        {
            if (!CurrencyConverter.TryConvert(amount, from, to, rates, out var money)) return;
            var line = $"{Calculator.Format(Math.Round(money, 2))} {to}";
            results.Add(Copy(line, $"{Calculator.Format(amount)} {from} · copies the answer", line));
            return;
        }
        // Rates arrive in a moment; the palette refreshes itself when they do.
        _rates.EnsureFetched(() => Application.Current?.Dispatcher.BeginInvoke(requery));
        results.Add(new CommandItem($"{Calculator.Format(amount)} {from} to {to}", "Looking up exchange rates…", "", "", CommandKind.Result, () => { }));
    }

    private void AddTimer(string text, List<CommandItem> results)
    {
        if (modules.Tabs.Select(tab => tab.Panel).OfType<ClockPanelViewModel>().FirstOrDefault() is not { } clock) return;
        if (TimerPhrase.IsStopwatch(text))
            results.Add(new CommandItem("Start the stopwatch", "Timer", "", "", CommandKind.Result,
                () => runAndTuck(() => clock.Timer.StartStopwatchCommand.Execute(null))));
        else if (TimerPhrase.TryParse(text, out var length))
            results.Add(new CommandItem($"Start a {Describe(length)} timer", "Timer", "", "", CommandKind.Result,
                () => runAndTuck(() => clock.Timer.StartCountdownCommand.Execute(length.TotalMinutes))));
    }

    private void AddReminder(string text, List<CommandItem> results)
    {
        if (!text.StartsWith("remind", StringComparison.OrdinalIgnoreCase) || !ReminderPhrase.TryParse(text, DateTimeOffset.Now, out var parsed)) return;
        var item = new ReminderItem { Title = parsed.Title, Due = parsed.Due, RepeatDays = parsed.RepeatDays };
        results.Add(new CommandItem($"Remind: {parsed.Title}", item.DueText, "", "", CommandKind.Result, () => runAndTuck(() => AddReminder(item))));
    }

    private void AddFiles(string text, List<CommandItem> results)
    {
        if (!settings.Current.Palette.Files || text.Length < 2) return;
        var folders = settings.Current.Palette.Folders.Count > 0 ? settings.Current.Palette.Folders : FileIndex.DefaultFolders().ToList();
        _files.EnsureStarted(folders);
        foreach (var hit in _files.Search(text, 5))
        {
            var path = hit.Path;
            results.Add(new CommandItem(hit.Name, Path.GetDirectoryName(path) ?? "", hit.IsDirectory ? "" : "", "", CommandKind.File,
                () => runAndTuck(() => PaletteActions.Open(path)),
                RunFolder: () => runAndTuck(() => PaletteActions.ShowInFolder(path)),
                RunElevated: hit.IsDirectory ? null : () => runAndTuck(() => PaletteActions.Elevated(path))));
        }
    }

    private CommandItem Copy(string title, string detail, string value) =>
        new(title, detail, "", "", CommandKind.Result, () =>
        {
            try { Clipboard.SetText(value); }
            catch (System.Runtime.InteropServices.COMException) { /* the clipboard was busy */ }
        });

    private void AddReminder(ReminderItem item)
    {
        if (modules.Tabs.Select(tab => tab.Panel).OfType<NotesViewModel>().FirstOrDefault() is { } notes) notes.Add(item);
        else
        {
            settings.Current.Notes.Reminders.Add(item); // Notes is switched off: the reminder still fires when it is back on
            settings.NotifyChanged();
        }
    }

    private static void Ask(ClaudeViewModel claude, string text)
    {
        claude.SectionIndex = 1;
        claude.Ask.Draft = text;
        if (claude.Ask.SendCommand.CanExecute(null)) claude.Ask.SendCommand.Execute(null);
        Atoll.Navigation.OpenModule?.Invoke("claude");
    }

    private static string Describe(TimeSpan length) =>
        length.TotalMinutes >= 60 && length.TotalMinutes % 60 == 0 ? $"{(int)length.TotalHours} hour"
        : length.TotalSeconds < 60 ? $"{(int)length.TotalSeconds} second"
        : length.TotalSeconds % 60 == 0 ? $"{(int)length.TotalMinutes} minute"
        : $"{(int)length.TotalMinutes}:{length.Seconds:00}";
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Notes;

/// <summary>
/// Notes with formatting, reminders typed as one phrase, and a small to-do list. Everything lives in the settings
/// file. Each note is one tab in the editor; the right column holds reminders and to-dos.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject, IDisposable
{
    private readonly NotesSettings _settings;
    private readonly Action _save;
    private readonly Func<DateTimeOffset> _now;
    private bool _disposed;

    public NotesViewModel(NotesSettings settings, Action save, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _save = save;
        _now = now ?? (() => DateTimeOffset.Now);
        foreach (var note in settings.Notes)
        {
            Notes.Add(note);
            note.PropertyChanged += OnItemChanged;
        }
        foreach (var reminder in settings.Reminders.OrderBy(reminder => reminder.Done).ThenBy(reminder => reminder.Due))
        {
            Reminders.Add(reminder);
            reminder.PropertyChanged += OnItemChanged;
        }
        foreach (var todo in settings.Todos)
        {
            Todos.Add(todo);
            todo.PropertyChanged += OnItemChanged;
        }
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
        Reminders.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasReminders)); RaiseCounts(); };
        Todos.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasTodos)); RaiseCounts(); };
        _selectedNote = Notes.FirstOrDefault();
        RefreshTimes();
    }

    public ObservableCollection<NoteItem> Notes { get; } = [];
    public ObservableCollection<ReminderItem> Reminders { get; } = [];
    public ObservableCollection<TodoItem> Todos { get; } = [];
    public IReadOnlyList<string> Sections { get; } = ["Reminders", "To-do"];
    public bool HasNotes => Notes.Count > 0;
    public bool HasReminders => Reminders.Count > 0;
    public bool HasTodos => Todos.Count > 0;
    public int OpenReminderCount => Reminders.Count(reminder => !reminder.Done);
    public int OpenTodoCount => Todos.Count(todo => !todo.Done);

    /// <summary>The note being edited; null only when there are no notes.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedNote))]
    private NoteItem? _selectedNote;

    public bool HasSelectedNote => SelectedNote is not null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReminders), nameof(IsTodos))]
    private int _sectionIndex;

    public bool IsReminders => SectionIndex == 0;
    public bool IsTodos => SectionIndex == 1;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReminderTitle)), NotifyCanExecuteChangedFor(nameof(AddReminderCommand))]
    private string _reminderTitle = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTodoDraft), nameof(CanAddTodo)), NotifyCanExecuteChangedFor(nameof(AddTodoCommand))]
    private string _todoDraft = "";

    [ObservableProperty] private string _message = "";

    public bool CanAddReminder => ReminderTitle.Trim().Length > 0;
    public bool CanAddTodo => TodoDraft.Trim().Length > 0;
    public bool HasReminderTitle => ReminderTitle.Length > 0;
    public bool HasTodoDraft => TodoDraft.Length > 0;

    /// <summary>The editor reports its text here; saving is debounced by the editor itself.</summary>
    public void UpdateNote(NoteItem note, string plain, string rich)
    {
        if (note.Text == plain && note.Rich == rich) return;
        note.Text = plain;
        note.Rich = rich;
        note.Updated = _now();
    }

    [RelayCommand]
    private void NewNote()
    {
        var item = new NoteItem { Updated = _now() };
        Notes.Add(item);
        _settings.Notes.Add(item);
        item.PropertyChanged += OnItemChanged;
        SelectedNote = item;
        Message = "";
        Save();
    }

    [RelayCommand]
    private void DeleteNote(NoteItem? note)
    {
        note ??= SelectedNote;
        if (note is null) return;
        var index = Notes.IndexOf(note);
        note.PropertyChanged -= OnItemChanged;
        Notes.Remove(note);
        _settings.Notes.Remove(note);
        if (ReferenceEquals(SelectedNote, note)) SelectedNote = Notes.Count == 0 ? null : Notes[Math.Min(index, Notes.Count - 1)];
        Message = "Note deleted.";
        Save();
    }

    /// <summary>Adds a reminder from one phrase such as "call mum tomorrow at 9" or "pills every day at 23:00".</summary>
    [RelayCommand(CanExecute = nameof(CanAddReminder))]
    private void AddReminder()
    {
        if (!ReminderPhrase.TryParse(ReminderTitle, _now(), out var parsed))
        {
            Message = "Add when: tomorrow at 9, in 20 minutes, every day at 23:00.";
            return;
        }
        Add(new ReminderItem { Title = parsed.Title, Due = parsed.Due, RepeatDays = parsed.RepeatDays });
        ReminderTitle = "";
        Message = "Reminder set.";
    }

    /// <summary>Adds a ready-made reminder, for example one the command palette parsed.</summary>
    public void Add(ReminderItem item)
    {
        Reminders.Add(item);
        _settings.Reminders.Add(item);
        item.PropertyChanged += OnItemChanged;
        SortReminders();
        Save();
    }

    /// <summary>Puts a reminder back 10 minutes from now (or from its due time while it is still ahead).</summary>
    [RelayCommand]
    private void Snooze(ReminderItem? reminder)
    {
        if (reminder is null) return;
        var now = _now();
        reminder.Done = false;
        reminder.Due = (reminder.Due > now ? reminder.Due : now).AddMinutes(10);
        SortReminders();
        Message = "Snoozed for 10 minutes.";
    }

    [RelayCommand]
    private void DeleteReminder(ReminderItem? reminder)
    {
        if (reminder is null) return;
        reminder.PropertyChanged -= OnItemChanged;
        Reminders.Remove(reminder);
        _settings.Reminders.Remove(reminder);
        RaiseCounts();
        Message = "Reminder deleted.";
        Save();
    }

    [RelayCommand]
    private void ClearDoneReminders()
    {
        foreach (var reminder in Reminders.Where(r => r.Done).ToList())
        {
            reminder.PropertyChanged -= OnItemChanged;
            Reminders.Remove(reminder);
            _settings.Reminders.Remove(reminder);
        }
        Message = "Finished reminders cleared.";
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanAddTodo))]
    private void AddTodo()
    {
        var text = TodoDraft.Trim();
        if (text.Length == 0) return;
        var item = new TodoItem { Text = text };
        Todos.Add(item);
        _settings.Todos.Add(item);
        item.PropertyChanged += OnItemChanged;
        TodoDraft = "";
        Message = "To-do added.";
        Save();
    }

    [RelayCommand]
    private void DeleteTodo(TodoItem? todo)
    {
        if (todo is null) return;
        todo.PropertyChanged -= OnItemChanged;
        Todos.Remove(todo);
        _settings.Todos.Remove(todo);
        Message = "To-do deleted.";
        Save();
    }

    [RelayCommand]
    private void ClearDoneTodos()
    {
        foreach (var todo in Todos.Where(t => t.Done).ToList())
        {
            todo.PropertyChanged -= OnItemChanged;
            Todos.Remove(todo);
            _settings.Todos.Remove(todo);
        }
        Message = "Finished to-dos cleared.";
        Save();
    }

    /// <summary>
    /// Fires what is due: one-off reminders are marked done, repeating ones move to their next day.
    /// Returns the reminders that fired so the module can show them.
    /// </summary>
    public IReadOnlyList<ReminderItem> TakeDue()
    {
        var now = _now();
        var due = _settings.Reminders.Where(reminder => !reminder.Done && reminder.Due <= now).ToList();
        foreach (var reminder in due)
        {
            if (reminder.RepeatDays != 0)
                reminder.Due = ReminderPhrase.NextRepeat(reminder.RepeatDays, reminder.Due.LocalDateTime.TimeOfDay, now);
            else reminder.Done = true;
        }
        return due;
    }

    /// <summary>Refresh "in 10 min" labels without writing settings on every tick.</summary>
    public void RefreshTimes()
    {
        foreach (var reminder in Reminders) reminder.RefreshTime();
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(OpenReminderCount));
        OnPropertyChanged(nameof(OpenTodoCount));
    }

    private void SortReminders()
    {
        var sorted = Reminders.OrderBy(reminder => reminder.Done).ThenBy(reminder => reminder.Due).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = Reminders.IndexOf(sorted[i]);
            if (at != i) Reminders.Move(at, i);
        }
        RaiseCounts();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteItem.UpdatedText) or nameof(NoteItem.Title) or nameof(ReminderItem.DueText) or nameof(ReminderItem.IsOverdue)) return;
        if (sender is ReminderItem && e.PropertyName is nameof(ReminderItem.Done) or nameof(ReminderItem.Due)) SortReminders();
        if (sender is TodoItem && e.PropertyName == nameof(TodoItem.Done)) RaiseCounts();
        Save();
    }

    private void Save()
    {
        if (!_disposed) _save();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var note in Notes) note.PropertyChanged -= OnItemChanged;
        foreach (var reminder in Reminders) reminder.PropertyChanged -= OnItemChanged;
        foreach (var todo in Todos) todo.PropertyChanged -= OnItemChanged;
    }
}

/// <summary>Notes tab: formatted notes, phrase reminders and a to-do list, with reminder alerts.</summary>
public sealed class NotesModule : IIslandModule
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    private ModuleContext? _context;
    private NotesViewModel? _vm;

    public object? Panel => _vm;

    public void Start(ModuleContext context)
    {
        _context = context;
        _vm = new NotesViewModel(context.Settings.Current.Notes, () => context.Settings.NotifyChanged());
        _timer.Tick += (_, _) => CheckReminders();
        _timer.Start();
        CheckReminders();
    }

    private void CheckReminders()
    {
        if (_context is null || _vm is null) return;
        var due = _vm.TakeDue();
        foreach (var reminder in due)
        {
            _context.Post($"reminder.{reminder.Id}", new IndicatorViewModel("", reminder.Title, reminder.RepeatDays != 0 ? "Repeating reminder" : "Reminder"),
                ActivityPriority.Critical, TimeSpan.FromSeconds(10), alert: true);
            SystemSounds.Exclamation.Play();
        }
        _vm.RefreshTimes();
        if (due.Count > 0) _context.Settings.NotifyChanged();
    }

    public void Dispose()
    {
        _timer.Stop();
        _vm?.Dispose();
    }
}

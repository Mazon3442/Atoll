using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atoll.Modules.Notes;

/// <summary>Notes, one-off reminders and a small to-do list. Everything lives in the settings file.</summary>
public sealed partial class NotesViewModel : ObservableObject, IDisposable
{
    private readonly NotesSettings _settings;
    private readonly Action _save;
    private NoteItem? _editing;
    private bool _disposed;

    public NotesViewModel(NotesSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        foreach (var note in settings.Notes.OrderByDescending(note => note.Updated))
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
        Reminders.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasReminders)); RaiseNextReminder(); };
        Todos.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasTodos)); OnPropertyChanged(nameof(OpenTodoCount)); };
        RefreshTimes();
    }

    /// <summary>The soonest reminder that is not done, for the card beside the lists.</summary>
    public ReminderItem? NextReminder => Reminders.Where(reminder => !reminder.Done).OrderBy(reminder => reminder.Due).FirstOrDefault();
    public bool HasNextReminder => NextReminder is not null;
    public int OpenReminderCount => Reminders.Count(reminder => !reminder.Done);
    public int OpenTodoCount => Todos.Count(todo => !todo.Done);

    private void RaiseNextReminder()
    {
        OnPropertyChanged(nameof(NextReminder));
        OnPropertyChanged(nameof(HasNextReminder));
        OnPropertyChanged(nameof(OpenReminderCount));
        OnPropertyChanged(nameof(OpenTodoCount));
    }

    [RelayCommand]
    private void SnoozeNext()
    {
        if (NextReminder is not { } reminder) return;
        var from = reminder.Due > DateTimeOffset.Now ? reminder.Due : DateTimeOffset.Now;
        reminder.Due = from.AddMinutes(10);
        SortReminders();
        Message = "Snoozed for 10 minutes.";
    }

    [RelayCommand]
    private void FinishNext()
    {
        if (NextReminder is not { } reminder) return;
        reminder.Done = true; // OnItemChanged sorts and saves
        Message = "Reminder done.";
    }

    public ObservableCollection<NoteItem> Notes { get; } = [];
    public ObservableCollection<ReminderItem> Reminders { get; } = [];
    public ObservableCollection<TodoItem> Todos { get; } = [];
    public IReadOnlyList<string> Sections { get; } = ["Notes", "Reminders", "To-do"];
    public bool HasNotes => Notes.Count > 0;
    public bool HasReminders => Reminders.Count > 0;
    public bool HasTodos => Todos.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotes), nameof(IsReminders), nameof(IsTodos))]
    private int _sectionIndex;

    public bool IsNotes => SectionIndex == 0;
    public bool IsReminders => SectionIndex == 1;
    public bool IsTodos => SectionIndex == 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveNote), nameof(HasNoteDraft))]
    [NotifyCanExecuteChangedFor(nameof(SaveNoteCommand))]
    private string _noteDraft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddReminder), nameof(HasReminderTitle))]
    [NotifyCanExecuteChangedFor(nameof(AddReminderCommand))]
    private string _reminderTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddTodo), nameof(HasTodoDraft))]
    [NotifyCanExecuteChangedFor(nameof(AddTodoCommand))]
    private string _todoDraft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteButtonLabel))]
    private bool _editingNote;

    [ObservableProperty] private string _message = "";

    public bool CanSaveNote => NoteDraft.Trim().Length > 0;
    public bool CanAddReminder => ReminderTitle.Trim().Length > 0;
    public bool CanAddTodo => TodoDraft.Trim().Length > 0;
    public bool HasNoteDraft => NoteDraft.Length > 0;
    public bool HasReminderTitle => ReminderTitle.Length > 0;
    public bool HasTodoDraft => TodoDraft.Length > 0;
    public string NoteButtonLabel => EditingNote ? "Save note" : "Add note";

    [RelayCommand(CanExecute = nameof(CanSaveNote))]
    private void SaveNote()
    {
        var text = NoteDraft.Trim();
        if (text.Length == 0) return;
        if (_editing is { } note)
        {
            note.Text = text;
            note.Updated = DateTimeOffset.Now;
            if (Notes.Remove(note)) Notes.Insert(0, note);
            Message = "Note updated.";
        }
        else
        {
            var item = new NoteItem { Text = text };
            Notes.Insert(0, item);
            _settings.Notes.Insert(0, item);
            item.PropertyChanged += OnItemChanged;
            Message = "Note saved.";
        }
        _editing = null;
        EditingNote = false;
        NoteDraft = "";
        Save();
    }

    [RelayCommand]
    private void NewNote()
    {
        _editing = null;
        EditingNote = false;
        NoteDraft = "";
        Message = "";
    }

    [RelayCommand]
    private void EditNote(NoteItem? note)
    {
        if (note is null) return;
        _editing = note;
        EditingNote = true;
        NoteDraft = note.Text;
        SectionIndex = 0;
        Message = "";
    }

    [RelayCommand]
    private void DeleteNote(NoteItem? note)
    {
        if (note is null) return;
        note.PropertyChanged -= OnItemChanged;
        Notes.Remove(note);
        _settings.Notes.Remove(note);
        if (ReferenceEquals(_editing, note)) NewNote();
        Message = "Note deleted.";
        Save();
    }

    [RelayCommand(CanExecute = nameof(CanAddReminder))]
    private void AddReminder(string? when)
    {
        var title = ReminderTitle.Trim();
        if (title.Length == 0) return;
        var now = DateTimeOffset.Now;
        DateTimeOffset due;
        if (when == "tomorrow")
        {
            var tomorrow = now.Date.AddDays(1).AddHours(9);
            due = new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
        }
        else due = now.AddMinutes(int.TryParse(when, out var minutes) ? minutes : 10);

        var item = new ReminderItem { Title = title, Due = due };
        Reminders.Add(item);
        _settings.Reminders.Add(item);
        item.PropertyChanged += OnItemChanged;
        ReminderTitle = "";
        Message = "Reminder set.";
        SortReminders();
        Save();
    }

    [RelayCommand]
    private void DeleteReminder(ReminderItem? reminder)
    {
        if (reminder is null) return;
        reminder.PropertyChanged -= OnItemChanged;
        Reminders.Remove(reminder);
        _settings.Reminders.Remove(reminder);
        RaiseNextReminder();
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

    /// <summary>Refresh "in 10 min" labels without writing settings on every tick.</summary>
    public void RefreshTimes()
    {
        foreach (var reminder in Reminders) reminder.RefreshTime();
        RaiseNextReminder();
    }

    private void SortReminders()
    {
        var sorted = Reminders.OrderBy(reminder => reminder.Done).ThenBy(reminder => reminder.Due).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = Reminders.IndexOf(sorted[i]);
            if (at != i) Reminders.Move(at, i);
        }
        RaiseNextReminder();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteItem.UpdatedText) or nameof(ReminderItem.DueText) or nameof(ReminderItem.IsOverdue)) return;
        if (sender is ReminderItem && e.PropertyName == nameof(ReminderItem.Done)) SortReminders();
        if (sender is TodoItem && e.PropertyName == nameof(TodoItem.Done)) OnPropertyChanged(nameof(OpenTodoCount));
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

/// <summary>Notes tab: notes, quick reminders and a to-do list, with reminder alerts.</summary>
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
        var now = DateTimeOffset.Now;
        var due = _context.Settings.Current.Notes.Reminders.Where(reminder => !reminder.Done && reminder.Due <= now).ToList();
        foreach (var reminder in due)
        {
            reminder.Done = true;
            _context.Post($"reminder.{reminder.Id}", new IndicatorViewModel("\uE916", reminder.Title, "Reminder"),
                ActivityPriority.Critical, TimeSpan.FromSeconds(10), alert: true);
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

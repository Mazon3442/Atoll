using Atoll.Modules.Notes;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class NotesTests : IDisposable
{
    private static readonly DateTime Local = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset Now = new(Local, TimeZoneInfo.Local.GetUtcOffset(Local));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "atoll-notes-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Notes_are_tabs_that_keep_their_text_and_formatting()
    {
        var settings = new NotesSettings();
        var saves = 0;
        using var vm = new NotesViewModel(settings, () => saves++, () => Now);

        vm.NewNoteCommand.Execute(null);
        var note = Assert.Single(settings.Notes);
        Assert.Same(note, vm.SelectedNote);
        Assert.Equal("New note", note.Title);

        vm.UpdateNote(note, "Friday release\n☐ Record the video", "<FlowDocument/>");
        Assert.Equal("Friday release", note.Title);
        Assert.Equal("<FlowDocument/>", note.Rich);
        Assert.Equal(Now, note.Updated);

        vm.NewNoteCommand.Execute(null);
        Assert.Equal(2, vm.Notes.Count);
        vm.DeleteNoteCommand.Execute(vm.SelectedNote);
        Assert.Same(note, vm.SelectedNote); // selection falls back to the neighbour
        vm.DeleteNoteCommand.Execute(null);
        Assert.Empty(settings.Notes);
        Assert.False(vm.HasSelectedNote);
        Assert.True(saves > 0);
    }

    [Fact]
    public void A_note_title_skips_task_and_bullet_marks()
    {
        Assert.Equal("Record the video", new NoteItem { Text = "☐ Record the video" }.Title);
        Assert.Equal("Buy milk", new NoteItem { Text = "\n\n- Buy milk" }.Title);
        Assert.EndsWith("…", new NoteItem { Text = "A very long first line that keeps going" }.Title);
    }

    [Fact]
    public void A_reminder_phrase_sets_the_title_the_time_and_the_repeat()
    {
        var settings = new NotesSettings();
        using var vm = new NotesViewModel(settings, () => { }, () => Now);

        vm.ReminderTitle = "pills every day at 23:00";
        vm.AddReminderCommand.Execute(null);
        var reminder = Assert.Single(settings.Reminders);
        Assert.Equal("Pills", reminder.Title);
        Assert.Equal(0x7F, reminder.RepeatDays);
        Assert.Equal("", vm.ReminderTitle);

        vm.ReminderTitle = "buy milk";
        vm.AddReminderCommand.Execute(null);
        Assert.Single(settings.Reminders);                 // no time: nothing is added
        Assert.Contains("when", vm.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("buy milk", vm.ReminderTitle);         // and the text stays so it can be fixed
    }

    [Fact]
    public void Due_reminders_finish_once_or_move_to_their_next_day()
    {
        var settings = new NotesSettings();
        var clock = Now;
        using var vm = new NotesViewModel(settings, () => { }, () => clock);
        vm.ReminderTitle = "standup every weekday at 9:00";
        vm.AddReminderCommand.Execute(null);
        vm.ReminderTitle = "call mum in 5 minutes";
        vm.AddReminderCommand.Execute(null);

        clock = Now.AddMinutes(6);
        var fired = vm.TakeDue();
        Assert.Equal("Call mum", Assert.Single(fired).Title);
        Assert.True(settings.Reminders.Single(r => r.Title == "Call mum").Done);

        clock = Now.AddDays(1).AddMinutes(-30); // Tuesday 9:30: the weekday reminder was due at 9:00
        fired = vm.TakeDue();
        var repeating = Assert.Single(fired);
        Assert.Equal("Standup", repeating.Title);
        Assert.False(repeating.Done);
        Assert.True(repeating.Due > clock);
        Assert.Equal(new TimeSpan(9, 0, 0), repeating.Due.LocalDateTime.TimeOfDay);
    }

    [Fact]
    public void Snooze_moves_a_reminder_ten_minutes_ahead_and_reopens_it()
    {
        var settings = new NotesSettings();
        settings.Reminders.Add(new ReminderItem { Title = "Standup", Due = Now.AddMinutes(-1), Done = true });
        using var vm = new NotesViewModel(settings, () => { }, () => Now);

        vm.SnoozeCommand.Execute(settings.Reminders[0]);

        Assert.False(settings.Reminders[0].Done);
        Assert.Equal(Now.AddMinutes(10), settings.Reminders[0].Due);
    }

    [Fact]
    public void Todos_still_work()
    {
        var settings = new NotesSettings();
        using var vm = new NotesViewModel(settings, () => { }, () => Now);
        vm.TodoDraft = "Ship it";
        vm.AddTodoCommand.Execute(null);
        Assert.Equal("Ship it", Assert.Single(settings.Todos).Text);
        Assert.Equal(1, vm.OpenTodoCount);
        settings.Todos[0].Done = true;
        Assert.Equal(0, vm.OpenTodoCount);
    }

    [Fact]
    public void Notes_survive_the_settings_json_round_trip()
    {
        Directory.CreateDirectory(_directory);
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new AppSettings();
        settings.Notes.Notes.Add(new NoteItem { Text = "hello", Rich = "<FlowDocument/>", Updated = DateTimeOffset.UnixEpoch });
        settings.Notes.Todos.Add(new TodoItem { Text = "ship", Done = true });
        settings.Notes.Reminders.Add(new ReminderItem { Title = "later", Due = DateTimeOffset.UnixEpoch.AddHours(1), RepeatDays = 0b0111110 });

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal("hello", Assert.Single(loaded.Notes.Notes).Text);
        Assert.Equal("<FlowDocument/>", loaded.Notes.Notes[0].Rich);
        Assert.True(Assert.Single(loaded.Notes.Todos).Done);
        Assert.Equal(0b0111110, Assert.Single(loaded.Notes.Reminders).RepeatDays);
    }

    [Fact]
    public void Notes_saved_before_formatting_existed_still_load()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{"Notes":{"Notes":[{"Id":"a","Text":"old note","Updated":"2026-01-01T00:00:00+00:00"}],"Reminders":[],"Todos":[]}}""");

        var note = Assert.Single(new SettingsStore(path).Load().Notes.Notes);

        Assert.Equal("old note", note.Text);
        Assert.Equal("", note.Rich); // the editor falls back to the plain text
    }

    [Fact]
    public void Reminder_labels_explain_due_done_and_repeat()
    {
        var reminder = new ReminderItem { Due = DateTimeOffset.Now.AddMinutes(10) };
        Assert.Contains("in", reminder.DueText);
        reminder.RepeatDays = 0x7F;
        Assert.StartsWith("Repeats", reminder.DueText);
        reminder.Done = true;
        Assert.Equal("Done", reminder.DueText);
        Assert.False(reminder.IsOverdue);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

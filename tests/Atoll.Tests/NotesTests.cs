using Atoll.Modules.Notes;
using Atoll.Settings;

namespace Atoll.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "atoll-notes-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Notes_todos_and_reminders_round_trip()
    {
        var settings = new NotesSettings();
        var saves = 0;
        using var vm = new NotesViewModel(settings, () => saves++);

        vm.NoteDraft = "Remember this";
        vm.SaveNoteCommand.Execute(null);
        Assert.Equal("Remember this", Assert.Single(settings.Notes).Text);

        vm.EditNoteCommand.Execute(settings.Notes[0]);
        vm.NoteDraft = "Remember this today";
        vm.SaveNoteCommand.Execute(null);
        Assert.Equal("Remember this today", Assert.Single(settings.Notes).Text);

        vm.TodoDraft = "Ship it";
        vm.AddTodoCommand.Execute(null);
        Assert.True(Assert.Single(settings.Todos).Text == "Ship it");
        settings.Todos[0].Done = true;
        Assert.True(settings.Todos[0].Done);

        vm.ReminderTitle = "Standup";
        vm.AddReminderCommand.Execute("10");
        var reminder = Assert.Single(settings.Reminders);
        Assert.Equal("Standup", reminder.Title);
        Assert.True(reminder.Due > DateTimeOffset.Now);

        vm.DeleteNoteCommand.Execute(settings.Notes[0]);
        Assert.Empty(settings.Notes);
        Assert.True(saves > 0);
    }

    [Fact]
    public void Notes_survive_the_settings_json_round_trip()
    {
        Directory.CreateDirectory(_directory);
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new AppSettings();
        settings.Notes.Notes.Add(new NoteItem { Text = "hello", Updated = DateTimeOffset.UnixEpoch });
        settings.Notes.Todos.Add(new TodoItem { Text = "ship", Done = true });
        settings.Notes.Reminders.Add(new ReminderItem { Title = "later", Due = DateTimeOffset.UnixEpoch.AddHours(1) });

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal("hello", Assert.Single(loaded.Notes.Notes).Text);
        Assert.True(Assert.Single(loaded.Notes.Todos).Done);
        Assert.Equal("later", Assert.Single(loaded.Notes.Reminders).Title);
    }

    [Fact]
    public void Reminder_labels_explain_due_and_done()
    {
        var reminder = new ReminderItem { Due = DateTimeOffset.Now.AddMinutes(10) };
        Assert.Contains("in", reminder.DueText);
        reminder.Done = true;
        Assert.Equal("Done", reminder.DueText);
        Assert.False(reminder.IsOverdue);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

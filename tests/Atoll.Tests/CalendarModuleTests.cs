using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows.Data;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Calendar;
using Atoll.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class CalendarModuleTests
{
    internal const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:all-day
        SUMMARY:All-day event
        DTSTART;VALUE=DATE:20261110
        DTEND;VALUE=DATE:20261111
        END:VEVENT
        BEGIN:VEVENT
        UID:active
        SUMMARY:Meeting in progress
        DTSTART:20261110T140000Z
        DTEND:20261110T160000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:next
        SUMMARY:Next meeting
        DTSTART:20261110T160000Z
        DTEND:20261110T170000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:repeat
        SUMMARY:Daily meeting
        DTSTART:20261110T180000Z
        DTEND:20261110T190000Z
        RRULE:FREQ=DAILY;COUNT=2
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Async_refresh_updates_the_bound_list_on_its_dispatcher_and_preserves_event_choices() => OnSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "atoll-calendar-" + Guid.NewGuid() + ".json");
        var store = new SettingsStore(path);
        var settings = new SettingsService(store);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero));
        using var handler = new PendingFeed();
        using var http = new HttpClient(handler);
        using var host = new ActivityHost(time);
        using var module = new CalendarModule(http, time);
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            module.Start(new ModuleContext("calendar", host, dispatcher, settings));
            Pump(module.RefreshAsync());
            settings.Current.Calendar.Sources.Add(new CalendarSource { Name = "Test calendar", EncryptedUrl = SecretStore.Protect("https://example.test/feed.ics") });
            var vm = Assert.IsType<CalendarViewModel>(module.Panel);
            var view = CollectionViewSource.GetDefaultView(vm.Days);
            var changes = 0;
            view.CollectionChanged += (_, _) => { Assert.True(dispatcher.CheckAccess()); changes++; };
            var load = module.RefreshAsync();
            Assert.True(vm.IsRefreshing);
            Assert.False(vm.RefreshCommand.CanExecute(null));
            handler.Complete(Feed);
            Pump(load);
            Assert.True(changes > 0);
            Assert.True(vm.HasEvents);
            Assert.Equal(5, vm.Days.Sum(d => d.Events.Count));
            Assert.False(vm.IsRefreshing);
            Assert.Equal("Meeting in progress", Next(host).Title);

            var allDay = vm.Days.SelectMany(d => d.Events).Single(e => e.Event.AllDay);
            Next(host).DismissCommand.Execute(null);
            Assert.Equal("Next meeting", Next(host).Title);
            allDay.TogglePinCommand.Execute(null);
            Assert.Equal("All-day event", Next(host).Title);
            Assert.Equal("All day", Next(host).When);
            Assert.True(Next(host).IsPinned);
            allDay.TogglePinCommand.Execute(null);
            Assert.Equal("Next meeting", Next(host).Title);
            Next(host).DismissCommand.Execute(null);
            Assert.Equal("Daily meeting", Next(host).Title);
            allDay.TogglePinCommand.Execute(null);
            settings.Flush();
            Assert.Equal(2, store.Load().Calendar.DismissedOccurrences.Count);
            Assert.Equal(allDay.Event.OccurrenceKey, store.Load().Calendar.PinnedOccurrence);

            var refresh = module.RefreshAsync();
            handler.Complete(Feed);
            Pump(refresh);
            Assert.Equal("All-day event", Next(host).Title);
            Assert.Equal(2, vm.Days.SelectMany(d => d.Events).Count(e => e.IsDismissed));
            Next(host).DismissCommand.Execute(null);
            Assert.Equal("Daily meeting", Next(host).Title);
            Assert.Equal(5, vm.Days.Sum(d => d.Events.Count));

            time.Advance(TimeSpan.FromDays(1));
            refresh = module.RefreshAsync();
            handler.Complete(Feed);
            Pump(refresh);
            Assert.Empty(settings.Current.Calendar.DismissedOccurrences);
            Assert.Equal("Daily meeting", Next(host).Title);
            Assert.False(Assert.Single(vm.Days.SelectMany(d => d.Events)).IsDismissed);

            refresh = module.RefreshAsync();
            handler.Complete("<html>Sign in</html>");
            Pump(refresh);
            Assert.False(vm.HasEvents);
            Assert.Contains("ICS", vm.EmptyMessage);
            Assert.True(vm.RefreshCommand.CanExecute(null));
        }
        finally { settings.Flush(); File.Delete(path); }
    });

    [Fact]
    public void Removing_sources_cancels_the_old_download_and_clears_the_list() => OnSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "atoll-calendar-" + Guid.NewGuid() + ".json");
        var settings = new SettingsService(new SettingsStore(path));
        using var handler = new PendingFeed();
        using var http = new HttpClient(handler);
        using var host = new ActivityHost();
        using var module = new CalendarModule(http, TimeProvider.System);
        try
        {
            module.Start(new ModuleContext("calendar", host, Dispatcher.CurrentDispatcher, settings));
            Pump(module.RefreshAsync());
            settings.Current.Calendar.Sources.Add(new CalendarSource { EncryptedUrl = SecretStore.Protect("https://example.test/feed.ics") });
            var old = module.RefreshAsync();
            settings.Current.Calendar.Sources.Clear();
            Pump(module.RefreshAsync());
            handler.Complete(Feed);
            Pump(old);
            var vm = Assert.IsType<CalendarViewModel>(module.Panel);
            Assert.False(vm.HasSources);
            Assert.False(vm.HasEvents);
            Assert.False(vm.IsRefreshing);
            Assert.Empty(vm.Days);
            Assert.Null(host.Current.Primary);
        }
        finally { settings.Flush(); File.Delete(path); }
    });

    private static NextEventViewModel Next(ActivityHost host) => Assert.IsType<NextEventViewModel>(host.Current.Primary?.Content);

    private sealed class PendingFeed : HttpMessageHandler
    {
        private TaskCompletionSource<string> _pending = null!;
        internal void Complete(string content) => _pending.SetResult(content);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = await _pending.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Calendar dispatcher test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

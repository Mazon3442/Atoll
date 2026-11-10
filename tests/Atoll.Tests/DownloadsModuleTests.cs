using System.Diagnostics;
using System.Windows.Threading;
using Atoll.Activities;
using Atoll.Modules;
using Atoll.Modules.Downloads;
using Atoll.Settings;

namespace Atoll.Tests;

/// <summary>
/// The tracker is unit-tested on its own; this drives the module the way the island does, through a real
/// folder watcher and the real activity host, so the wiring is covered too.
/// </summary>
public sealed class DownloadsModuleTests
{
    [Fact]
    public void A_download_shows_while_it_runs_and_is_announced_when_it_finishes()
    {
        var folder = Directory.CreateTempSubdirectory("atoll-downloads-");
        try
        {
            using var host = new ActivityHost();
            var service = new SettingsService(new SettingsStore(Path.Combine(folder.FullName, "settings.json")));
            using var module = new DownloadsModule(() => folder.FullName);
            module.Start(new ModuleContext("downloads", host, Dispatcher.CurrentDispatcher, service));

            var temp = Path.Combine(folder.FullName, "report.pdf.crdownload");
            File.WriteAllText(temp, "hello");

            Assert.True(Pump(() => Shows(host, alert: false)), "the running download never appeared");

            var running = host.Snapshot(4).Primary;
            Assert.NotNull(running);
            Assert.Equal("downloads", running!.ModuleId);
            var live = Assert.IsType<DownloadsViewModel>(running.Content);
            Assert.Equal("report.pdf", live.Name);
            Assert.Contains("report.pdf", live.Summary);

            var final = Path.Combine(folder.FullName, "report.pdf");
            File.Move(temp, final);

            Assert.True(Pump(() => Shows(host, alert: true)), "the finished download was never announced");
            var done = host.Snapshot(4).Primary!;
            var content = Assert.IsType<DownloadCompleteViewModel>(done.Content);
            Assert.Equal("report.pdf", content.Name);
            Assert.Equal(final, content.Path);
            Assert.False(string.IsNullOrWhiteSpace(content.SizeText));
        }
        finally
        {
            try { folder.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_file_that_merely_appears_is_never_announced()
    {
        var folder = Directory.CreateTempSubdirectory("atoll-downloads-quiet-");
        try
        {
            using var host = new ActivityHost();
            var service = new SettingsService(new SettingsStore(Path.Combine(folder.FullName, "settings.json")));
            using var module = new DownloadsModule(() => folder.FullName);
            module.Start(new ModuleContext("downloads", host, Dispatcher.CurrentDispatcher, service));

            File.WriteAllText(Path.Combine(folder.FullName, "copied.txt"), "not a download");
            Pump(() => false, 800);

            Assert.Null(host.Snapshot(4).Primary);
        }
        finally
        {
            try { folder.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private static bool Shows(ActivityHost host, bool alert)
    {
        var snapshot = host.Snapshot(4);
        return snapshot.Primary is { ModuleId: "downloads", Alert: var isAlert } && isAlert == alert
            || snapshot.Sides.Any(a => a.ModuleId == "downloads" && a.Alert == alert);
    }

    /// <summary>Runs the dispatcher queue so the module's posted work executes, without a real app.</summary>
    private static bool Pump(Func<bool> until, int timeoutMs = 4000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (until()) return true;
            Thread.Sleep(25);
        }
        return until();
    }
}

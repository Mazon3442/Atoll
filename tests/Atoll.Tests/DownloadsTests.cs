using Atoll.Activities;
using Atoll.Modules.Downloads;
using Atoll.Shell;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public sealed class DownloadsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "atoll-downloads-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    // ---- folder resolution -------------------------------------------------------------------

    [Fact]
    public void Resolve_asks_the_shell_for_the_real_Downloads_folder()
    {
        Directory.CreateDirectory(_directory);
        var asked = Guid.Empty;

        var resolved = DownloadFolder.Resolve(id =>
        {
            asked = id;
            return _directory;
        });

        Assert.Equal(new Guid("374DE290-123F-4565-9164-39C4925E467B"), asked);
        Assert.Equal(_directory, resolved);
    }

    [Fact]
    public void Resolve_ignores_a_folder_that_is_not_there_and_a_shell_that_fails()
    {
        Assert.Null(DownloadFolder.Resolve(_ => Path.Combine(_directory, "gone")));
        Assert.Null(DownloadFolder.Resolve(_ => null));
        Assert.Null(DownloadFolder.Resolve(_ => throw new InvalidOperationException("no shell")));
    }

    [Fact]
    public void Resolve_uses_the_shell_folder_so_one_drive_redirection_works()
    {
        // There is no %USERPROFILE%\Downloads fallback anywhere: the shell's answer is the only source.
        Assert.Equal(DownloadFolder.FromShell(), DownloadFolder.Resolve());
    }

    // ---- which files start a download --------------------------------------------------------

    [Theory]
    [InlineData("report.pdf.crdownload")]
    [InlineData("movie.mp4.part")]
    [InlineData("archive.zip.partial")]
    [InlineData("setup.exe.download")]
    [InlineData("file.opdownload")]
    [InlineData("notes.txt.tmp")]
    [InlineData("REPORT.PDF.CRDOWNLOAD")]
    public void Browser_temp_names_start_a_download(string name) => Assert.True(DownloadsTracker.IsTemporary(name));

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("movie.mp4")]
    [InlineData("archive.tmp.zip")]
    [InlineData("notes.txt")]
    [InlineData("crdownload")]
    [InlineData("")]
    public void Ordinary_files_do_not(string name) => Assert.False(DownloadsTracker.IsTemporary(name));

    // ---- the completion state machine --------------------------------------------------------

    [Fact]
    public void A_temp_file_tracks_under_the_name_it_will_finish_with()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");

        Assert.True(tracker.Start(temp, 1_024, Start));
        var item = Assert.Single(tracker.Active);
        Assert.Equal("report.pdf", item.Name);
        Assert.Equal(1_024, item.Bytes);
        Assert.True(tracker.IsTracking(temp));

        // Files that are not browser temp names never join, however they are named.
        Assert.False(tracker.Start(Path.Combine(_directory, "copy.pdf"), 1_024, Start));
        Assert.Single(tracker.Active);
    }

    [Fact]
    public void The_browser_rename_to_the_final_name_completes_the_download()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        var final = Path.Combine(_directory, "report.pdf");
        tracker.Start(temp, 2_000_000, Start);
        Assert.True(tracker.Update(temp, 45_000_000, Start.AddSeconds(3)));

        var done = tracker.Rename(temp, final, 45_000_000, Start.AddSeconds(4));

        Assert.NotNull(done);
        Assert.Equal(final, done.Path);
        Assert.Equal("report.pdf", done.Name);
        Assert.Equal(45_000_000, done.Bytes);
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void A_rename_to_another_name_stops_tracking_without_completing()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        tracker.Start(temp, 1_024, Start);

        Assert.Null(tracker.Rename(temp, Path.Combine(_directory, "unrelated.pdf"), 1_024, Start.AddSeconds(1)));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void Deleted_then_created_with_the_same_stem_completes_within_ten_seconds()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        var final = Path.Combine(_directory, "report.pdf");
        tracker.Start(temp, 900_000, Start);
        Assert.True(tracker.Deleted(temp, Start.AddSeconds(1)));

        var done = tracker.Created(final, 1_200_000, Start.AddSeconds(5));

        Assert.NotNull(done);
        Assert.Equal(final, done.Path);
        Assert.Equal(1_200_000, done.Bytes);
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void Deleted_then_created_later_than_ten_seconds_alerts_nothing()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        tracker.Start(temp, 900_000, Start);
        tracker.Deleted(temp, Start.AddSeconds(1));

        Assert.Null(tracker.Created(Path.Combine(_directory, "report.pdf"), 1_200_000, Start.AddSeconds(12)));
        tracker.Tick(Start.AddSeconds(20));
        Assert.Null(tracker.Created(Path.Combine(_directory, "report.pdf"), 1_200_000, Start.AddSeconds(21)));
    }

    [Fact]
    public void Deleted_then_created_with_another_name_alerts_nothing()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        tracker.Start(temp, 900_000, Start);
        tracker.Deleted(temp, Start.AddSeconds(1));

        Assert.Null(tracker.Created(Path.Combine(_directory, "other.pdf"), 1_200_000, Start.AddSeconds(2)));
    }

    [Fact]
    public void A_file_that_simply_appears_or_is_renamed_is_never_a_completion()
    {
        var tracker = new DownloadsTracker();
        var final = Path.Combine(_directory, "unzipped.pdf");

        // A copy, an unzip, or a rename that is not a tracked temp name: nothing to announce.
        Assert.Null(tracker.Created(final, 1_024, Start));
        Assert.Null(tracker.Rename(Path.Combine(_directory, "elsewhere.pdf"), final, 1_024, Start.AddSeconds(1)));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void An_abandoned_temp_file_stops_tracking_after_the_idle_timeout()
    {
        var tracker = new DownloadsTracker();
        var temp = Path.Combine(_directory, "report.pdf.crdownload");
        tracker.Start(temp, 10, Start);

        Assert.False(tracker.Tick(Start.AddMinutes(14)));
        Assert.Single(tracker.Active);
        Assert.True(tracker.Tick(Start.AddMinutes(16)));
        Assert.Empty(tracker.Active);
    }

    // ---- size throttling ---------------------------------------------------------------------

    [Fact]
    public void Size_updates_wait_for_half_a_megabyte_or_one_percent_and_two_seconds()
    {
        var time = new FakeTimeProvider(Start);
        var gate = new SizeUpdateGate(time);

        Assert.True(gate.ShouldPush(45_000_000));   // the first size always shows
        Assert.False(gate.ShouldPush(60_000_000));  // no matter the jump, less than 2 s later
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(gate.ShouldPush(45_100_000));  // +0.1 MB and +0.2%: not worth a redraw
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(gate.ShouldPush(45_600_000));   // +0.5 MB
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(gate.ShouldPush(45_700_000));  // +0.1 MB and +0.2%
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(gate.ShouldPush(46_257_000));   // +1.4% of the last shown size
    }

    [Fact]
    public void Small_files_still_move_by_one_percent()
    {
        var time = new FakeTimeProvider(Start);
        var gate = new SizeUpdateGate(time);
        Assert.True(gate.ShouldPush(100_000));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(gate.ShouldPush(101_500));      // +1.5%, well under half a megabyte

        gate.Reset();
        Assert.True(gate.ShouldPush(101_500));      // a fresh download shows its first size at once
    }

    // ---- what the island shows ---------------------------------------------------------------

    [Fact]
    public void Several_downloads_share_one_line()
    {
        var vm = new DownloadsViewModel();
        var size = 45L * 1024 * 1024;

        vm.Update("report.pdf", 1, size);
        Assert.Equal("report.pdf", vm.Title);
        Assert.Equal("45.0 MB", vm.SizeText);
        Assert.Equal("report.pdf · 45.0 MB", vm.Summary);
        Assert.False(vm.IsSeveral);

        vm.Update("", 2, size);
        Assert.Equal("2 downloading", vm.Title);
        Assert.Equal("2 downloading · 45.0 MB", vm.Summary);
        Assert.Equal("Downloading · 45.0 MB", vm.Detail);
        Assert.True(vm.IsSeveral);
    }

    [Fact]
    public void The_completion_alert_carries_the_final_name_and_size()
    {
        var vm = new DownloadCompleteViewModel(@"C:\Users\me\Downloads\report.pdf", 45L * 1024 * 1024);

        Assert.Equal("report.pdf", vm.Name);
        Assert.Equal("45.0 MB", vm.SizeText);
        Assert.Equal("Downloaded · 45.0 MB", vm.Detail);
    }

    [Fact]
    public void The_side_activity_is_a_dot_with_the_download_icon_and_the_live_size()
    {
        var vm = new DownloadsViewModel();
        vm.Update("report.pdf", 1, 45L * 1024 * 1024);
        using var chip = new SideChipViewModel(
            new Activity("downloads.active", "downloads", vm, ActivityPriority.Ambient),
            System.Windows.Media.Colors.White);
        chip.SetWide(false);

        Assert.Equal("download", chip.Glyph);
        Assert.False(chip.IsRing);
        Assert.False(chip.ShowLabel); // one or two activities are round dots
        Assert.Contains("45.0 MB", chip.Label);

        // A changed size redraws the dot's label without re-posting the activity.
        vm.Update("report.pdf", 1, 60L * 1024 * 1024);
        Assert.Contains("60.0 MB", chip.Label);
    }
}

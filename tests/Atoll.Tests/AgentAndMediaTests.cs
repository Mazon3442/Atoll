using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atoll.Claude;
using Atoll.Modules.Claude;
using Atoll.Modules.Media;
using Atoll.Modules.Stats;
using Atoll.Settings;
using NAudio.Wave;

namespace Atoll.Tests;

public class AgentAndMediaTests
{
    [Fact]
    public void Island_answers_become_claude_permission_decisions()
    {
        Assert.Contains("\"behavior\":\"allow\"", HookProtocol.DecisionOutput("allow"));
        Assert.Contains("\"behavior\":\"deny\"", HookProtocol.DecisionOutput("deny"));
        Assert.Null(HookProtocol.DecisionOutput(null)); // the terminal asks
        Assert.Null(HookProtocol.DecisionOutput("rm -rf")); // anything else is not an answer
        using var doc = JsonDocument.Parse(HookProtocol.DecisionOutput("allow")!);
        Assert.Equal("PermissionRequest", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
    }

    [Fact]
    public async Task Pipe_returns_the_island_answer_to_a_waiting_hook()
    {
        var name = "atoll-test-" + Guid.NewGuid().ToString("N");
        using var server = new HookPipeServer(name) { Decide = m => Task.FromResult<string?>(m.Tool == "Bash" ? "allow" : null) };
        var plain = new TaskCompletionSource<HookMessage>();
        server.Received += m => plain.TrySetResult(m);
        server.Start();

        async Task<string?> Send(HookMessage message)
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(message, HookProtocol.Json));
            pipe.WriteByte((byte)'\n');
            await pipe.FlushAsync();
            if (!message.WantsReply) return null;
            return await new StreamReader(pipe, Encoding.UTF8).ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("allow", await Send(new HookMessage { Event = "PermissionRequest", Tool = "Bash", WantsReply = true }));
        Assert.Equal("", await Send(new HookMessage { Event = "PermissionRequest", Tool = "Write", WantsReply = true }));
        await Send(new HookMessage { Event = "PreToolUse", SessionId = "s1" }); // one-way messages still arrive as before
        Assert.Equal("s1", (await plain.Task.WaitAsync(TimeSpan.FromSeconds(5))).SessionId);
    }

    [Fact]
    public void Shell_messages_carry_the_command()
    {
        using var doc = JsonDocument.Parse("""{"hook_event_name":"CommandFinished","session_id":"42","command":"npm test\n--watch","notification_type":"1","message":"1m 5s"}""");
        var m = HookProtocol.FromHookInput(doc.RootElement, 0, "shell");
        Assert.Equal("npm test", m.Detail);
        Assert.Equal("1", m.NotificationType);
        Assert.Equal("PowerShell", HookProtocol.AgentName("shell"));
    }

    [Fact]
    public async Task PowerShell_integration_keeps_the_rest_of_the_profile()
    {
        var home = Path.Combine(Path.GetTempPath(), "atoll-shell-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(home, "Atoll.ClaudeHook.exe");
        Directory.CreateDirectory(home);
        File.WriteAllText(exe, "");
        try
        {
            var installer = new AgentIntegrationInstaller(home, exe);
            var profile = installer.ShellProfiles.First();
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.WriteAllText(profile, "Set-Alias g git");
            Assert.False(installer.IsInstalled("shell"));

            Assert.Null(await installer.InstallAsync("shell"));
            Assert.True(installer.IsInstalled("shell"));
            Assert.Null(await installer.InstallAsync("shell")); // twice adds nothing
            Assert.Single(File.ReadAllLines(profile), l => l.Contains("atoll-commands.ps1"));
            Assert.Contains($"& '{exe}' --agent shell", File.ReadAllText(installer.PathFor("shell")));

            Assert.Null(await installer.RemoveAsync("shell"));
            Assert.Equal(["Set-Alias g git"], File.ReadAllLines(profile));
            Assert.All(installer.ShellProfiles.Where(File.Exists), p => Assert.DoesNotContain("atoll", File.ReadAllText(p)));
            Assert.False(File.Exists(installer.PathFor("shell")));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Reads_claude_and_codex_plan_limits()
    {
        using var claude = JsonDocument.Parse("""{"five_hour":{"utilization":42.5,"resets_at":"2026-11-08T15:00:00+00:00"},"seven_day":{"utilization":7,"resets_at":null},"seven_day_opus":null}""");
        var limits = PlanLimits.ParseClaude(claude.RootElement);
        Assert.Equal(["Claude · 5-hour", "Claude · weekly"], limits.Select(l => l.Name));
        Assert.Equal(42.5, limits[0].Percent);
        Assert.Null(limits[1].ResetsAt);

        var codex = PlanLimits.ParseCodex("""{"timestamp":"2026-11-08T10:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":12,"window_minutes":300,"resets_in_seconds":3600},"secondary":{"used_percent":80,"window_minutes":10080,"resets_at":1762700000}}}}""");
        Assert.Equal(["Codex · 5-hour", "Codex · weekly"], codex.Select(l => l.Name));
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T11:00:00Z"), codex[0].ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762700000), codex[1].ResetsAt);
        Assert.Empty(PlanLimits.ParseCodex("not json"));
    }

    [Fact]
    public void Limit_rows_count_down_and_clear_after_the_reset()
    {
        var now = DateTimeOffset.Parse("2026-11-08T10:00:00Z");
        var row = new PlanLimitRow(new PlanLimit("Claude · 5-hour", 85, now.AddMinutes(90)));
        row.Refresh(now);
        Assert.Equal("85% used", row.PercentText);
        Assert.Equal("resets in 1h 30m", row.ResetText);
        Assert.True(row.IsHigh);
        row.Refresh(now.AddHours(2));
        Assert.Equal(0, row.Progress);
        Assert.Equal("reset", row.ResetText);
    }

    [Fact]
    public void Synced_lyrics_follow_the_song()
    {
        var lines = Lyrics.Parse("[ar:Someone]\n[00:05.20]First line\n[00:10.00][01:00.5]Chorus\n[00:20.00]\n");
        Assert.Equal(4, lines.Count);
        Assert.Equal("", Lyrics.LineAt(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal("First line", Lyrics.LineAt(lines, TimeSpan.FromSeconds(6)));
        Assert.Equal("Chorus", Lyrics.LineAt(lines, TimeSpan.FromSeconds(12)));
        Assert.Equal("♪", Lyrics.LineAt(lines, TimeSpan.FromSeconds(30)));
        Assert.Equal("Chorus", Lyrics.LineAt(lines, TimeSpan.FromSeconds(61)));
        Assert.Equal(-1, Lyrics.IndexAt(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, Lyrics.IndexAt(lines, TimeSpan.FromSeconds(12)));

        // LRC's positive offset makes lyrics appear sooner; negative delays them, and times never go below zero.
        var earlier = Lyrics.Parse("[offset:+500]\n[00:10.00]Line\n[00:20.00]Next");
        Assert.Equal(TimeSpan.FromSeconds(9.5), earlier[0].At);
        Assert.Equal(TimeSpan.FromSeconds(19.5), earlier[1].At);
        var later = Lyrics.Parse("[offset:-2000]\n[00:01.00]Line");
        Assert.Equal(TimeSpan.FromSeconds(3), later[0].At);
        var clamped = Lyrics.Parse("[offset:5000]\n[00:01.00]Line");
        Assert.Equal(TimeSpan.Zero, clamped[0].At);

        using var results = JsonDocument.Parse("""[{"duration":250,"syncedLyrics":null},{"duration":300,"syncedLyrics":"[00:01.00]live"},{"duration":201,"syncedLyrics":"[00:01.00]album"}]""");
        Assert.Equal("[00:01.00]album", Lyrics.Pick(results.RootElement, TimeSpan.FromSeconds(200)));
        Assert.Null(Lyrics.Pick(results.RootElement, TimeSpan.FromSeconds(100))); // no version of this length
    }

    [Fact]
    public void Visualizer_reads_integer_audio_too()
    {
        Assert.Equal(32, AudioLevels.SampleBits(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.Equal(16, AudioLevels.SampleBits(new WaveFormat(44100, 16, 2)));
        Assert.Equal(24, AudioLevels.SampleBits(new WaveFormat(48000, 24, 2)));
        Assert.Equal(0, AudioLevels.SampleBits(new WaveFormat(8000, 8, 1)));
        Assert.Equal(-1f, AudioLevels.ReadSample([0x00, 0x80], false, 16));
        Assert.Equal(0.5f, AudioLevels.ReadSample([0x00, 0x00, 0x40], false, 24));
        Assert.Equal(0.25f, AudioLevels.ReadSample(BitConverter.GetBytes(0.25f), true, 32));
    }
}

/// <summary>Runs on the shared WPF thread (see MediaSeekTests): renders the new agent, media and system views.</summary>
internal static class AgentMediaUi
{
    internal static void Verify()
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        resources.Add(new ResourceDictionary { Source = new Uri("/Atoll;component/Modules/Claude/ClaudeTemplates.xaml", UriKind.Relative) });
        resources.Add(new ResourceDictionary { Source = new Uri("/Atoll;component/Updates/UpdateTemplates.xaml", UriKind.Relative) });
        resources.Add(new StatsTemplates());

        var session = new ClaudeSessionViewModel("s") { Project = "atoll", Detail = "Running dotnet test", Hwnd = 1 };
        var ask = new ClaudeAlertViewModel(session, "atoll needs you", "Claude needs your permission to use Bash", _ => { });
        Assert.False(ask.CanOpen);
        Render(ask, "ClaudeAlertViewModel.Peek", "permission-peek", 620);
        var shell = new ClaudeSessionViewModel("shell:1") { Agent = "shell", Project = "atoll", Hwnd = 1 };
        Render(new ClaudeAlertViewModel(shell, "Failed · exit 1", "dotnet test · 1m 5s"), "ClaudeAlertViewModel.Peek", "command-peek", 520);

        var claude = new ClaudeViewModel(new ClaudeSessionTracker(TimeProvider.System));
        var now = DateTimeOffset.Now;
        claude.Usage.SetLimits([new("Claude · 5-hour", 42, now.AddMinutes(95)), new("Claude · weekly", 83, now.AddDays(3)),
            new("Codex · 5-hour", 5, now.AddHours(4)), new("Codex · weekly", 21, now.AddDays(5))], now);
        Render(claude, "ClaudeViewModel.Panel", "limits-panel", 560, 220);
        claude.SectionIndex = 1;
        Render(claude, "ClaudeViewModel.Panel", "agents-ask-empty", 640, 300);
        var askInput = FindAskInput(claude);
        // Enter sends; Shift+Enter does not match the gesture and falls through to the box's own newline.
        Assert.True(askInput.AcceptsReturn);
        var gesture = Assert.Single(askInput.InputBindings.OfType<System.Windows.Input.KeyBinding>());
        Assert.Equal(System.Windows.Input.Key.Enter, gesture.Key);
        Assert.Equal(System.Windows.Input.ModifierKeys.None, gesture.Modifiers);
        claude.Ask.Draft = "Summarize the failing test";
        claude.Ask.Provider = "Claude Code";
        var swatch = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : (i / 4 % 64) * 4)).ToArray(), 64 * 4);
        claude.Ask.AddImage(AskPaste.Normalize(swatch)!);
        claude.Ask.AddImage(AskPaste.Normalize(swatch)!);
        Render(claude, "ClaudeViewModel.Panel", "agents-ask-images", 640, 340);
        claude.Ask.Images.Clear();
        claude.Ask.IsRunning = true;
        Render(claude, "ClaudeViewModel.Panel", "agents-ask-thinking", 640, 340);
        claude.Ask.IsRunning = false;
        claude.Ask.Answer = "The test compares two timestamps and the expected value is one day behind.";
        Render(claude, "ClaudeViewModel.Panel", "agents-ask", 640, 340);
        Render(new Atoll.Updates.UpdatePromptViewModel("1.4.0", () => { }, () => { }), "UpdatePromptViewModel.Peek", "update-ready-peek", 620);
        Render(new AskAlertViewModel(claude.Ask, "Answer from Claude Code", "The test compares two timestamps…"),
            "AskAlertViewModel.Peek", "ask-answer-peek", 620);

        var media = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask)
        { HasSession = true, HasTimeline = true, Title = "Song", Artist = "Artist", Lyric = "And the words appear in time", HasLyrics = true, PositionText = "1:02", DurationText = "3:30", Progress = 0.3 };
        media.SetLyrics(["I will fold one more for you", "♪", "And the words appear in time", "two lost waves now one", "keep the light at the top"]);
        media.LyricIndex = 2; Assert.True(media.ShowLyrics);
        Assert.True(media.LyricLines[2].IsCurrent);
        media.Sources.Add(new("media:spotify", "Spotify")); media.Follow("media:spotify");
        media.SetOutputs([new("a", "Speakers (Realtek Audio)"), new("b", "Headphones (MX Sound)")], "b");
        Assert.True(media.HasOutputs);
        media.CanShuffle = true; media.IsShuffling = true; media.CanRepeat = true; media.Repeat = MediaRepeat.One;
        Render(media, "MediaViewModel.Panel", "media-panel", 600, 180);

        // Bongo Cat: off by default, and it takes the lyrics slot only while lyrics are off.
        Assert.False(media.ShowBongoCat);
        media.ShowLyrics = false; media.HasLyrics = false;
        Assert.False(media.ShowBongoCat);
        media.BongoCat = BongoCatStyle.Classic; media.IsPlaying = true;
        Assert.True(media.ShowBongoCat);
        Render(media, "MediaViewModel.Panel", "media-bongo-cat", 600, 180);
        media.BongoCat = BongoCatStyle.Excited; media.IsPlaying = false;
        Render(media, "MediaViewModel.Panel", "media-bongo-cat-excited-paused", 600, 180);
        media.ShowLyrics = true;
        Assert.False(media.ShowBongoCat);
        Assert.False(new AppSettings().BongoCat != BongoCatStyle.Off);

        var stats = new StatsViewModel();
        stats.Devices.Add(new DeviceBattery("MX Anywhere 2S", 90));
        stats.Devices.Add(new DeviceBattery("Xbox Wireless Controller", 19));
        Render(stats, "StatsViewModel.Panel", "stats-batteries", 560, 200);

        var game = new Atoll.Modules.Games.GameViewModel { IsPlaying = true, GameName = "Minecraft", GameDetail = "1.21.4 · Multiplayer · play.example.net", PlayTime = "1h 05m",
            Stats = "CPU 34%  GPU 71%  64°C", HasServer = true, ServerAddress = "play.example.net", ServerOnline = true, ServerStatus = "3/20 online · 42 ms · Paper 1.21.4", ServerMotd = "Hello world" };
        game.Library.Add(new("Minecraft Launcher", "Minecraft", "x")); game.Library.Add(new("Dota 2", "Steam", "x")); game.HasLibrary = true;
        Render(game, "GameViewModel.Compact", "game-overlay", 420, 24);
        Render(game, "GameViewModel.Panel", "games-panel", 640, 210);
        var launcher = new Atoll.Modules.Launcher.LauncherViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        launcher.SetAppsForTest([
            new("Visual Studio Code", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Visual Studio Code.lnk", "Development"),
            new("Spotify", @"C:\Users\mason\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Spotify.lnk", "Music"),
            new("Steam", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Steam.lnk", "Games"),
        ]);
        Render(new Atoll.Modules.LauncherPanelViewModel(launcher, game), "LauncherPanelViewModel.Panel", "launcher-panel", 640, 360);
        var timer = new Atoll.Modules.Clock.TimerViewModel(TimeProvider.System);
        var calendar = new Atoll.Modules.Calendar.CalendarViewModel(() => { }) { HasSources = true, HasEvents = true };
        var today = DateTime.Today;
        calendar.SelectedDate = today;
        calendar.VisibleMonth = new DateTime(today.Year, today.Month, 1);
        calendar.MonthCells = Atoll.Modules.Calendar.CalendarViewModel.BuildMonth(calendar.VisibleMonth, today, today,
            new Dictionary<DateTime, Atoll.Modules.Calendar.CalendarDayMark>
            {
                [today] = new(2, ["#0A84FF", "#30D158"]),
                [today.AddDays(3)] = new(1, ["#FF9F0A"]),
            });
        var eventStart = DateTimeOffset.Now.AddHours(1);
        calendar.Days.Add(new Atoll.Modules.Calendar.CalendarDay
        {
            Label = "Today",
            Events =
            [
                new(new Atoll.Modules.Calendar.CalendarEventItem("render", eventStart, eventStart.AddHours(1), false,
                    "Design review", "Studio 2", "Work", "#0A84FF"), _ => { }, _ => { }),
            ],
        });
        var home = new Atoll.Modules.Home.HomeViewModel(media, null, new Atoll.Modules.Clock.ClockPanelViewModel(new Atoll.Modules.Clock.ClockViewModel(), timer),
            calendar, game, new HomeSettings());
        Render(home, "HomeViewModel.Panel", "home-panel", 640, 300);

        var notesSettings = new NotesSettings();
        notesSettings.Notes.Add(new NoteItem { Text = "Ship the calendar update", Updated = DateTimeOffset.Now.AddMinutes(-20) });
        notesSettings.Reminders.Add(new ReminderItem { Title = "Standup", Due = DateTimeOffset.Now.AddMinutes(10) });
        notesSettings.Todos.Add(new TodoItem { Text = "Review release notes" });
        notesSettings.Todos.Add(new TodoItem { Text = "Reply to Sam", Done = true });
        var notes = new Atoll.Modules.Notes.NotesViewModel(notesSettings, () => { });
        Render(notes, "NotesViewModel.Panel", "notes-panel", 640, 380);
        notes.SectionIndex = 1;
        Render(notes, "NotesViewModel.Panel", "notes-reminders", 640, 380);
        notes.SectionIndex = 2;
        Render(notes, "NotesViewModel.Panel", "notes-todos", 640, 380);
    }

    private static System.Windows.Controls.TextBox FindAskInput(object vm)
    {
        var view = (FrameworkElement)((DataTemplate)Application.Current.Resources["ClaudeViewModel.Panel"]).LoadContent();
        var host = new System.Windows.Controls.Border { Child = view, DataContext = vm };
        host.Measure(new Size(640, 420));
        host.Arrange(new Rect(0, 0, 640, 420));
        host.UpdateLayout();
        return Find<System.Windows.Controls.TextBox>(host, "Ask a question")
            ?? throw new InvalidOperationException("the Ask box is missing");

        static T? Find<T>(DependencyObject root, string name) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match && System.Windows.Automation.AutomationProperties.GetName(match) == name) return match;
                if (Find<T>(child, name) is { } nested) return nested;
            }
            return null;
        }
    }

    private static void Render(object vm, string key, string name, double width, double height = 60)
    {
        var view = (FrameworkElement)((DataTemplate)Application.Current.Resources[key]).LoadContent();
        var host = new System.Windows.Controls.Border { Background = (Brush)Application.Current.Resources["Island.Background"], Padding = new Thickness(14), Child = view, DataContext = vm };
        System.Windows.Documents.TextElement.SetForeground(host, (Brush)Application.Current.Resources["Island.Foreground"]); // the island window sets this
        host.Measure(new Size(width, height + 28));
        host.Arrange(new Rect(0, 0, width, height + 28));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)(height + 28), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(Path.GetTempPath(), "atoll-tier-two-three-review");
        Directory.CreateDirectory(dir);
        using var output = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(output);
    }
}

public class HomeAndGamesTests
{
    [Fact]
    public void Every_icon_parses() =>
        Assert.All(Atoll.Shell.Icon.Paths.Keys, kind => Assert.NotNull(Atoll.Shell.Icon.Geometry(kind)));

    [Fact]
    public void Month_grid_is_six_weeks_with_today_and_event_days()
    {
        var marks = new Dictionary<DateTime, Atoll.Modules.Calendar.CalendarDayMark>
        {
            [new DateTime(2026, 11, 12)] = new(1, ["#0A84FF"]),
        };
        var cells = Atoll.Modules.Calendar.CalendarViewModel.BuildMonth(
            new DateTime(2026, 11, 1), new DateTime(2026, 11, 9), new DateTime(2026, 11, 9), marks);
        Assert.Equal(42, cells.Count);
        Assert.Single(cells, c => c.IsToday && c.Date.Day == 9 && c.IsSelected);
        Assert.Single(cells, c => c.HasEvents && c.Date.Day == 12 && c.InMonth);
        Assert.Equal(30, cells.Count(c => c.InMonth));
    }

    [Fact]
    public void Launcher_filters_apps_by_name_and_source()
    {
        var vm = new Atoll.Modules.Launcher.LauncherViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        vm.SetAppsForTest([
            new("Visual Studio Code", @"C:\Start Menu\Visual Studio Code.lnk", "Development"),
            new("Spotify", @"C:\Start Menu\Spotify.lnk", "Music"),
            new("Steam", @"C:\Start Menu\Steam.lnk", "Games"),
        ]);
        Assert.Equal(3, vm.FilteredApps.Count);
        Assert.True(vm.HasApps);

        vm.SearchText = "studio";
        Assert.Equal("Visual Studio Code", Assert.Single(vm.FilteredApps).Name);
        vm.SearchText = "music";
        Assert.Equal("Spotify", Assert.Single(vm.FilteredApps).Name);
        vm.SearchText = "nothing here";
        Assert.Empty(vm.FilteredApps);
        Assert.True(vm.ShowEmpty);
    }

    [Fact]
    public void Reads_steam_manifests_and_skips_tools()
    {
        var game = Atoll.Modules.Games.GameLibrary.ParseAcf("\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"installdir\" \"dota 2 beta\" \"LastPlayed\" \"1762600000\" }", @"D:\Steam\steamapps");
        Assert.Equal("Dota 2", game!.Name);
        Assert.Equal("steam://rungameid/570", game.Launch);
        Assert.Equal(@"D:\Steam\steamapps\common\dota 2 beta", game.InstallDir);
        Assert.Null(Atoll.Modules.Games.GameLibrary.ParseAcf("\"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\"", "x"));
    }

    [Fact]
    public void Reads_a_minecraft_server_status()
    {
        Assert.Equal(("play.example.net", 25565), Atoll.Modules.Games.MinecraftServer.ParseAddress(" play.example.net "));
        Assert.Equal(("10.0.0.5", 25570), Atoll.Modules.Games.MinecraftServer.ParseAddress("10.0.0.5:25570"));
        var status = Atoll.Modules.Games.MinecraftServer.Parse("""{"version":{"name":"Paper 1.21.4"},"players":{"online":3,"max":20},"description":{"text":"§aHello ","extra":[{"text":"world"}]}}""", 42);
        Assert.Equal((3, 20, "Paper 1.21.4", "Hello world", 42L), (status.Players, status.MaxPlayers, status.Version, status.Motd, status.PingMs));
    }

    [Fact]
    public void Finds_the_last_minecraft_server_in_the_log()
    {
        var log = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(log, ["[12:00:01] [Render thread/INFO]: Connecting to old.example.net, 25565", "[12:30:00] [Server Connector #2/INFO]: Connecting to play.example.net, 25565"]);
            Assert.Equal("play.example.net", Atoll.Modules.Games.GamesModule.LastServer(log));
        }
        finally { File.Delete(log); }
        Assert.Equal("1h 05m", Atoll.Modules.Games.GamesModule.Format(TimeSpan.FromMinutes(65)));
    }

    [Fact]
    public void Stats_tiles_fill_in_within_seconds()
    {
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var service = new SettingsService(new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
                using var host = new Atoll.Activities.ActivityHost();
                using var module = new StatsModule();
                module.Start(new Atoll.Modules.ModuleContext("stats", host, System.Windows.Threading.Dispatcher.CurrentDispatcher, service));
                var vm = (StatsViewModel)module.Panel!;
                var view = new System.Windows.Controls.Border();
                vm.Observe(view, true); // as the System tab does when it is on screen
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                if (vm.Cpu.Value == "—" || vm.Memory.Value == "—") failure = $"CPU {vm.Cpu.Value}, memory {vm.Memory.Value} after 3 s";
            }
            catch (Exception ex) { failure = ex.ToString(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}

public class GameDetectionTests
{
    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper32.exe", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\rocketleague\Binaries\Win64\RocketLeague.exe", false)]
    public void Steam_tools_are_not_games(string path, bool notAGame) => Assert.Equal(notAGame, Atoll.Shell.GameDetector.IsNotAGame(path));
}

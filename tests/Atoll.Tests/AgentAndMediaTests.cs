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

        var media = new MediaViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask)
        { HasSession = true, HasTimeline = true, Title = "Song", Artist = "Artist", Lyric = "And the words appear in time", HasLyrics = true, PositionText = "1:02", DurationText = "3:30", Progress = 0.3 };
        media.Sources.Add(new("media:spotify", "Spotify")); media.Follow("media:spotify");
        media.SetOutputs([new("a", "Speakers (Realtek Audio)"), new("b", "Headphones (MX Sound)")], "b");
        Assert.True(media.HasOutputs);
        Render(media, "MediaViewModel.Panel", "media-panel", 560, 200);

        var stats = new StatsViewModel();
        stats.Devices.Add(new DeviceBattery("MX Anywhere 2S", 90));
        stats.Devices.Add(new DeviceBattery("Xbox Wireless Controller", 19));
        Render(stats, "StatsViewModel.Panel", "stats-batteries", 560, 200);
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

using Atoll.Modules.Media;
using Atoll.Modules.Tide.Listen;

namespace Atoll.Tests;

public sealed class ListenTests
{
    [Fact]
    public void Resampler_produces_16k_from_48k_across_ragged_blocks()
    {
        var resampler = new StreamResampler(48_000);
        var input = new float[48_000]; // one second of a 440 Hz tone at half scale
        for (var i = 0; i < input.Length; i++) input[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48_000f);

        var output = new List<float>();
        for (var offset = 0; offset < input.Length;)
        {
            var count = Math.Min(offset % 7 + 101, input.Length - offset);
            resampler.Process(input.AsSpan(offset, count), output);
            offset += count;
        }

        Assert.InRange(output.Count, 15_995, 16_005);
        Assert.InRange(PcmAudio.Rms(output.ToArray()), 0.32f, 0.38f); // sine RMS is about 0.354
    }

    [Fact]
    public void Resampler_at_the_same_rate_keeps_every_sample()
    {
        var resampler = new StreamResampler(16_000);
        var input = new float[800];
        for (var i = 0; i < input.Length; i++) input[i] = i / 1000f;

        var output = new List<float>();
        resampler.Process(input.AsSpan(0, 300), output);
        resampler.Process(input.AsSpan(300), output);

        Assert.Equal(input.Length, output.Count);
        Assert.Equal(input[^1], output[^1], 4);
    }

    [Fact]
    public void Mixer_ducks_the_mic_when_the_call_is_loud()
    {
        var mic = Enumerable.Repeat(0.5f, FrameMixer.FrameSamples).ToArray();
        var quiet = new float[FrameMixer.FrameSamples];
        var call = Enumerable.Repeat(0.5f, FrameMixer.FrameSamples).ToArray();

        var alone = FrameMixer.Mix(mic, quiet);
        Assert.Equal(0.35f, alone.Samples[0], 3); // 0.5 * headroom, not clipped
        Assert.Equal(0.5f, alone.MicLevel, 3);
        Assert.Equal(0f, alone.SystemLevel, 3);

        var together = FrameMixer.Mix(mic, call);
        Assert.Equal((0.5f * FrameMixer.DuckGain + 0.5f) * 0.7f, together.Samples[0], 3);
        Assert.Equal(0.5f, together.SystemLevel, 3);
    }

    [Fact]
    public void Soft_clamp_rounds_loud_samples_instead_of_clipping_flat()
    {
        Assert.Equal(0.5f, FrameMixer.SoftClamp(0.5f));
        Assert.InRange(FrameMixer.SoftClamp(4f), 0.99f, 1f);
        Assert.InRange(FrameMixer.SoftClamp(-4f), -1f, -0.99f);
    }

    [Fact]
    public void Ring_drops_the_oldest_audio_instead_of_growing()
    {
        var ring = new AudioRing(8);
        ring.Write([1, 2, 3, 4, 5, 6]);
        var buffer = new float[8];
        Assert.Equal(6, ring.Read(buffer));
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, buffer[..6]);

        ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Equal(8, ring.Count);
        Assert.Equal(8, ring.Read(buffer));
        Assert.Equal(3f, buffer[0]); // the oldest samples made room
        Assert.Equal(10f, buffer[7]);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Vad_skips_quiet_windows_and_labels_the_speaker()
    {
        var window = new VadWindow();
        var silence = new AudioFrame(new float[FrameMixer.FrameSamples], 0.0005f, 0.0005f);
        var taken = false;
        for (var i = 0; i < 60 && !taken; i++)
        {
            window.Append(silence);
            taken = window.TryTake(out _, out _);
        }

        Assert.False(taken);
        Assert.Equal(VadWindow.OverlapSamples, window.Count); // only the overlap is kept

        var loud = Enumerable.Repeat(0.2f, FrameMixer.FrameSamples).ToArray();
        var you = false;
        float[] others = [];
        taken = false;
        for (var i = 0; i < 60 && !taken; i++)
        {
            window.Append(new AudioFrame(loud, 0.004f, 0.2f));
            taken = window.TryTake(out others, out you);
        }
        Assert.True(taken);
        Assert.Equal(VadWindow.WindowSamples, others.Length);
        Assert.False(you);
        Assert.Equal(VadWindow.OverlapSamples, window.Count);

        window.Clear();
        var self = false;
        taken = false;
        for (var i = 0; i < 60 && !taken; i++)
        {
            window.Append(new AudioFrame(loud, 0.2f, 0.004f));
            taken = window.TryTake(out _, out self);
        }
        Assert.True(taken);
        Assert.True(self);
    }

    [Fact]
    public void Notes_gate_waits_for_300_new_characters_or_45_seconds()
    {
        var now = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var gate = new NotesGate(now);
        Assert.False(gate.Due(299, now));
        Assert.True(gate.Due(300, now));

        gate.Mark(300, now);
        Assert.False(gate.Due(350, now));
        Assert.False(gate.Due(350, now.AddSeconds(44)));
        Assert.True(gate.Due(350, now.AddSeconds(45)));
        Assert.True(gate.Due(600, now.AddSeconds(1)));
    }

    [Fact]
    public void Notes_json_is_parsed_from_fences_and_loose_shapes()
    {
        var result = ListenNotesResult.Parse("""
            Here is the result:
            ```json
            {"summary":"Ship Friday.","action_items":["Send the deck",{"text":"Draft notes","owner":"Sam"}],"replies":[{"text":"I can own that.","tone":"helpful"},"Sounds good"]}
            ```
            """);

        Assert.NotNull(result);
        Assert.Equal("Ship Friday.", result!.Summary);
        Assert.Equal(2, result.ActionItems.Count);
        Assert.Equal("Send the deck", result.ActionItems[0].Text);
        Assert.Equal("Sam", result.ActionItems[1].Owner);
        Assert.Equal("helpful", result.Replies[0].Tone);
        Assert.Equal("Sounds good", result.Replies[1].Text);

        Assert.Null(ListenNotesResult.Parse("no json here"));
        Assert.Null(ListenNotesResult.Parse("""{"summary":""}"""));
    }

    [Fact]
    public void Listen_session_round_trips_through_its_json_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atoll-listen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ListenStore(directory);
            var started = new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.FromHours(-6));
            var session = new ListenSession
            {
                Started = started,
                Title = "Standup",
                Notes = "ask about the deploy",
                Summary = "Ship on Friday.",
            };
            session.Lines.Add(new ListenLine { Speaker = "Others", At = started.AddSeconds(4), Text = "We ship Friday." });
            session.Lines.Add(new ListenLine { Speaker = "You", At = started.AddSeconds(9), Text = "I can take the note." });
            session.ActionItems.Add(new ListenActionItem { Text = "Draft the note", Owner = "Sam" });
            session.Replies.Add(new ListenReply { Text = "I can take that.", Tone = "helpful" });

            var path = store.Save(session);
            Assert.True(File.Exists(path));

            var loaded = Assert.Single(store.Recent());
            Assert.Equal(started, loaded.Started);
            Assert.Equal("Standup", loaded.Title);
            Assert.Equal("ask about the deploy", loaded.Notes);
            Assert.Equal("Ship on Friday.", loaded.Summary);
            Assert.Equal(2, loaded.Lines.Count);
            Assert.Equal("You", loaded.Lines[1].Speaker);
            Assert.Equal("Sam", loaded.ActionItems[0].Owner);
            Assert.Equal("helpful", loaded.Replies[0].Tone);

            Assert.Null(ListenStore.Parse("not json"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Transcript_drops_non_speech_markers()
    {
        Assert.Equal("", ListenViewModel.CleanTranscript("[BLANK_AUDIO]"));
        Assert.Equal("", ListenViewModel.CleanTranscript("(upbeat music)"));
        Assert.Equal("Hello world.", ListenViewModel.CleanTranscript(" [BLANK_AUDIO] Hello   world. "));
    }
}

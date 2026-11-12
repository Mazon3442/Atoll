using Atoll.Modules.Media;
using Atoll.Settings;
using System.Windows;
using System.Windows.Threading;

namespace Atoll.Tests;

public class BongoCatTests
{
    [Fact]
    public void Cat_steps_frames_while_playing_and_freezes_when_paused() => OnSta(() =>
    {
        var cat = new BongoCatView { Variant = BongoCatStyle.Excited, Playing = false };
        using var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("BongoCatTest") { Width = 200, Height = 200 }) { RootVisual = cat };
        Pump(100);
        Assert.False(cat.IsRunning);
        Assert.Equal(0, cat.FrameIndex);

        cat.Playing = true;
        Pump(300);
        Assert.True(cat.IsRunning, $"loaded {cat.IsLoaded} visible {cat.IsVisible} anim {SystemParameters.ClientAreaAnimation}");
        Assert.NotEqual(0, cat.FrameIndex); // 30 ms frames: it has moved on by now

        cat.Playing = false;
        var frozen = cat.FrameIndex;
        Pump(300);
        Assert.False(cat.IsRunning);
        Assert.Equal(frozen, cat.FrameIndex);

        cat.Playing = true; // resumes from the same frame, not from the start
        Assert.True(cat.IsRunning);
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Bongo Cat test timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

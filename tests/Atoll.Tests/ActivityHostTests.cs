using Atoll.Activities;
using Microsoft.Extensions.Time.Testing;

namespace Atoll.Tests;

public class ActivityHostTests
{
    private readonly FakeTimeProvider _time = new();

    private static Activity Make(string id, string module, ActivityPriority p, TimeSpan? duration = null) =>
        new(id, module, new object(), p, duration);

    [Fact]
    public void Primary_is_highest_priority_then_newest()
    {
        using var host = new ActivityHost(_time);
        host.Post(Make("clock", "clock", ActivityPriority.Idle));
        host.Post(Make("a", "media", ActivityPriority.Normal));
        host.Post(Make("b", "claude", ActivityPriority.Normal));
        host.Post(Make("low", "stats", ActivityPriority.Ambient));

        var (primary, secondary) = host.Current;

        Assert.Equal("b", primary?.Id);
        Assert.Equal("a", secondary?.Id);
    }

    [Fact]
    public void Secondary_comes_from_another_module_and_is_never_idle()
    {
        using var host = new ActivityHost(_time);
        host.Post(Make("clock", "clock", ActivityPriority.Idle));
        host.Post(Make("m1", "media", ActivityPriority.High));
        host.Post(Make("m2", "media", ActivityPriority.Normal));

        var (primary, secondary) = host.Current;

        Assert.Equal("m1", primary?.Id);
        Assert.Null(secondary);
    }

    [Fact]
    public void Transient_activity_expires_after_its_duration()
    {
        using var host = new ActivityHost(_time);
        var changes = 0;
        host.Changed += (_, _) => changes++;
        host.Post(Make("clock", "clock", ActivityPriority.Idle));
        host.Post(Make("vol", "system", ActivityPriority.High, TimeSpan.FromSeconds(2)));
        Assert.Equal("vol", host.Current.Primary?.Id);

        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal("vol", host.Current.Primary?.Id);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal("clock", host.Current.Primary?.Id);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Reposting_same_id_restarts_duration()
    {
        using var host = new ActivityHost(_time);
        host.Post(Make("vol", "system", ActivityPriority.High, TimeSpan.FromSeconds(2)));
        _time.Advance(TimeSpan.FromSeconds(1.5));
        host.Post(Make("vol", "system", ActivityPriority.High, TimeSpan.FromSeconds(2)));
        _time.Advance(TimeSpan.FromSeconds(1.5));

        Assert.Equal("vol", host.Current.Primary?.Id);
    }

    [Fact]
    public void RemoveModule_drops_all_of_its_activities()
    {
        using var host = new ActivityHost(_time);
        host.Post(Make("a", "media", ActivityPriority.Normal));
        host.Post(Make("b", "media", ActivityPriority.High));
        host.Post(Make("c", "clock", ActivityPriority.Idle));

        host.RemoveModule("media");

        Assert.Equal("c", host.Current.Primary?.Id);
    }
}

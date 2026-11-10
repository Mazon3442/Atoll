using Atoll.Modules.Indicators;

namespace Atoll.Tests;

public sealed class BatteryTests
{
    [Fact]
    public void A_pc_without_a_battery_shows_no_battery_anywhere()
    {
        var panel = new SystemPanelViewModel(_ => { }, () => { });
        panel.BatterySupported = false;
        panel.ShowBattery = true;
        panel.Battery = "100% · plugged in"; // whatever a desktop reports if anything asks

        Assert.False(panel.HasBattery);
        Assert.False(panel.ShowBatteryChip);
    }

    [Fact]
    public void A_laptop_shows_the_chip_and_its_level()
    {
        var panel = new SystemPanelViewModel(_ => { }, () => { });
        panel.BatterySupported = true;
        panel.Battery = "80% · on battery";
        panel.ShowBattery = true;

        Assert.True(panel.HasBattery);
        Assert.True(panel.ShowBatteryChip);
        Assert.Equal("80%", panel.BatteryShort);

        panel.ShowBattery = false;
        Assert.False(panel.ShowBatteryChip);
    }
}

using Windows.Devices.Enumeration;

namespace Atoll.Modules.Stats;

public sealed record DeviceBattery(string Name, int Percent)
{
    public string Text => $"{Percent}%";
    public bool IsLow => Percent <= 20;
    public string Description => $"{Name}, battery {Percent} percent";
}

/// <summary>
/// Battery of connected Bluetooth devices, read the way Settings › Bluetooth shows it: the device node's battery
/// property, kept only for devices whose container is connected (the property outlives the connection).
/// </summary>
internal static class BluetoothBattery
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string BluetoothClass = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    public static async Task<IReadOnlyList<DeviceBattery>> ReadAsync()
    {
        // Containers and device nodes answer in milliseconds; paired-endpoint queries took 30-60 s.
        var connected = (await DeviceInformation.FindAllAsync("System.Devices.Connected:=System.StructuredQueryType.Boolean#True",
                ["System.Devices.Connected"], DeviceInformationKind.DeviceContainer))
            .Select(c => Guid.TryParse(c.Id, out var id) ? id : Guid.Empty).ToHashSet();
        var nodes = await DeviceInformation.FindAllAsync(BluetoothClass, [BatteryKey, ContainerKey], DeviceInformationKind.Device);
        return nodes
            .Select(n => (n.Name, Battery: n.Properties.TryGetValue(BatteryKey, out var b) ? b as byte? : null,
                Container: n.Properties.TryGetValue(ContainerKey, out var id) ? id as Guid? : null))
            .Where(n => n.Battery is not null && n.Container is { } c && connected.Contains(c))
            .GroupBy(n => n.Container!.Value) // a headset has several nodes (hands-free, audio) with the same level
            .Select(g => new DeviceBattery(g.First().Name, Math.Clamp((int)g.First().Battery!, 0, 100)))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

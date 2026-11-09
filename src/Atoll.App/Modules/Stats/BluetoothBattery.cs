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
/// property, kept only for devices whose paired endpoint is connected (the property outlives the connection).
/// </summary>
internal static class BluetoothBattery
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string BluetoothClass = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    public static async Task<IReadOnlyList<DeviceBattery>> ReadAsync()
    {
        var endpoints = await DeviceInformation.FindAllAsync("System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
            ["System.Devices.Aep.IsConnected", "System.Devices.Aep.ContainerId"], DeviceInformationKind.AssociationEndpoint);
        var connected = endpoints
            .Where(e => e.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true)
            .Select(e => (Container: e.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var id) ? id as Guid? : null, e.Name))
            .Where(e => e.Container is not null)
            .GroupBy(e => e.Container!.Value).ToDictionary(g => g.Key, g => g.First().Name);
        if (connected.Count == 0) return [];

        var nodes = await DeviceInformation.FindAllAsync(BluetoothClass, [BatteryKey, ContainerKey], DeviceInformationKind.Device);
        return nodes
            .Select(n => (Battery: n.Properties.TryGetValue(BatteryKey, out var b) ? b as byte? : null,
                Container: n.Properties.TryGetValue(ContainerKey, out var id) ? id as Guid? : null))
            .Where(n => n.Battery is not null && n.Container is { } c && connected.ContainsKey(c))
            .GroupBy(n => n.Container!.Value) // a headset has several nodes (hands-free, audio) with the same level
            .Select(g => new DeviceBattery(connected[g.Key], Math.Clamp((int)g.First().Battery!, 0, 100)))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

using PeriphHub.Core;

namespace PeriphHub.Drivers.Asus;

public sealed class AsusProvider : IDeviceProvider
{
    private bool _done;
    public string Name => "ASUS laptop";

    public async Task ScanAsync(DeviceHub hub)
    {
        if (_done) return;
        _done = true;
        var maker = AsusLaptopDevice.Wmi("Win32_ComputerSystem", "Manufacturer") ?? "";
        if (!maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) { Log.Info($"Not an ASUS laptop ({maker})"); return; }
        var model = AsusLaptopDevice.Wmi("Win32_ComputerSystem", "Model") ?? "ASUS laptop";
        var acpi = AtkAcpi.TryOpen();
        var aura = AuraHid.Find();
        Log.Info($"ASUS laptop: {model}, ACPI={(acpi != null)}, AuraHID={(aura != null)}");
        var dev = new AsusLaptopDevice(model, acpi, aura);
        hub.Add(dev);
        await dev.ApplySavedAsync();
        await dev.PollAsync();
    }

    public void Dispose() { }
}

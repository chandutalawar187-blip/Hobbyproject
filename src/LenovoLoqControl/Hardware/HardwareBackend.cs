using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class HardwareBackend : IHardwareBackend
{
    public IHardwareMonitor Monitor { get; }
    public IFanController FanController { get; }
    public IGpuOverclockController GpuOverclock { get; }
    public IKeyboardLightController KeyboardLight { get; }

    public HardwareBackend()
    {
        GpuOverclock = new NvidiaGpuOverclockController();
        KeyboardLight = new LenovoKeyboardLightController();
        Monitor = new WindowsHardwareMonitor(GpuOverclock is NvidiaGpuOverclockController nvidia
            ? nvidia.ReadCurrentGraphicsClockGhz
            : null);
        var lenovoWmi = new LenovoWmiFanController();
        var energyDrv = new EnergyDrvFanController();
        if (lenovoWmi.IsSupported)
        {
            FanController = new FallbackFanController(lenovoWmi, energyDrv);
            return;
        }

        FanController = energyDrv.IsSupported ? energyDrv : new UnsupportedFanController();
    }

    public void Dispose()
    {
        FanController.Dispose();
        GpuOverclock.Dispose();
        KeyboardLight.Dispose();
        if (!ReferenceEquals(Monitor, null))
            Monitor.Dispose();
    }
}

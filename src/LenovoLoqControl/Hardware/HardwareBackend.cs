using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class HardwareBackend : IHardwareBackend
{
    public IHardwareMonitor Monitor { get; }
    public IFanController FanController { get; }
    public IGpuOverclockController GpuOverclock { get; }

    public HardwareBackend()
    {
        GpuOverclock = new NvidiaGpuOverclockController();
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
        if (!ReferenceEquals(Monitor, null))
            Monitor.Dispose();
    }
}

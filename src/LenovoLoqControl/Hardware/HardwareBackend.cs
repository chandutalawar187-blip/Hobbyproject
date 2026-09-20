using LenovoLoqControl.Core;
using LenovoLoqControl.Services;

namespace LenovoLoqControl.Hardware;

public sealed class HardwareBackend : IHardwareBackend
{
    private readonly object _lifecycleLock = new();
    private readonly Lazy<NvidiaGpuOverclockController> _gpuOverclock = new(
        static () => new NvidiaGpuOverclockController());
    private bool _disposed;

    public IHardwareMonitor Monitor { get; }
    public IFanController FanController { get; }
    public IGpuOverclockController GpuOverclock
    {
        get
        {
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _gpuOverclock.Value;
            }
        }
    }
    public IKeyboardLightController KeyboardLight { get; }

    public HardwareBackend(bool useElevatedService = true)
    {
        KeyboardLight = new LenovoKeyboardLightController();
        var localMonitor = new WindowsHardwareMonitor(ReadGpuClock);
        var serviceFan = useElevatedService ? new ServiceFanController() : null;
        Monitor = serviceFan?.ServiceRunning == true
            ? new ServiceHardwareMonitor(localMonitor)
            : localMonitor;
        if (serviceFan?.IsSupported == true)
        {
            FanController = serviceFan;
            return;
        }

        serviceFan?.Dispose();
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
        NvidiaGpuOverclockController? gpuOverclock = null;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            FanController.Dispose();
            if (_gpuOverclock.IsValueCreated)
                gpuOverclock = _gpuOverclock.Value;
            KeyboardLight.Dispose();
        }

        gpuOverclock?.Dispose();
        Monitor.Dispose();
    }

    private double? ReadGpuClock()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return null;
            return _gpuOverclock.Value.ReadCurrentGraphicsClockGhz();
        }
    }
}

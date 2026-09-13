using System.ServiceProcess;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;

namespace LenovoLoqControlService;

internal static class Program
{
    private static void Main()
    {
        if (Environment.UserInteractive)
        {
            using var service = new LoqHardwareService();
            service.RunInteractive();
            return;
        }

        ServiceBase.Run(new LoqHardwareService());
    }
}

internal sealed class LoqHardwareService : ServiceBase
{
    private HardwareBackend? _hardware;
    private Timer? _timer;
    private int _running;
    private FanMode? _lastMode;

    public LoqHardwareService()
    {
        ServiceName = "LoqControlHardwareService";
        CanStop = true;
        CanPauseAndContinue = false;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _hardware = new HardwareBackend();
        _timer = new Timer(EnforceCurrentMode, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    protected override void OnStop()
    {
        _timer?.Dispose();
        _timer = null;
        _hardware?.FanController.RestoreSafeStateAsync(CancellationToken.None).GetAwaiter().GetResult();
        _hardware?.Dispose();
        _hardware = null;
    }

    internal void RunInteractive()
    {
        OnStart([]);
        Console.WriteLine("LoqControlHardwareService running. Press Enter to stop.");
        Console.ReadLine();
        OnStop();
    }

    private void EnforceCurrentMode(object? state)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            return;

        try
        {
            var hardware = _hardware;
            if (hardware is null || !hardware.FanController.IsSupported)
                return;

            var mode = hardware.FanController.GetCurrentModeAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            if (mode is FanMode.Quiet or FanMode.Auto or FanMode.Balanced or FanMode.Performance
                && mode != _lastMode)
            {
                hardware.FanController.SetFanModeAsync(mode.Value, CancellationToken.None)
                    .GetAwaiter().GetResult();
                _lastMode = mode;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.TimeoutException
                                   or UnauthorizedAccessException
                                   or System.Management.ManagementException)
        {
            // The service must remain alive if firmware temporarily rejects a poll.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

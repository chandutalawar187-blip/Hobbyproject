using System.Runtime.InteropServices;
using System.Management;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class LenovoWmiFanController : IFanController
{
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ILenovoWmiOperations _operations;
    private readonly bool _isSupported;

    public LenovoWmiFanController(ILenovoWmiOperations? operations = null)
    {
        _operations = operations ?? new LenovoWmiOperations();
        _isSupported = _operations.IsSmartFanSupported;
        AvailabilityMessage = _isSupported
            ? "Lenovo GameZone firmware power modes are available. Performance is restricted to AC power."
            : "Lenovo GameZone WMI is unavailable or does not report Smart Fan support.";
    }

    public bool IsSupported => _isSupported;
    public string AvailabilityMessage { get; }

    public Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken)
    {
        if (!_isSupported)
            return Task.FromResult<FanMode?>(null);

        return Task.Run<FanMode?>(() => _operations.GetSmartFanMode() switch
        {
            1 => FanMode.Quiet,
            2 => FanMode.Auto,
            3 => FanMode.Performance,
            244 => FanMode.MaxCooling,
            255 => FanMode.Custom,
            _ => null
        }, cancellationToken);
    }

    public async Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken)
    {
        if (!_isSupported)
            return null;

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            return _operations.ReadCustomFanTable();
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException
            or COMException or TimeoutException)
        {
            return null;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken)
    {
        if (!_isSupported)
            return FanControlResult.Unsupported(AvailabilityMessage);

        if (mode is FanMode.Performance or FanMode.MaxCooling && !IsAcConnected())
            return new FanControlResult(false, "Performance and Max Cooling require AC power.");

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (mode == FanMode.MaxCooling)
            {
                _operations.SetFullSpeed(false);
                _operations.SetSmartFanMode(244u);
                return new FanControlResult(true,
                    "Maximum firmware cooling mode applied.");
            }

            if (mode == FanMode.Custom)
            {
                _operations.SetFullSpeed(false);
                _operations.SetSmartFanMode(255u);
                return new FanControlResult(true, "Custom firmware mode enabled.");
            }

            var firmwareMode = mode switch
            {
                FanMode.Quiet => 1u,
                FanMode.Auto or FanMode.Balanced => 2u,
                FanMode.Performance => 3u,
                _ => 0u
            };

            if (firmwareMode == 0)
                return FanControlResult.Unsupported("The requested firmware mode is not exposed by the Lenovo interface.");

            _operations.SetFullSpeed(false);
            _operations.SetSmartFanMode(firmwareMode);
            return new FanControlResult(true,
                $"{mode} firmware mode applied.");
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or COMException
            or TimeoutException)
        {
            return new FanControlResult(false, $"Lenovo firmware rejected the {mode} request: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken)
    {
        if (!_isSupported)
            return FanControlResult.Unsupported(AvailabilityMessage);
        if (!IsAcConnected())
            return new FanControlResult(false, "Custom fan curves require AC power.");

        var errors = FanCurveValidator.Validate(curve);
        if (errors.Count > 0)
            return new FanControlResult(false, string.Join(" ", errors));

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var table = _operations.ReadCustomFanTable()
                ?? throw new InvalidOperationException("The Lenovo firmware did not expose the custom fan table.");
            var values = BuildFanSteps(curve, table);

            // Legion Toolkit clears the separate full-speed flag before applying
            // a table; otherwise firmware can keep both fans at maximum.
            _operations.SetFullSpeed(false);
            _operations.SetSmartFanMode(255u);
            _operations.SetFanTable(values);
            return new FanControlResult(true,
                "Custom firmware fan curve applied in Custom mode.");
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or COMException
            or TimeoutException)
        {
            return new FanControlResult(false, $"Lenovo firmware rejected the custom fan curve: {ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task RestoreSafeStateAsync(CancellationToken cancellationToken)
    {
        if (!_isSupported)
            return;

        await SetFanModeAsync(FanMode.Auto, cancellationToken);
    }

    private static int[] BuildFanSteps(FanCurve curve, FirmwareFanTable table)
    {
        var points = curve.Points;
        return table.TemperaturesCelsius.Select(temperature =>
        {
            var requested = Interpolate(points, temperature);
            var step = (int)Math.Ceiling(requested / 10d);
            return Math.Clamp(step, 0, 10);
        }).Zip(table.MinimumSteps, Math.Max).ToArray();
    }

    private static double Interpolate(IReadOnlyList<FanCurvePoint> points, double temperature)
    {
        if (temperature <= points[0].TemperatureCelsius)
            return points[0].FanPercent;
        if (temperature >= points[^1].TemperatureCelsius)
            return points[^1].FanPercent;
        for (var i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].TemperatureCelsius)
                continue;
            var before = points[i - 1];
            var after = points[i];
            var ratio = (temperature - before.TemperatureCelsius) /
                        (after.TemperatureCelsius - before.TemperatureCelsius);
            return before.FanPercent + ((after.FanPercent - before.FanPercent) * ratio);
        }
        return points[^1].FanPercent;
    }

    private static bool IsAcConnected()
    {
        var status = new SystemPowerStatus();
        return GetSystemPowerStatus(ref status) && status.ACLineStatus == 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    public void Dispose()
    {
        _commandLock.Dispose();
        _operations.Dispose();
    }
}

using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Hardware;

public sealed class EnergyDrvFanController : IFanController
{
    private const string DevicePath = @"\\.\EnergyDrv";
    private const uint IoctlSetPowerMode = 0x8310213C;
    private const uint IoctlReadFanState = 0x831020C4;
    private const uint IoctlSetFanState = 0x831020C0;
    private const uint NormalFanState = 1;
    private const uint FanStateQuery = 14;

    private readonly bool _isAvailable;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private DateTimeOffset _lastCommand = DateTimeOffset.MinValue;

    public bool IsSupported => _isAvailable;
    public string AvailabilityMessage { get; }
    public Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken) =>
        Task.FromResult<FirmwareFanTable?>(null);
    public Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<FanMode?>(null);

    public EnergyDrvFanController()
    {
        try
        {
            _isAvailable = Probe();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            _isAvailable = false;
        }
        AvailabilityMessage = _isAvailable
            ? "Lenovo ACPI-Compliant Virtual Power Controller detected. Preset firmware modes are available."
            : "Lenovo EnergyDrv is unavailable. Install or enable Lenovo ACPI-Compliant Virtual Power Controller.";
    }

    public Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken)
    {
        if (!_isAvailable)
            return Task.FromResult(FanControlResult.Unsupported(AvailabilityMessage));

        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = mode switch
            {
                FanMode.Quiet => 0x0013B001u,
                FanMode.Auto or FanMode.Balanced => 0x001F5001u,
                FanMode.Performance => 0x0012B001u,
                _ => 0u
            };

            if (command == 0)
                return FanControlResult.Unsupported("Custom fan curves and direct fan-speed commands are not supported by this backend.");

            await _commandLock.WaitAsync(cancellationToken);
            try
            {
                var sinceLast = DateTimeOffset.UtcNow - _lastCommand;
                if (sinceLast < TimeSpan.FromMilliseconds(500))
                    await Task.Delay(TimeSpan.FromMilliseconds(500) - sinceLast, cancellationToken);

                var result = await Task.Run(() => Send(IoctlSetPowerMode, command), cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                _lastCommand = DateTimeOffset.UtcNow;
                return result.Success
                    ? new FanControlResult(true, $"{mode} firmware mode applied.")
                    : new FanControlResult(false, $"EnergyDrv rejected the {mode} request (Windows error {result.ErrorCode}). No mode change was confirmed.");
            }
            catch (TimeoutException)
            {
                return new FanControlResult(false, "EnergyDrv did not respond within 2 seconds. No mode change was confirmed.");
            }
            finally
            {
                _commandLock.Release();
            }
        }, cancellationToken);
    }

    public Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken) =>
        Task.FromResult(FanControlResult.Unsupported("Custom fan curves are not exposed by the verified EnergyDrv interface."));

    public Task RestoreSafeStateAsync(CancellationToken cancellationToken) =>
        SetFanModeAsync(FanMode.Auto, cancellationToken);

    private static bool Probe()
    {
        using var handle = Open(DeviceAccess.Read);
        if (handle.IsInvalid) return false;

        var query = new[] { FanStateQuery };
        var output = new uint[1];
        return DeviceIoControl(handle, IoctlReadFanState, query, sizeof(uint), output, sizeof(uint), out _, IntPtr.Zero);
    }

    private static (bool Success, int ErrorCode) Send(uint ioctl, uint command)
    {
        using var handle = Open(DeviceAccess.ReadWrite, FileShare.None);
        if (handle.IsInvalid) return (false, Marshal.GetLastWin32Error());
        var input = ioctl == IoctlSetFanState ? new[] { 6u, 1u, command } : new[] { command };
        var output = new uint[1];
        var success = DeviceIoControl(handle, ioctl, input, input.Length * sizeof(uint),
            output, sizeof(uint), out _, IntPtr.Zero);
        return (success, success ? 0 : Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle Open(DeviceAccess access, FileShare share = FileShare.Read | FileShare.Write)
    {
        var handle = CreateFile(DevicePath, (uint)access, share, IntPtr.Zero,
            FileMode.Open, 0, IntPtr.Zero);
        return new SafeFileHandle(handle, true);
    }

    [Flags]
    private enum DeviceAccess : uint { Read = 0x80000000, Write = 0x40000000, ReadWrite = Read | Write }

    [Flags]
    private enum FileShare : uint { None = 0, Read = 1, Write = 2 }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, uint[]? input, int inputLength,
        uint[]? output, int outputLength, out int returned, IntPtr overlapped);

    public void Dispose() => _commandLock.Dispose();
}

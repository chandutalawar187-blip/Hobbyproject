using LenovoLoqControl.Core;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Exceptions;

namespace LenovoLoqControl.Hardware;

public sealed class NvidiaGpuOverclockController : IGpuOverclockController
{
    private const int MaxCoreOffsetMhz = 150;
    private const int MaxMemoryOffsetMhz = 200;
    private readonly object _sync = new();
    private bool _initialized;
    private PhysicalGPU? _gpu;

    public NvidiaGpuOverclockController()
    {
        try
        {
            GeneralApi.Initialize();
            _initialized = true;
            var handle = GPUApi.EnumPhysicalGPUs().FirstOrDefault();
            _gpu = handle.Equals(default(PhysicalGPUHandle)) ? null : new PhysicalGPU(handle);
        }
        catch (Exception ex) when (ex is NVIDIAApiException
            or InvalidOperationException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            AvailabilityMessage = ex switch
            {
                DllNotFoundException => "NVIDIA GPU overclocking is unavailable because the NVIDIA driver API was not found.",
                EntryPointNotFoundException or BadImageFormatException =>
                    "NVIDIA GPU overclocking is unavailable because the installed NVIDIA driver API is incompatible.",
                _ => "NVIDIA GPU overclocking is unavailable through the supported NVAPI interface."
            };
        }

        if (_gpu is not null)
        {
            IsSupported = true;
            AvailabilityMessage = $"NVIDIA GPU detected. Limits: +{MaxCoreOffsetMhz} MHz core, +{MaxMemoryOffsetMhz} MHz VRAM.";
        }
        else if (string.IsNullOrEmpty(AvailabilityMessage))
            AvailabilityMessage = "No supported NVIDIA GPU was detected.";
    }

    public bool IsSupported { get; }
    public string AvailabilityMessage { get; }

    public double? ReadCurrentGraphicsClockGhz()
    {
        lock (_sync)
        {
            var gpu = _gpu;
            if (!_initialized || !IsSupported || gpu is null)
                return null;
            try
            {
                var frequencies = GPUApi.GetAllClockFrequencies(gpu.Handle, null);
                var frequencyKHz = frequencies.GraphicsClock.Frequency;
                return frequencyKHz > 0 ? frequencyKHz / 1_000_000d : null;
            }
            catch (Exception ex) when (ex is NVIDIAApiException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    public Task<FanControlResult> ApplyAsync(int coreOffsetMhz, int memoryOffsetMhz, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        coreOffsetMhz = Math.Clamp(coreOffsetMhz, 0, MaxCoreOffsetMhz);
        memoryOffsetMhz = Math.Clamp(memoryOffsetMhz, 0, MaxMemoryOffsetMhz);
        lock (_sync)
        {
            var gpu = _gpu;
            if (!_initialized || !IsSupported || gpu is null)
                return Task.FromResult(FanControlResult.Unsupported(AvailabilityMessage));

            if (!IsAcConnected())
                return Task.FromResult(new FanControlResult(false, "GPU overclocking requires AC power."));

            try
            {
                SetOverclockInfo(gpu, coreOffsetMhz, memoryOffsetMhz);
                return Task.FromResult(new FanControlResult(true,
                    $"GPU overclock applied: core +{coreOffsetMhz} MHz, VRAM +{memoryOffsetMhz} MHz."));
            }
            catch (Exception ex) when (ex is NVIDIAApiException or InvalidOperationException)
            {
                return Task.FromResult(new FanControlResult(false, $"NVIDIA rejected the GPU overclock: {ex.Message}"));
            }
        }
    }

    public Task<FanControlResult> ResetAsync(CancellationToken cancellationToken) =>
        ApplyAsync(0, 0, cancellationToken);

    private static void SetOverclockInfo(PhysicalGPU gpu, int coreMhz, int memoryMhz)
    {
        var clocks = new[]
        {
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics,
                new PerformanceStates20ParameterDelta(coreMhz * 1000)),
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory,
                new PerformanceStates20ParameterDelta(memoryMhz * 1000))
        };
        var state = new PerformanceStates20InfoV1(
            [new PerformanceStates20InfoV1.PerformanceState20(
                PerformanceStateId.P0_3DPerformance, clocks,
                Array.Empty<PerformanceStates20BaseVoltageEntryV1>())], 2, 0);
        GPUApi.SetPerformanceStates20(gpu.Handle, state);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_initialized) return;
            try { GeneralApi.Unload(); } catch (NVIDIAApiException) { }
            _initialized = false;
            _gpu = null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    private static bool IsAcConnected()
    {
        var status = new SystemPowerStatus();
        return GetSystemPowerStatus(ref status) && status.ACLineStatus == 1;
    }

    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
    }
}

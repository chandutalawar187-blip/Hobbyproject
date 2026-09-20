using System.ServiceProcess;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Management;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;
using DiskInfoToolkit;
using Microsoft.Win32.SafeHandles;

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
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private HardwareBackend? _hardware;
    private CancellationTokenSource? _pipeCancellation;
    private Task? _pipeTask;
    private readonly SemaphoreSlim _telemetryLock = new(1, 1);
    private readonly object _telemetryStateLock = new();
    private SensorReading? _cachedTelemetry;
    private DateTimeOffset _cachedTelemetryAt;
    private Task<SensorReading>? _telemetryReadTask;

    public LoqHardwareService()
    {
        ServiceName = "LoqControlHardwareService";
        CanStop = true;
        CanPauseAndContinue = false;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _hardware = new HardwareBackend(useElevatedService: false);
        _pipeCancellation = new CancellationTokenSource();
        _pipeTask = RunPipeServerAsync(_pipeCancellation.Token);
    }

    protected override void OnStop()
    {
        _pipeCancellation?.Cancel();
        try { _pipeTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _pipeTask = null;
        _pipeCancellation?.Dispose();
        _pipeCancellation = null;
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

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreatePipe();
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       or UnauthorizedAccessException
                                       or Win32Exception
                                       or COMException
                                       or IdentityNotMappedException)
            {
                LogServiceError($"Pipe creation failed: {ex}");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            await using (server)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var recycleServer = false;
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken);
                        await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                        {
                            AutoFlush = true
                        };
                        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var (line, requestTooLarge) = await ReadRequestAsync(server, requestTimeout.Token);
                        if (!IsAuthorizedClient(server))
                        {
                            LogServiceError("Rejected named-pipe client.");
                            continue;
                        }

                        ServiceResponse response;
                        if (requestTooLarge)
                        {
                            response = new ServiceResponse(false, $"Service requests cannot exceed {MaxRequestBytes} bytes.");
                        }
                        else try
                        {
                            response = await HandleRequestAsync(line, requestTimeout.Token);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException
                                                   or System.TimeoutException
                                                   or UnauthorizedAccessException
                                                   or IOException
                                                   or System.Management.ManagementException
                                                   or COMException)
                        {
                            LogServiceError($"Service command failed: {ex}");
                            response = new ServiceResponse(false, $"Hardware operation failed: {ex.Message}");
                        }

                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // An idle or unauthorized client must not hold the single pipe instance.
                    }
                    catch (IOException)
                    {
                        // A disconnected client must not stop the service.
                    }
                    catch (Exception ex)
                    {
                        LogServiceError($"Pipe loop failed: {ex}");
                    }
                    finally
                    {
                        if (server.IsConnected)
                            server.Disconnect();
                        recycleServer = true;
                    }

                    if (recycleServer)
                        break;
                }
            }
        }
    }

    private static async Task<(string? Line, bool IsOversized)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var request = new MemoryStream();
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                return (DecodeRequest(request), false);

            var newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            var payloadLength = newlineIndex >= 0 ? newlineIndex : bytesRead;
            if (request.Length + payloadLength > MaxRequestBytes)
                return (null, true);

            request.Write(buffer, 0, payloadLength);
            if (newlineIndex >= 0)
                return (DecodeRequest(request), false);
        }
    }

    private static string DecodeRequest(MemoryStream request)
    {
        var buffer = request.GetBuffer();
        var length = checked((int)request.Length);
        if (length > 0 && buffer[length - 1] == (byte)'\r')
            length--;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static void LogServiceError(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LOQ Control", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "hardware-service.log"),
                $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }

        catch (IOException)
        {
        }
    }

    private async Task<ServiceResponse> HandleRequestAsync(string? line, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return new ServiceResponse(false, "Empty service request.");

                ServiceRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ServiceRequest>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    return new ServiceResponse(false, "Invalid service request.");
                }

                if (request is null || request.Version != ServiceProtocol.ProtocolVersion)
                    return new ServiceResponse(false, "Unsupported service protocol version.");

                var hardware = _hardware;
                if (hardware is null)
                    return new ServiceResponse(false, "Hardware service is not ready.");

                switch (request.Command)
                {
                    case "get-telemetry":
                        return new ServiceResponse(true, "Hardware telemetry read.", true,
                            Reading: await ReadTelemetryAsync(hardware, cancellationToken));
                    case "get-ssd-temperature":
                        return new ServiceResponse(true, "SSD temperature read.", true,
                            SsdTemperature: ReadSsdTemperature());
                    case "get-mode":
                        if (!hardware.FanController.IsSupported)
                            return new ServiceResponse(false, hardware.FanController.AvailabilityMessage);
                        return new ServiceResponse(true, "Current firmware mode read.", true,
                            await hardware.FanController.GetCurrentModeAsync(cancellationToken));
                    case "get-table":
                        if (!hardware.FanController.IsSupported)
                            return new ServiceResponse(false, hardware.FanController.AvailabilityMessage);
                        return new ServiceResponse(true, "Custom fan table read.", true,
                            Table: await hardware.FanController.ReadCustomFanTableAsync(cancellationToken));
                    case "set-mode" when request.Mode is FanMode mode:
                        if (!hardware.FanController.IsSupported)
                            return new ServiceResponse(false, hardware.FanController.AvailabilityMessage);
                        return ToResponse(await hardware.FanController.SetFanModeAsync(mode, cancellationToken));
                    case "set-curve" when request.Curve is not null:
                        if (!hardware.FanController.IsSupported)
                            return new ServiceResponse(false, hardware.FanController.AvailabilityMessage);
                        return ToResponse(await hardware.FanController.SetFanCurveAsync(new FanCurve(request.Curve), cancellationToken));
                    case "restore-safe":
                        if (!hardware.FanController.IsSupported)
                            return new ServiceResponse(false, hardware.FanController.AvailabilityMessage);
                        await hardware.FanController.RestoreSafeStateAsync(cancellationToken);
                        return new ServiceResponse(true, "Safe automatic mode restored.", true);
                    default:
                        return new ServiceResponse(false, "Unsupported or incomplete service command.");
                }
    }

    private static ServiceResponse ToResponse(FanControlResult result) =>
                new(result.Accepted, result.Message, result.Accepted);

    private static double? ReadSsdTemperature()
    {
        double? temperature = null;
        try
        {
            StorageManager.ReloadStorages();
            foreach (var disk in StorageManager.Storages)
            {
                disk.Update();
                var value = disk.Smart?.Temperature;
                if (value is > 0 and < 150)
                    temperature = temperature is double current ? Math.Max(current, value.Value) : value.Value;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Continue to the Windows storage fallbacks.
        }

        if (temperature is double)
            return temperature;

        try
        {
            using var counters = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject counter in counters.Get())
            {
                if (double.TryParse(counter["Temperature"]?.ToString(), out var value)
                    && value is > 0 and < 150)
                    temperature = temperature is double current ? Math.Max(current, value) : value;
            }
        }
        catch (ManagementException)
        {
            // Some OEM storage drivers do not expose reliability counters.
        }

        return temperature ?? ReadNvmeSmartTemperature();
    }

    private static double? ReadNvmeSmartTemperature()
    {
        for (var diskNumber = 0; diskNumber < 32; diskNumber++)
        {
            using var handle = CreateFile(
                $@"\\.\PhysicalDrive{diskNumber}", 0, FileShare.ReadWrite, IntPtr.Zero,
                FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                continue;

            var query = new byte[48];
            BitConverter.GetBytes(50u).CopyTo(query, 0);
            BitConverter.GetBytes(0u).CopyTo(query, 4);
            BitConverter.GetBytes(3u).CopyTo(query, 8);
            BitConverter.GetBytes(2u).CopyTo(query, 12);
            BitConverter.GetBytes(2u).CopyTo(query, 16);
            BitConverter.GetBytes(0u).CopyTo(query, 20);
            BitConverter.GetBytes(40u).CopyTo(query, 24);
            BitConverter.GetBytes(512u).CopyTo(query, 28);

            var output = new byte[4096];
            if (!DeviceIoControl(handle, 0x002D1400, query, query.Length,
                    output, output.Length, out var returned, IntPtr.Zero)
                || returned < 51)
                continue;

            var kelvin = BitConverter.ToUInt16(output, 49);
            var celsius = kelvin - 273.15;
            if (celsius is > 0 and < 150)
                return celsius;
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte[] inputBuffer, int inputBufferSize,
        byte[] outputBuffer, int outputBufferSize, out int bytesReturned, IntPtr overlapped);

    private async Task<SensorReading> ReadTelemetryAsync(
        HardwareBackend hardware,
        CancellationToken cancellationToken)
    {
        Task<SensorReading> readTask;
        lock (_telemetryStateLock)
        {
            if (_cachedTelemetry is not null &&
                DateTimeOffset.UtcNow - _cachedTelemetryAt < TimeSpan.FromSeconds(1))
                return _cachedTelemetry;

            _telemetryReadTask ??= SampleTelemetryAsync(hardware);
            readTask = _telemetryReadTask;
        }

        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        }
        catch (System.TimeoutException)
        {
            lock (_telemetryStateLock)
            {
                if (_cachedTelemetry is not null)
                    return _cachedTelemetry;
            }

            throw;
        }
        finally
        {
            if (readTask.IsCompleted)
            {
                lock (_telemetryStateLock)
                {
                    if (ReferenceEquals(_telemetryReadTask, readTask))
                        _telemetryReadTask = null;
                }
            }
        }
    }

    private async Task<SensorReading> SampleTelemetryAsync(HardwareBackend hardware)
    {
        await _telemetryLock.WaitAsync();
        try
        {
            var reading = await hardware.Monitor.ReadAsync(CancellationToken.None);
            lock (_telemetryStateLock)
            {
                _cachedTelemetry = reading;
                _cachedTelemetryAt = DateTimeOffset.UtcNow;
            }

            return reading;
        }
        finally
        {
            _telemetryLock.Release();
        }
    }

    private static bool IsAuthorizedClient(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientProcessId) ||
                !ProcessIdToSessionId(clientProcessId, out var clientSessionId))
                return false;

            return IsActiveSession(clientSessionId);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or IOException
                                   or IdentityNotMappedException
                                   or COMException
                                   or Win32Exception
                                   or UnauthorizedAccessException)
        {
            LogServiceError($"Client authorization check failed: {ex}");
            return false;
        }
    }

    private static NamedPipeServerStream CreatePipe()
            {
                var security = new PipeSecurity();
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                security.AddAccessRule(new PipeAccessRule(
                    users, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    ServiceProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                    4096,
                    4096,
                    security);
    }

    private static SecurityIdentifier? GetActiveUserSid()
    {
            var consoleSessionId = WTSGetActiveConsoleSessionId();
            if (consoleSessionId != InvalidSessionId)
            {
                var sid = GetSessionUserSid(consoleSessionId);
                if (sid is not null)
                    return sid;
            }

            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessionInfo, out var count))
                return null;

            try
            {
                var current = sessionInfo;
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var i = 0; i < count; i++)
                {
                    var session = Marshal.PtrToStructure<WtsSessionInfo>(current);
                    if (session.State == WtsConnectState.Active)
                    {
                        var sid = GetSessionUserSid(session.SessionId);
                        if (sid is not null)
                            return sid;
                    }

                    current += size;
                }

                return null;
            }
            finally
            {
                WTSFreeMemory(sessionInfo);
            }
        }

        private static bool IsActiveSession(uint sessionId)
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessionInfo, out var count))
                return false;

            try
            {
                var current = sessionInfo;
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var i = 0; i < count; i++)
                {
                    var session = Marshal.PtrToStructure<WtsSessionInfo>(current);
                    if (session.SessionId == sessionId)
                        return session.State == WtsConnectState.Active;
                    current += size;
                }

                return false;
            }
            finally
            {
                WTSFreeMemory(sessionInfo);
            }
        }

        private static SecurityIdentifier? GetSessionUserSid(uint sessionId)
        {
            if (WTSQueryUserToken(sessionId, out var token))
            {
                try
                {
                    using var identity = new WindowsIdentity(token);
                    return identity.User;
                }
                finally
                {
                    CloseHandle(token);
                }
            }

            var userName = QuerySessionString(sessionId, WtsInfoClass.UserName);
            var domainName = QuerySessionString(sessionId, WtsInfoClass.DomainName);
            if (string.IsNullOrWhiteSpace(userName))
                return null;

            var qualifiedName = string.IsNullOrWhiteSpace(domainName)
                ? userName
                : $"{domainName}\\{userName}";
            try
            {
                return new NTAccount(qualifiedName).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
            }
            catch (IdentityNotMappedException)
            {
                return null;
            }
        }

        private static string? QuerySessionString(uint sessionId, WtsInfoClass infoClass)
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero, sessionId, infoClass, out var buffer, out var byteCount))
                return null;

            try
            {
                return byteCount == 0 ? null : Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        private const uint InvalidSessionId = uint.MaxValue;

        private enum WtsConnectState
        {
            Active = 0
        }

        private enum WtsInfoClass
        {
            UserToken = 4,
            UserName = 5,
            DomainName = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WtsSessionInfo
        {
            public uint SessionId;
            public IntPtr WinStationName;
            public WtsConnectState State;
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSEnumerateSessions(
            IntPtr server,
            int reserved,
            int version,
            out IntPtr sessionInfo,
            out int count);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr server,
            uint sessionId,
            WtsInfoClass infoClass,
            out IntPtr buffer,
            out int byteCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint processId);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }

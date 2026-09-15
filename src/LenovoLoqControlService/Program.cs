using System.ServiceProcess;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private HardwareBackend? _hardware;
    private Timer? _timer;
    private CancellationTokenSource? _pipeCancellation;
    private Task? _pipeTask;
    private readonly SemaphoreSlim _telemetryLock = new(1, 1);
    private SensorReading? _cachedTelemetry;
    private DateTimeOffset _cachedTelemetryAt;
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
        _hardware = new HardwareBackend(useElevatedService: false);
        _pipeCancellation = new CancellationTokenSource();
        _pipeTask = RunPipeServerAsync(_pipeCancellation.Token);
        _timer = new Timer(EnforceCurrentMode, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    protected override void OnStop()
    {
        _timer?.Dispose();
        _timer = null;
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

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await using var server = CreatePipe();
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken);
                        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                        await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        if (!IsAuthorizedClient(server))
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(
                                new ServiceResponse(false, "The connected client is not authorized.")));
                            continue;
                        }

                        var line = await reader.ReadLineAsync(cancellationToken);
                        ServiceResponse response;
                        try
                        {
                            response = await HandleRequestAsync(line, cancellationToken);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException
                                                   or System.TimeoutException
                                                   or UnauthorizedAccessException
                                                   or IOException
                                                   or System.Management.ManagementException
                                                   or COMException)
                        {
                            response = new ServiceResponse(false, $"Hardware operation failed: {ex.Message}");
                        }
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        // A disconnected client must not stop the service.
                    }
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

    private async Task<SensorReading> ReadTelemetryAsync(
        HardwareBackend hardware,
        CancellationToken cancellationToken)
    {
        await _telemetryLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedTelemetry is not null &&
                DateTimeOffset.UtcNow - _cachedTelemetryAt < TimeSpan.FromSeconds(1))
                return _cachedTelemetry;

            _cachedTelemetry = await hardware.Monitor.ReadAsync(cancellationToken);
            _cachedTelemetryAt = DateTimeOffset.UtcNow;
            return _cachedTelemetry;
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
            var userName = server.GetImpersonationUserName();
            return !userName.Equals("ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The pipe ACL already limits access to authenticated local users.
            // Some client tokens do not expose an impersonation name at this level.
            return true;
        }
    }

    private static NamedPipeServerStream CreatePipe()
            {
                var security = new PipeSecurity();
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                security.AddAccessRule(new PipeAccessRule(users, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    ServiceProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    4096,
                    4096,
                    security);
    }
    }

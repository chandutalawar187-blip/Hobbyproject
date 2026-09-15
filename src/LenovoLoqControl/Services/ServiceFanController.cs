using System.IO.Pipes;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Services;

public sealed class ServiceFanController : IFanController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly bool _serviceAvailable;

    public ServiceFanController()
    {
        _serviceAvailable = IsServiceRunning();
        AvailabilityMessage = _serviceAvailable
            ? "Verified Lenovo hardware control is provided by the elevated LOQ Control service."
            : "The elevated LOQ Control service is unavailable. Fan control is disabled until it is installed and running.";
    }

    public bool IsSupported => _serviceAvailable;
    public string AvailabilityMessage { get; }

    public async Task<FanMode?> GetCurrentModeAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new ServiceRequest(ServiceProtocol.ProtocolVersion, "get-mode"), cancellationToken);
        if (!response.Success || response.Mode is not FanMode mode || !Enum.IsDefined(mode))
            return null;
        return mode;
    }

    public async Task<FirmwareFanTable?> ReadCustomFanTableAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new ServiceRequest(ServiceProtocol.ProtocolVersion, "get-table"), cancellationToken);
        return response.Success ? response.Table : null;
    }

    public async Task<FanControlResult> SetFanModeAsync(FanMode mode, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
            return FanControlResult.Unsupported("The requested fan mode is invalid.");
        var response = await SendAsync(new ServiceRequest(ServiceProtocol.ProtocolVersion, "set-mode", mode), cancellationToken);
        return new FanControlResult(response.Success, response.Message);
    }

    public async Task<FanControlResult> SetFanCurveAsync(FanCurve curve, CancellationToken cancellationToken)
    {
        var errors = FanCurveValidator.Validate(curve);
        if (errors.Count > 0)
            return new FanControlResult(false, string.Join(" ", errors));
        var response = await SendAsync(new ServiceRequest(ServiceProtocol.ProtocolVersion, "set-curve", Curve: curve.Points), cancellationToken);
        return new FanControlResult(response.Success, response.Message);
    }

    public async Task RestoreSafeStateAsync(CancellationToken cancellationToken)
    {
        if (_serviceAvailable)
            await SendAsync(new ServiceRequest(ServiceProtocol.ProtocolVersion, "restore-safe"), cancellationToken);
    }

    public void Dispose()
    {
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController("LoqControlHardwareService");
            service.Refresh();
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<ServiceResponse> SendAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, cancellationToken);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await reader.ReadLineAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(line)
                ? new ServiceResponse(false, "The hardware service returned an empty response.")
                : JsonSerializer.Deserialize<ServiceResponse>(line, JsonOptions)
                    ?? new ServiceResponse(false, "The hardware service returned an invalid response.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.TimeoutException or UnauthorizedAccessException)
        {
            return new ServiceResponse(false, $"The hardware service is unavailable: {ex.Message}");
        }
    }
}

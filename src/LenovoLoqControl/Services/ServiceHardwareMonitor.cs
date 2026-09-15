using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.Services;

public sealed class ServiceHardwareMonitor : IHardwareMonitor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHardwareMonitor _fallback;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private SensorReading? _lastReading;

    public ServiceHardwareMonitor(IHardwareMonitor fallback)
    {
        _fallback = fallback;
        Identity = fallback.Identity;
    }

    public HardwareIdentity Identity { get; }

    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, cancellationToken);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ServiceRequest(ServiceProtocol.ProtocolVersion, "get-telemetry"), JsonOptions));
            var line = await reader.ReadLineAsync(cancellationToken);
            var response = string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<ServiceResponse>(line, JsonOptions);
            Log($"response success={response?.Success} message={response?.Message} reading={response?.Reading is not null} cpu={response?.Reading?.CpuTemperature} gpu={response?.Reading?.GpuTemperature} cpuFan={response?.Reading?.CpuFanRpm} gpuFan={response?.Reading?.GpuFanRpm}");
            if (response?.Success == true && response.Reading is not null)
            {
                _lastReading = response.Reading;
                return response.Reading;
            }

            return _lastReading ?? await _fallback.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.TimeoutException or UnauthorizedAccessException)
        {
            Log($"fallback {ex.GetType().Name}: {ex.Message}");
            return _lastReading ?? await _fallback.ReadAsync(cancellationToken);
        }
        finally
        {
            _readLock.Release();
        }
    }

    public void Dispose()
    {
        _readLock.Dispose();
        _fallback.Dispose();
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LOQ Control", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "service-telemetry.log"),
                $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}

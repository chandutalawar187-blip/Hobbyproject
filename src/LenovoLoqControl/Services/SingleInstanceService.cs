using System.IO.Pipes;
using System.IO;
using System.Text;

namespace LenovoLoqControl.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\LenovoLoqControl.SingleInstance";
    private const string PipeName = "LenovoLoqControl.Activate";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _ownsMutex;

    private SingleInstanceService(Mutex mutex)
    {
        _mutex = mutex;
    }

    public event Action? ActivationRequested;

    public static bool TryCreate(out SingleInstanceService? instance)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstanceService(mutex) { _ownsMutex = true };
        instance.ListenForActivation();
        return true;
    }

    public static void ActivateExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            var message = Encoding.UTF8.GetBytes("activate");
            client.Write(message, 0, message.Length);
        }
        catch (TimeoutException)
        {
            // The existing process may still be starting. The mutex still prevents a duplicate.
        }
        catch (IOException)
        {
            // The existing process may be shutting down.
        }
    }

    private void ListenForActivation()
    {
        _ = Task.Run(async () =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_shutdown.Token);
                    var buffer = new byte[32];
                    _ = await server.ReadAsync(buffer, _shutdown.Token);
                    ActivationRequested?.Invoke();
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // Recreate the pipe after a client disconnects unexpectedly.
                }
            }
        });
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex may already have been released during process teardown.
            }
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }
}

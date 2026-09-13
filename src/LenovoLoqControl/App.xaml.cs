using System.IO;
using System.Reflection;
using System.Windows;

namespace LenovoLoqControl;

public partial class App : Application
{
    private bool _startupCompleted;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            var exception = Unwrap(args.Exception);
            try
            {
                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:u} startupCompleted={_startupCompleted} {exception}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never prevent the error from being shown.
            }
            MessageBox.Show(
                _startupCompleted
                    ? $"LOQ Control encountered an unexpected error. Details were written to:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{exception.Message}"
                    : $"LOQ Control could not start. Details were written to:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                _startupCompleted ? "LOQ Control error" : "LOQ Control startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            if (!_startupCompleted)
                Shutdown(-1);
        };

        base.OnStartup(e);
        _startupCompleted = true;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TypeInitializationException or TargetInvocationException
               && exception.InnerException is not null)
            exception = exception.InnerException;
        return exception;
    }
}

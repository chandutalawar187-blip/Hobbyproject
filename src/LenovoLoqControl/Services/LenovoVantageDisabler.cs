using System.Diagnostics;
using System.Management;
using System.ServiceProcess;

namespace LenovoLoqControl.Services;

public sealed record LenovoVantageStatus(
    bool Installed,
    bool Enabled,
    string Message);

public sealed class LenovoVantageDisabler
{
    private static readonly string[] ServiceNames = ["ImControllerService", "LenovoVantageService"];
    private static readonly string[] ProcessNames = ["LenovoVantage", "Lenovo.Modern.ImController"];
    private static readonly string[] TaskPaths =
    [
        @"\Lenovo\BatteryGauge\",
        @"\Lenovo\ImController\",
        @"\Lenovo\ImController\Plugins\",
        @"\Lenovo\ImController\TimeBasedEvents\",
        @"\Lenovo\UDC\",
        @"\Lenovo\Vantage\",
        @"\Lenovo\Vantage\Schedule\"
    ];

    public Task<LenovoVantageStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(GetStatus, cancellationToken);

    public Task<LenovoVantageStatus> DisableAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            RunElevatedIntegrationCommand(false);
            KillProcesses();
            return GetStatus();
        }, cancellationToken);

    public Task<LenovoVantageStatus> EnableAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            RunElevatedIntegrationCommand(true);
            return GetStatus();
        }, cancellationToken);

    private static LenovoVantageStatus GetStatus()
    {
        var installed = false;
        var enabled = false;
        var active = new List<string>();

        foreach (var serviceName in ServiceNames)
        {
            using var service = FindService(serviceName);
            if (service is null)
                continue;
            installed = true;
            var state = Convert.ToString(service["State"]) ?? "Unknown";
            var startMode = Convert.ToString(service["StartMode"]) ?? "Unknown";
            if (state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                !startMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                active.Add($"{serviceName}={state}/{startMode}");
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (ProcessNames.Any(name => process.ProcessName.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                {
                    enabled = true;
                    active.Add($"process:{process.ProcessName}");
                }
            }
            catch
            {
                // A process may exit while the status snapshot is being collected.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (!installed && active.Count == 0)
            return new(false, false, "Lenovo Vantage and ImController were not found.");
        return enabled
            ? new(true, true, $"Lenovo integration is active ({string.Join(", ", active)}).")
            : new(true, false, "Lenovo Vantage and ImController are disabled.");
    }

    private static ManagementObject? FindService(string name)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            $"SELECT * FROM Win32_Service WHERE Name = '{name}'");
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }

    private static void SetServiceEnabled(string name, bool enabled)
    {
        using var service = FindService(name);
        if (service is null)
            return;

        // Use the Win32_Service provider directly instead of starting sc.exe
        // for every operation. This avoids several process-start and polling
        // delays while keeping the service manager as the authority.
        using var modeParameters = service.GetMethodParameters("ChangeStartMode");
        modeParameters["StartMode"] = enabled ? "Automatic" : "Disabled";
        using var modeResponse = service.InvokeMethod(
            "ChangeStartMode", modeParameters, new InvokeMethodOptions());
        var modeResult = ReadMethodReturnValue(modeResponse);
        if (modeResult != 0)
            throw new InvalidOperationException($"Unable to change startup mode for {name} (WMI code {modeResult}).");

        if (!enabled)
        {
            // Disable automatic restart before requesting a stop. Lenovo's
            // Vantage service can otherwise be restarted by SCM between the
            // stop request and the startup-mode change.
            StopService(name);
        }

        if (enabled)
        {
            StartService(name);
        }
    }

    private static uint ReadMethodReturnValue(ManagementBaseObject? response)
    {
        var value = response?["ReturnValue"];
        return value is null ? 0u : Convert.ToUInt32(value);
    }

    private static void StopService(string name)
    {
        using var controller = new ServiceController(name);
        controller.Refresh();
        if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            return;

        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
    }

    private static void StartService(string name)
    {
        using var controller = new ServiceController(name);
        controller.Refresh();
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            return;

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
    }

    private static void KillProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (ProcessNames.Any(name => process.ProcessName.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // The service state is still reported; an individual protected process may survive.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TrySetScheduledTasksEnabled(bool enabled)
    {
        var script = "$paths=@(" +
            string.Join(",", TaskPaths.Select(path => $"'{path}'")) +
            "); foreach($path in $paths) { Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | " +
            $"ForEach-Object {{ Enable-ScheduledTask -InputObject $_ -ErrorAction Stop | Out-Null }}" +
            " }";
        if (!enabled)
            script = script.Replace("Enable-ScheduledTask", "Disable-ScheduledTask", StringComparison.Ordinal);
        try
        {
            RunPowerShell(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.TimeoutException)
        {
            // Scheduled-task folders are optional; service control remains authoritative.
        }
    }

    private static void RunElevatedIntegrationCommand(bool enabled)
    {
        var taskPaths = string.Join(",", TaskPaths.Select(path => $"'{path}'"));
        var serviceNames = string.Join(",", ServiceNames.Select(name => $"'{name}'"));
        var serviceCommand = enabled
            ? "Set-Service -Name $name -StartupType Automatic -ErrorAction Stop; Start-Service -Name $name -ErrorAction Stop"
            : "Stop-Service -Name $name -Force -ErrorAction SilentlyContinue; Set-Service -Name $name -StartupType Disabled -ErrorAction Stop";
        var taskCommand = enabled
            ? "Enable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue | Out-Null"
            : "Disable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue | Out-Null";
        var script = "$paths=@(" + taskPaths + "); " +
                     "foreach($path in $paths) { " +
                     $"Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | ForEach-Object {{ {taskCommand} }} " +
                     "}; " +
                     $"foreach($name in @({serviceNames})) {{ if(Get-Service -Name $name -ErrorAction SilentlyContinue) {{ {serviceCommand} }} }}";

        var startInfo = new ProcessStartInfo
        {
            FileName = TrustedWindowsExecutables.PowerShell,
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to request administrator access for Lenovo integration.");
        if (!process.WaitForExit(15000))
        {
            process.Kill();
            throw new System.TimeoutException("Lenovo integration service operation timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Lenovo integration service operation failed with exit code {process.ExitCode}.");
    }

    private static void RunServiceCommand(string command, string serviceName, string? extra = null,
        bool allowAlreadyStopped = false, bool allowAlreadyRunning = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = TrustedWindowsExecutables.ServiceControl,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.ArgumentList.Add(serviceName);
        if (command.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add("start=");
            process.StartInfo.ArgumentList.Add(extra ?? "disabled");
        }
        else if (extra is not null)
            process.StartInfo.ArgumentList.Add(extra);
        if (!process.Start())
            throw new InvalidOperationException($"Unable to control service {serviceName}.");
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new System.TimeoutException($"Service operation {command} timed out for {serviceName}.");
        }
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (process.ExitCode == 0 ||
            (allowAlreadyStopped && output.Contains("1062", StringComparison.OrdinalIgnoreCase)) ||
            (allowAlreadyRunning && output.Contains("1056", StringComparison.OrdinalIgnoreCase)))
            return;
        throw new InvalidOperationException(
            $"Service operation {command} failed for {serviceName}: {output.Trim()}");
    }

    private static void RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TrustedWindowsExecutables.PowerShell,
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the scheduled-task controller.");
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new System.TimeoutException("Scheduled-task operation timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Scheduled-task operation failed with exit code {process.ExitCode}: {process.StandardError.ReadToEnd()}");
    }
}

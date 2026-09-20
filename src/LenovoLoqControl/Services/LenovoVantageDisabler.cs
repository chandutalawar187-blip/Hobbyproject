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
    private static readonly TimeSpan ElevatedOperationTimeout = TimeSpan.FromSeconds(60);
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

        if (!installed)
        {
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

    private static void RunElevatedIntegrationCommand(bool enabled)
    {
        var taskPaths = string.Join(",", TaskPaths.Select(path => $"'{path}'"));
        var serviceNames = string.Join(",", ServiceNames.Select(name => $"'{name}'"));
        var serviceCommand = enabled
            ? "Set-Service -Name $name -StartupType Automatic -ErrorAction Stop; " +
              "if((Get-Service -Name $name).Status -ne 'Running') { Start-Service -Name $name -ErrorAction Stop }"
            : "if((Get-Service -Name $name).Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction Stop }; " +
              "Set-Service -Name $name -StartupType Disabled -ErrorAction Stop";
        var taskCommand = enabled
            ? "Enable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue | Out-Null"
            : "Disable-ScheduledTask -InputObject $_ -ErrorAction SilentlyContinue | Out-Null";
        var processCommand = enabled
            ? string.Empty
            : "Get-Process -Name LenovoVantage,Lenovo.Modern.ImController -ErrorAction SilentlyContinue | " +
              "Stop-Process -Force -ErrorAction SilentlyContinue; ";
        var verification = enabled
            ? "foreach($name in @(" + serviceNames + ")) { " +
              "if(Get-Service -Name $name -ErrorAction SilentlyContinue | " +
              "Where-Object {$_.Status -ne 'Running' -or $_.StartType -ne 'Automatic'}) { " +
              "throw \"Unable to start Lenovo service $name.\" } }"
            : "foreach($name in @(" + serviceNames + ")) { " +
              "if(Get-Service -Name $name -ErrorAction SilentlyContinue | " +
              "Where-Object {$_.Status -ne 'Stopped' -or $_.StartType -ne 'Disabled'}) { " +
              "throw \"Unable to disable Lenovo service $name.\" } }";
        var script = "$ErrorActionPreference='Stop'; " +
                     "$paths=@(" + taskPaths + "); " +
                     "foreach($path in $paths) { " +
                     $"Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | ForEach-Object {{ {taskCommand} }} " +
                     "}; " +
                     $"foreach($name in @({serviceNames})) {{ if(Get-Service -Name $name -ErrorAction SilentlyContinue) {{ {serviceCommand} }} }} " +
                     processCommand +
                     verification;

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
        if (!process.WaitForExit((int)ElevatedOperationTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new System.TimeoutException("Lenovo integration service operation timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Lenovo integration service operation failed with exit code {process.ExitCode}.");
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

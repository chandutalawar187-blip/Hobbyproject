using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Text.Json;

namespace LenovoLoqControl.Hardware;

internal static class SmartmontoolsTemperatureReader
{
    public static double? ReadTemperature(string physicalDrive, string expectedModel)
    {
        var executable = FindSmartctl();
        if (executable is null)
            return null;

        try
        {
            var discovered = Run(executable, "--scan-open");
            foreach (var line in SplitLines(discovered))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0 || !fields[0].StartsWith('/'))
                    continue;

                var device = fields[0];
                var deviceType = fields.Length >= 3 && fields[1] == "-d" ? fields[2] : null;
                var json = deviceType is null
                    ? Run(executable, "--json", "--all", device)
                    : Run(executable, "--json", "--all", "-d", deviceType, device);
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty("model_name", out var model)
                    && !string.IsNullOrWhiteSpace(expectedModel)
                    && !model.GetString()!.Contains(expectedModel, StringComparison.OrdinalIgnoreCase)
                    && !expectedModel.Contains(model.GetString()!, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (document.RootElement.TryGetProperty("temperature", out var temperature)
                    && temperature.TryGetProperty("current", out var current)
                    && current.TryGetDouble(out var value))
                    return IsValidTemperature(value) ? value : null;

                if (document.RootElement.TryGetProperty("nvme_smart_health_information_log", out var health)
                    && health.TryGetProperty("temperature", out var nvmeTemperature)
                    && nvmeTemperature.TryGetDouble(out var nvmeValue))
                    return IsValidTemperature(nvmeValue) ? nvmeValue : null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        return null;
    }

    private static string Run(string executable, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("smartctl timed out");
        }

        Task.WaitAll(outputTask, errorTask);
        return outputTask.Result;
    }

    private static IEnumerable<string> SplitLines(string output) =>
        output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private static string? FindSmartctl()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "smartmontools", "smartctl.exe"),
            Path.Combine(AppContext.BaseDirectory, "smartctl.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "smartmontools", "bin", "smartctl.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "smartmontools", "bin", "smartctl.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsValidTemperature(double value) => value is > 0 and < 150;
}

using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;

namespace LenovoLoqControl.UI;

public partial class ProjectsView : UserControl
{
    private readonly IHardwareBackend _hardware;

    public ProjectsView(IHardwareBackend hardware)
    {
        InitializeComponent();
        _hardware = hardware;
        BuildWorkflows();
    }

    private void BuildWorkflows()
    {
        AddWorkflow("Hardware capability refresh",
            "Re-probe the verified Lenovo fan, keyboard-light, and NVIDIA interfaces.",
            "Refresh capabilities", RefreshCapabilitiesAsync);
        AddWorkflow("Telemetry snapshot",
            "Read one shared temperature, utilization, clock, fan, memory, and battery snapshot.",
            "Read telemetry", ReadTelemetryAsync);
        AddWorkflow("Balanced recovery",
            "Apply the verified Automatic/Balanced firmware mode. No custom or undocumented command is used.",
            "Apply Balanced", ApplyBalancedAsync);
        AddWorkflow("Background service status",
            "Inspect the optional persistent hardware service installed for MSI integration.",
            "Check service", CheckServiceAsync);
    }

    private void AddWorkflow(
        string title,
        string description,
        string actionText,
        Func<Task<string>> action)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card.Metric"),
            Margin = new Thickness(0, 0, 14, 14)
        };
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Text.Title") });
        root.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("Text.BodySecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14)
        });
        var button = new Button
        {
            Content = actionText,
            Style = (Style)FindResource("Button.Primary"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            ShowResult($"Running {title}…", null);
            try
            {
                ShowResult(await action(), true);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       or UnauthorizedAccessException
                                       or System.TimeoutException
                                       or System.Management.ManagementException)
            {
                ShowResult($"{title} failed: {ex.Message}", false);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        root.Children.Add(button);
        card.Child = root;
        WorkflowGrid.Children.Add(card);
    }

    private Task<string> RefreshCapabilitiesAsync()
    {
        var keyboard = _hardware.KeyboardLight.ZoneDescription;
        var gpu = _hardware.GpuOverclock.IsSupported
            ? "NVIDIA control available"
            : "NVIDIA control unavailable";
        return Task.FromResult(_hardware.FanController.IsSupported
            ? $"Capabilities refreshed: fan control available; {keyboard}; {gpu}."
            : $"Capabilities refreshed: monitoring only; {keyboard}; {gpu}.");
    }

    private async Task<string> ReadTelemetryAsync()
    {
        var reading = await _hardware.Monitor.ReadAsync(CancellationToken.None);
        return $"Telemetry captured: CPU {Format(reading.CpuTemperature, "°C")}, GPU {Format(reading.GpuTemperature, "°C")}, CPU fan {Format(reading.CpuFanRpm, " RPM")}.";
    }

    private async Task<string> ApplyBalancedAsync()
    {
        var result = await _hardware.FanController.SetFanModeAsync(FanMode.Balanced, CancellationToken.None);
        return result.Message;
    }

    private static Task<string> CheckServiceAsync()
    {
        try
        {
            using var service = new ServiceController("LoqControlHardwareService");
            service.Refresh();
            return Task.FromResult($"LoqControlHardwareService is {service.Status}.");
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult("LoqControlHardwareService is not installed.");
        }
    }

    private static string Format(double? value, string suffix) =>
        value is double number ? $"{number:0.0}{suffix}" : "unavailable";

    private void ShowResult(string message, bool? accepted)
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultText.Text = message;
        ResultBanner.Style = (Style)FindResource(accepted switch
        {
            true => "Badge.Success",
            false => "Badge.Warning",
            _ => "Badge.Neutral"
        });
    }
}

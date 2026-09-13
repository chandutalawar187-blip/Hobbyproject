using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;
using LenovoLoqControl.Services;

namespace LenovoLoqControl.UI;

public partial class SettingsView : UserControl
{
    private readonly Core.IHardwareBackend _hardware;
    private readonly LenovoVantageDisabler _imController = new();

    public SettingsView(Core.IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        FanProviderStatus.Text = _hardware.FanController.IsSupported
            ? "Provider status · Ready for firmware mode commands"
            : $"Provider status · {_hardware.FanController.AvailabilityMessage}";
        ReducedMotion.IsChecked = AppUiPreferences.ReducedMotion;
        ReducedMotion.Checked += (_, _) => AppUiPreferences.ReducedMotion = true;
        ReducedMotion.Unchecked += (_, _) => AppUiPreferences.ReducedMotion = false;
        Loaded += async (_, _) =>
        {
            await RefreshImControllerAsync();
            await RefreshCurveAvailabilityAsync();
        };
    }

    private async Task RefreshCurveAvailabilityAsync()
    {
        try
        {
            var table = await _hardware.FanController.ReadCustomFanTableAsync(CancellationToken.None);
            CurveAvailability.Text = table is null
                ? "Custom curve availability · Unavailable on this firmware"
                : $"Custom curve availability · {table.TemperaturesCelsius.Count} firmware points readable";
        }
        catch (Exception ex)
        {
            CurveAvailability.Text = $"Custom curve availability · Error: {ex.Message}";
        }
    }

    private async Task RefreshImControllerAsync()
    {
        try
        {
            var status = await _imController.GetStatusAsync(CancellationToken.None);
            ImStatus.Text = status.Message;
            DisableIm.IsEnabled = status.Installed && status.Enabled;
            EnableIm.IsEnabled = status.Installed && !status.Enabled;
        }
        catch (Exception ex)
        {
            ImStatus.Text = $"Unable to inspect ImController: {ex.Message}";
            DisableIm.IsEnabled = false;
            EnableIm.IsEnabled = false;
        }
    }

    private async void DisableImClick(object sender, RoutedEventArgs e) =>
        await ChangeImControllerAsync(false);

    private async void EnableImClick(object sender, RoutedEventArgs e) =>
        await ChangeImControllerAsync(true);

    private async Task ChangeImControllerAsync(bool enable)
    {
        var action = enable ? "re-enable" : "disable";
        if (!enable)
        {
            var answer = MessageBox.Show(
                "Disable Lenovo Vantage and ImController now?\n\nThis stops both services, disables Lenovo scheduled tasks, and closes related processes. Lenovo Vantage features, hotkeys, and other Lenovo integrations may stop working. You can re-enable them here later.",
                "Confirm Lenovo service change",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        DisableIm.IsEnabled = false;
        EnableIm.IsEnabled = false;
        ImStatus.Text = $"{char.ToUpperInvariant(action[0])}{action[1..]}ing ImController…";
        try
        {
            var status = enable
                ? await _imController.EnableAsync(CancellationToken.None)
                : await _imController.DisableAsync(CancellationToken.None);
            ImStatus.Text = status.Message;
        }
        catch (Exception ex)
        {
            ImStatus.Text = $"Unable to {action} ImController: {ex.Message}";
        }
        finally
        {
            await RefreshImControllerAsync();
        }
    }

    private void OpenDiagnosticsHintClick(object sender, RoutedEventArgs e)
    {
        AdvancedStatus.Text = "Use the Diagnostics page in the navigation rail for the full compatibility report.";
        AdvancedStatus.Foreground = (Brush)FindResource("Brush.Information");
    }

    private void ExportSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("LOQ Control — Settings Report");
            sb.AppendLine($"Generated: {DateTimeOffset.Now:u}");
            sb.AppendLine($"StartWithWindows: {StartWithWindows.IsChecked}");
            sb.AppendLine($"StartMinimized: {StartMinimized.IsChecked}");
            sb.AppendLine($"RememberProfile: {RememberProfile.IsChecked}");
            sb.AppendLine($"Notifications: {NotificationsEnabled.IsChecked}");
            sb.AppendLine($"ReducedMotion: {ReducedMotion.IsChecked}");
            sb.AppendLine($"Logging: {EnableLogging.IsChecked}");
            sb.AppendLine($"DebugMode: {DebugMode.IsChecked}");
            sb.AppendLine($"FanSupported: {_hardware.FanController.IsSupported}");
            sb.AppendLine($"FanMessage: {_hardware.FanController.AvailabilityMessage}");
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"LOQ-Control-Settings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            AdvancedStatus.Text = $"Exported to {path}";
            AdvancedStatus.Foreground = (Brush)FindResource("Brush.Success");
        }
        catch (Exception ex)
        {
            AdvancedStatus.Text = $"Export failed: {ex.Message}";
            AdvancedStatus.Foreground = (Brush)FindResource("Brush.Critical");
        }
    }
}

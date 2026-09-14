using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    private readonly UpdateService _updates = new();
    private AppUpdateInfo? _availableUpdate;
    private CancellationTokenSource? _updateCts;

    public SettingsView(Core.IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        VersionText.Text = $"Version {UpdateService.DisplayVersion}";
        FanProviderStatus.Text = _hardware.FanController.IsSupported
            ? "Provider status · Ready for firmware mode commands"
            : $"Provider status · {_hardware.FanController.AvailabilityMessage}";
        ReducedMotion.IsChecked = AppUiPreferences.ReducedMotion;
        RunInTrayOnClose.IsChecked = AppUiPreferences.RunInTrayOnClose;
        ReducedMotion.Checked += (_, _) => AppUiPreferences.ReducedMotion = true;
        ReducedMotion.Unchecked += (_, _) => AppUiPreferences.ReducedMotion = false;
        RunInTrayOnClose.Checked += (_, _) => AppUiPreferences.RunInTrayOnClose = true;
        RunInTrayOnClose.Unchecked += (_, _) => AppUiPreferences.RunInTrayOnClose = false;
        Loaded += async (_, _) =>
        {
            await RefreshImControllerAsync();
            await RefreshCurveAvailabilityAsync();
            await CheckForUpdatesAsync();
        };
        Unloaded += (_, _) => _updateCts?.Cancel();
    }

    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking for updates…";
        try
        {
            _availableUpdate = await _updates.CheckForUpdateAsync(_updateCts.Token);
            if (_availableUpdate is null)
            {
                UpdateStatus.Text = $"You are up to date (v{UpdateService.CurrentVersion.ToString(3)}).";
                return;
            }

            UpdateStatus.Text = $"Version {_availableUpdate.Version.ToString(3)} is available.";
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Update check cancelled.";
        }
        catch (HttpRequestException ex)
        {
            UpdateStatus.Text = $"Unable to check for updates: {ex.Message}";
        }
        catch (JsonException ex)
        {
            UpdateStatus.Text = $"The release information was invalid: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
            return;

        var update = _availableUpdate;
        var answer = MessageBox.Show(
            $"Download LOQ Control v{update.Version.ToString(3)} now?",
            "LOQ Control update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
            return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        try
        {
            UpdateStatus.Text = $"Downloading {update.InstallerName}…";
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            var installer = await _updates.DownloadInstallerAsync(update, progress, CancellationToken.None);
            var closeAnswer = MessageBox.Show(
                "The update has finished downloading. LOQ Control must close before the installer can replace it. Close the current session and install the update now?",
                "LOQ Control update ready",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (closeAnswer != MessageBoxResult.Yes)
            {
                UpdateStatus.Text = "Update downloaded. Installation was postponed.";
                InstallUpdateButton.IsEnabled = true;
                CheckUpdatesButton.IsEnabled = true;
                return;
            }

            UpdateService.LaunchInstallerAndRestart(installer);
            if (Application.Current.MainWindow is MainWindow mainWindow)
                mainWindow.CloseForUpdate();
            else
                Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UpdateStatus.Text = $"Update failed: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
        }
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

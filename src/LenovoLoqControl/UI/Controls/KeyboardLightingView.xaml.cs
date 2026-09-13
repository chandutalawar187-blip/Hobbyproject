using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LenovoLoqControl.Core;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

public partial class KeyboardLightingView : UserControl
{
    private readonly DropShadowEffect _selectedGlow = new()
    {
        Color = Color.FromRgb(0xE2, 0x23, 0x1A),
        BlurRadius = 14,
        ShadowDepth = 0,
        Opacity = 0.42,
        RenderingBias = RenderingBias.Performance
    };

    private KeyboardLightingPresenter? _presenter;
    private CancellationTokenSource? _lifetimeCts;
    private bool _stylesReady;
    private bool _syncingDeviceState;
    private bool _rgbColorSpeedStacked;
    private CancellationTokenSource? _rgbPreviewCts;
    private Task? _deviceSyncTask;
    private Style? _levelIdleStyle;
    private Style? _levelSelectedStyle;

    public KeyboardLightingView()
    {
        InitializeComponent();
        Loaded += OnLoadedAsync;
        Unloaded += OnUnloaded;
    }

    private void RgbColorSpeedGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;
        AdaptRgbColorSpeedLayout(e.NewSize.Width);
    }

    private void AdaptRgbColorSpeedLayout(double width)
    {
        if (RgbColorSpeedGrid is null || RgbColorPanel is null || RgbSpeedPanel is null)
            return;

        var stack = width < ResponsiveLayout.BreakpointMedium;
        if (stack == _rgbColorSpeedStacked)
            return;

        _rgbColorSpeedStacked = stack;
        ResponsiveLayout.Transition(RgbColorSpeedGrid, () =>
        {
            if (stack)
            {
                RgbColorSpeedGap.Width = new GridLength(0);
                RgbColorSpeedRowGap.Height = new GridLength(14);
                Grid.SetColumn(RgbColorPanel, 0);
                Grid.SetRow(RgbColorPanel, 0);
                Grid.SetColumnSpan(RgbColorPanel, 3);
                Grid.SetColumn(RgbSpeedPanel, 0);
                Grid.SetRow(RgbSpeedPanel, 2);
                Grid.SetColumnSpan(RgbSpeedPanel, 3);
            }
            else
            {
                RgbColorSpeedGap.Width = new GridLength(16);
                RgbColorSpeedRowGap.Height = new GridLength(0);
                Grid.SetColumn(RgbColorPanel, 0);
                Grid.SetRow(RgbColorPanel, 0);
                Grid.SetColumnSpan(RgbColorPanel, 1);
                Grid.SetColumn(RgbSpeedPanel, 2);
                Grid.SetRow(RgbSpeedPanel, 0);
                Grid.SetColumnSpan(RgbSpeedPanel, 1);
            }
        }, RgbColorSpeedGrid);
    }

    /// <summary>Binds this control to the shared keyboard-light backend (no new hardware objects).</summary>
    public void Attach(IKeyboardLightController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        if (_presenter is not null)
            _presenter.StateChanged -= OnPresenterStateChanged;

        _presenter = new KeyboardLightingPresenter(controller);
        _presenter.StateChanged += OnPresenterStateChanged;
        _presenter.SyncFromBackendMetadata();
        ApplyState(_presenter.State);
    }

    /// <summary>Test seam for the active presenter.</summary>
    internal KeyboardLightingPresenter? Presenter => _presenter;

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        EnsureStyles();
        if (_presenter is null)
            return;

        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        var lifetimeCts = new CancellationTokenSource();
        _lifetimeCts = lifetimeCts;

        _presenter.SyncFromBackendMetadata();
        ApplyState(_presenter.State);
        UpdateLiveKeyboardPreview();

        try
        {
            await _presenter.RefreshAsync(lifetimeCts.Token);
            if (ReferenceEquals(_lifetimeCts, lifetimeCts) && !lifetimeCts.IsCancellationRequested)
                _deviceSyncTask = SyncDeviceStateAsync(lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SyncDeviceStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            await _presenter!.RefreshAsync(cancellationToken);
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var lifetimeCts = _lifetimeCts;
        lifetimeCts?.Cancel();
        _rgbPreviewCts?.Cancel();
        var deviceSyncTask = _deviceSyncTask;
        _deviceSyncTask = null;
        _rgbPreviewCts?.Dispose();
        _lifetimeCts = null;
        _rgbPreviewCts = null;
        ClearSelectionGlow();
        KeyboardPreview.RgbEffect = null;

        if (deviceSyncTask is not null)
        {
            try
            {
                await deviceSyncTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetimeCts?.Dispose();
    }

    private void OnPresenterStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ApplyPresenterState);
            return;
        }

        ApplyPresenterState();
    }

    private void ApplyPresenterState()
    {
        if (_presenter is null)
            return;
        // Drop updates after unload so a completed Task cannot paint stale success/error.
        if (_lifetimeCts is null || _lifetimeCts.IsCancellationRequested)
            return;
        ApplyState(_presenter.State);
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _levelIdleStyle = TryFindStyle("Keyboard.LevelButton");
        _levelSelectedStyle = TryFindStyle("Keyboard.LevelButtonSelected");
        _stylesReady = true;
    }

    private Style? TryFindStyle(string key)
    {
        if (Resources[key] is Style local)
            return local;
        return TryFindResource(key) as Style;
    }

    private void ApplyState(KeyboardLightingState state)
    {
        EnsureStyles();

        var isFourZone = state.UiMode == KeyboardLightingUiMode.FourZoneRgb;
        var isRgbComingLater = state.UiMode is KeyboardLightingUiMode.TwentyFourZoneRgbComingLater
            or KeyboardLightingUiMode.RgbLayoutUnknown;

        TitleText.Text = state.UiMode == KeyboardLightingUiMode.WhiteBacklit
            ? "Keyboard Lighting"
            : state.Headline;
        AvailabilityText.Text = state.DetailBody;
        TypeBadgeText.Text = state.TypeBadgeText;

        WhitePanel.Visibility = state.ShowWhiteControls ? Visibility.Visible : Visibility.Collapsed;
        RgbInfoPanel.Visibility = isFourZone ? Visibility.Visible : Visibility.Collapsed;
        RgbComingLaterPanel.Visibility = isRgbComingLater ? Visibility.Visible : Visibility.Collapsed;
        UnsupportedPanel.Visibility = Visibility.Collapsed;

        var showKeyboard = state.ShowWhiteControls || state.ShowRgbComingSoon;
        KeyboardPreview.Visibility = showKeyboard ? Visibility.Visible : Visibility.Collapsed;
        KeyboardPreview.PreferReducedMotion = AppUiPreferences.ReducedMotion;
        KeyboardPreview.ShowZoneHints = isFourZone;

        if (state.ShowWhiteControls)
        {
            KeyboardPreview.LightLevel = state.CurrentLevel ?? state.SelectedLevel;
            KeyboardPreview.RgbEffect = null;
            KeyboardPreview.RgbPreviewColor = null;
        }
        else if (isFourZone)
        {
            KeyboardPreview.LightLevel = null;
            if (state.LastConfirmedRgbSettings is { } confirmed)
                ApplyConfirmedRgbControls(confirmed);
            if (!state.IsBusy && state.LastConfirmedRgbSettings is not null)
                UpdateLiveKeyboardPreview(state.LastConfirmedRgbSettings);
            else
                UpdateLiveKeyboardPreview();
        }
        else
        {
            KeyboardPreview.LightLevel = null;
            KeyboardPreview.RgbEffect = null;
            KeyboardPreview.RgbPreviewColor = null;
        }

        if (isFourZone)
        {
            RgbHeadline.Text = state.Headline;
            RgbDetail.Text = state.DetailBody;
            RgbInfoBody.Text = KeyboardLightingPresentation.RgbInfoPanelText(state.UiMode);
            RgbLinkStatus.Text = state.IsBusy ? "LINK · BUSY" : "LINK · READY";
        }

        if (isRgbComingLater)
        {
            RgbComingLaterHeadline.Text = state.Headline;
            RgbComingLaterDetail.Text = state.DetailBody + " " +
                KeyboardLightingPresentation.RgbInfoPanelText(state.UiMode);
        }

        if (state.UiMode == KeyboardLightingUiMode.Unsupported)
            UnsupportedDetail.Text = state.DetailBody;

        CurrentLevelText.Text = state.CurrentLevelLabel;
        StatusText.Text = state.StatusMessage;
        StatusBanner.Style = (Style)FindResource(BadgeStyleKey(state.StatusKind));
        StatusBanner.Visibility = state.ShowWhiteControls || isFourZone
            ? Visibility.Visible : Visibility.Collapsed;
        TypeBadge.Style = (Style)FindResource(state.UiMode == KeyboardLightingUiMode.Unsupported
            ? "Badge.Neutral"
            : state.ShowRgbComingSoon ? "Badge.Info" : "Badge.Mode");

        var enabled = state.ControlsEnabled;
        LevelOff.IsEnabled = enabled;
        LevelLow.IsEnabled = enabled;
        LevelHigh.IsEnabled = enabled;
        RefreshButton.IsEnabled = !state.IsBusy;
        RgbEffectPanel.IsEnabled = isFourZone && !state.IsBusy;
        RgbApplyButton.IsEnabled = isFourZone && !state.IsBusy;

        HighlightLevel(state.SelectedLevel, state.ShowWhiteControls && !AppUiPreferences.ReducedMotion);
    }

    private void HighlightLevel(KeyboardLightLevel? level, bool allowGlow)
    {
        ClearSelectionGlow();
        ApplyLevelStyle(LevelOff, level == KeyboardLightLevel.Off);
        ApplyLevelStyle(LevelLow, level == KeyboardLightLevel.Low);
        ApplyLevelStyle(LevelHigh, level == KeyboardLightLevel.High);

        if (!allowGlow || level is null)
            return;

        var selected = level switch
        {
            KeyboardLightLevel.Off => LevelOff,
            KeyboardLightLevel.Low => LevelLow,
            KeyboardLightLevel.High => LevelHigh,
            _ => null
        };
        if (selected is not null)
            selected.Effect = _selectedGlow;
    }

    private void ApplyLevelStyle(Button button, bool selected)
    {
        if (_levelIdleStyle is null || _levelSelectedStyle is null)
            return;
        button.Style = selected ? _levelSelectedStyle : _levelIdleStyle;
    }

    private void ClearSelectionGlow()
    {
        LevelOff.Effect = null;
        LevelLow.Effect = null;
        LevelHigh.Effect = null;
    }

    private static string BadgeStyleKey(KeyboardLightingStatusKind kind) => kind switch
    {
        KeyboardLightingStatusKind.Success => "Badge.Success",
        KeyboardLightingStatusKind.Warning => "Badge.Warning",
        KeyboardLightingStatusKind.Error => "Badge.Critical",
        _ => "Badge.Neutral"
    };

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (_presenter is null || _lifetimeCts is null)
            return;
        try
        {
            await _presenter.RefreshAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void LevelOffClick(object sender, RoutedEventArgs e) =>
        await SelectAsync(KeyboardLightLevel.Off);

    private async void LevelLowClick(object sender, RoutedEventArgs e) =>
        await SelectAsync(KeyboardLightLevel.Low);

    private async void LevelHighClick(object sender, RoutedEventArgs e) =>
        await SelectAsync(KeyboardLightLevel.High);

    private async Task SelectAsync(KeyboardLightLevel level)
    {
        if (_presenter is null || _lifetimeCts is null)
            return;
        try
        {
            await _presenter.SelectLevelAsync(level, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void ApplyRgbClick(object sender, RoutedEventArgs e)
    {
        if (_presenter is null || _lifetimeCts is null)
            return;
        if (!TryGetRgbSettings(out var settings))
        {
            StatusText.Text = "Enter a color in #RRGGBB format.";
            return;
        }

        UpdateLiveKeyboardPreview(settings);
        await _presenter.ApplyRgbEffectAsync(settings, _lifetimeCts.Token);
    }

    private void RgbEffectChipChecked(object sender, RoutedEventArgs e)
    {
        // Checked events can fire while BAML is still assigning named fields / during init sync.
        if (!IsInitialized || !IsLoaded || _syncingDeviceState)
            return;

        if (sender is not RadioButton { IsChecked: true })
            return;

        UpdateSignalColorVisibility(EffectColorCycle.IsChecked == true);
        QueueRgbPreview();
    }

    private void RgbPreviewChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || !IsLoaded || _syncingDeviceState)
            return;
        QueueRgbPreview();
    }

    private void ApplyConfirmedRgbControls(KeyboardRgbSettings settings)
    {
        _syncingDeviceState = true;
        try
        {
            EffectOff.IsChecked = settings.Effect == KeyboardRgbEffect.Off;
            EffectStatic.IsChecked = settings.Effect == KeyboardRgbEffect.Static;
            EffectBreathing.IsChecked = settings.Effect == KeyboardRgbEffect.Breathing;
            EffectColorCycle.IsChecked = settings.Effect == KeyboardRgbEffect.ColorCycle;
            EffectWave.IsChecked = settings.Effect == KeyboardRgbEffect.Wave;
            RgbColorText.Text = $"#{settings.Color.Red:X2}{settings.Color.Green:X2}{settings.Color.Blue:X2}";
            RgbSpeedSlider.Value = settings.Speed;
            RgbBrightnessLow.IsChecked = settings.Brightness == KeyboardRgbBrightness.Low;
            RgbBrightnessHigh.IsChecked = settings.Brightness == KeyboardRgbBrightness.High;
            UpdateSignalColorVisibility(settings.Effect == KeyboardRgbEffect.ColorCycle);
        }
        finally
        {
            _syncingDeviceState = false;
        }
    }

    /// <summary>
    /// Color Cycle owns its own palette — hide Signal Color / picker immediately.
    /// Static, Breathing, Wave, and Off restore the section. Speed stays visible.
    /// </summary>
    private void UpdateSignalColorVisibility(bool hideForColorCycle)
    {
        if (RgbColorPanel is null || ColorPickerPopup is null)
            return;

        if (hideForColorCycle)
            ColorPickerPopup.IsOpen = false;

        RgbColorPanel.Visibility = hideForColorCycle ? Visibility.Collapsed : Visibility.Visible;
        if (RgbColorColumn is not null)
            RgbColorColumn.Width = hideForColorCycle ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (RgbColorSpeedGap is not null)
            RgbColorSpeedGap.Width = hideForColorCycle ? new GridLength(0) : new GridLength(16);
        // Keep speed in its own column so collapsing Signal Color never zeroes it out.
        if (RgbSpeedPanel is not null)
            Grid.SetColumn(RgbSpeedPanel, 2);
    }

    private async void QueueRgbPreview()
    {
        if (!IsInitialized || !IsLoaded || _syncingDeviceState
            || _presenter is null || _lifetimeCts is null || _lifetimeCts.IsCancellationRequested)
            return;
        if (!TryGetRgbSettings(out var settings))
            return;

        // Immediate visual preview on the silhouette (live effect).
        UpdateLiveKeyboardPreview(settings);

        _rgbPreviewCts?.Cancel();
        _rgbPreviewCts?.Dispose();
        _rgbPreviewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        try
        {
            await Task.Delay(280, _rgbPreviewCts.Token);
            await _presenter.PreviewRgbEffectAsync(settings, _rgbPreviewCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateLiveKeyboardPreview(KeyboardRgbSettings? settings = null)
    {
        if (settings is null && !TryGetRgbSettings(out settings))
        {
            KeyboardPreview.RgbEffect = KeyboardRgbEffect.Static;
            KeyboardPreview.RgbPreviewColor = Colors.White;
            KeyboardPreview.RgbSpeed = 3;
            KeyboardPreview.RgbBrightness = KeyboardRgbBrightness.High;
            RgbSpeedValue.Text = "03";
            RgbSpeedSlider.IsEnabled = true;
            RgbSpeedValue.Opacity = 1;
            RgbSpeedPanel.Visibility = Visibility.Visible;
            UpdateSignalColorVisibility(hideForColorCycle: false);
            return;
        }

        KeyboardPreview.RgbEffect = settings.Effect;
        KeyboardPreview.RgbSpeed = settings.Speed;
        KeyboardPreview.RgbBrightness = settings.Brightness;
        KeyboardPreview.RgbPreviewColor = settings.Effect == KeyboardRgbEffect.Off
            ? null
            : Color.FromRgb(settings.Color.Red, settings.Color.Green, settings.Color.Blue);

        RgbColorSwatchButton.Background = new SolidColorBrush(
            Color.FromRgb(settings.Color.Red, settings.Color.Green, settings.Color.Blue));
        if (ColorPickerPopup.IsOpen && settings.Effect != KeyboardRgbEffect.ColorCycle)
            ColorPicker.SetColor(settings.Color, raiseEvent: false);

        RgbSpeedValue.Text = settings.Effect == KeyboardRgbEffect.Static
            ? "N/A"
            : settings.Speed.ToString("00");
        RgbSpeedSlider.IsEnabled = settings.Effect != KeyboardRgbEffect.Static;
        RgbSpeedValue.Opacity = settings.Effect == KeyboardRgbEffect.Static ? 0.45 : 1;
        RgbSpeedPanel.Visibility = settings.Effect == KeyboardRgbEffect.Static
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateSignalColorVisibility(settings.Effect == KeyboardRgbEffect.ColorCycle);
    }

    private void OpenColorPickerClick(object sender, RoutedEventArgs e)
    {
        if (EffectColorCycle.IsChecked == true)
            return;

        if (TryParseHex(RgbColorText.Text, out var color))
            ColorPicker.SetColor(color, raiseEvent: false);
        else
            ColorPicker.SetColor(Colors.White, raiseEvent: false);

        ColorPickerPopup.IsOpen = true;
    }

    private void ColorPickerColorChanged(object? sender, Color color)
    {
        _syncingDeviceState = true;
        try
        {
            RgbColorText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            RgbColorSwatchButton.Background = new SolidColorBrush(color);
        }
        finally
        {
            _syncingDeviceState = false;
        }

        if (TryGetRgbSettings(out var settings))
            UpdateLiveKeyboardPreview(settings);
        QueueRgbPreview();
    }

    private bool TryGetRgbSettings(out KeyboardRgbSettings settings)
    {
        settings = KeyboardRgbSettings.Default;
        if (!TryParseHex(RgbColorText.Text, out var color))
            return false;

        var effect =
            EffectOff.IsChecked == true ? KeyboardRgbEffect.Off :
            EffectBreathing.IsChecked == true ? KeyboardRgbEffect.Breathing :
            EffectColorCycle.IsChecked == true ? KeyboardRgbEffect.ColorCycle :
            EffectWave.IsChecked == true ? KeyboardRgbEffect.Wave :
            KeyboardRgbEffect.Static;

        settings = new KeyboardRgbSettings(
            effect,
            color,
            (byte)Math.Clamp((int)RgbSpeedSlider.Value, 1, 4),
            RgbBrightnessLow.IsChecked == true
                ? KeyboardRgbBrightness.Low
                : KeyboardRgbBrightness.High);
        return true;
    }

    private static bool TryParseHex(string? text, out KeyboardRgbColor color)
    {
        color = default;
        var value = text?.Trim().TrimStart('#');
        if (value?.Length != 6 || !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(value[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(value[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;
        color = new KeyboardRgbColor(r, g, b);
        return true;
    }
}

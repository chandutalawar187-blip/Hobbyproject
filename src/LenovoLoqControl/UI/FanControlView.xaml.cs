using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;
using LenovoLoqControl.UI.Controls;

namespace LenovoLoqControl.UI;

public partial class FanControlView : UserControl
{
    private readonly IHardwareBackend _hardware;
    private readonly DispatcherTimer _liveTimer;
    private FirmwareFanTable? _fanTable;
    private FanCurveGraphEditor? _curveGraph;
    private TextBlock? _summaryTemp;
    private TextBlock? _summaryPercent;
    private TextBlock? _summaryRpm;
    private TextBlock? _summaryStep;
    private TextBlock? _summarySafety;
    private TextBlock? _validationHint;
    private Slider? _fixedSpeedSlider;
    private Button? _selectedModeButton;
    private bool _refreshing;
    private readonly DropShadowEffect _modeButtonGlow = new()
    {
        BlurRadius = 16,
        ShadowDepth = 0,
        Opacity = 0,
        RenderingBias = RenderingBias.Performance
    };
    private static readonly CubicEase ModeAccentEase = new() { EasingMode = EasingMode.EaseInOut };

    public FanControlView(IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        var identity = _hardware.Monitor.Identity;
        var supported = _hardware.FanController.IsSupported;
        CapabilityTitle.Text = supported ? "Firmware fan modes available" : "Fan control unavailable";
        CapabilityDetail.Text = supported
            ? $"Model {identity.Model}: Silent, Automatic, Performance, Max Cooling, live RPM, and firmware custom curves are available when the Lenovo provider permits them."
            : $"Model {identity.Model}: {_hardware.FanController.AvailabilityMessage}";
        CapabilityBanner.Style = (Style)FindResource(supported ? "Card.SuccessBanner" : "Card.Warning");

        StatusText.Text = supported
            ? "Ready. No command has been sent yet."
            : _hardware.FanController.AvailabilityMessage;

        SetModeButtonsEnabled(supported);
        GpuOverclockStatus.Text = _hardware.GpuOverclock.AvailabilityMessage;
        GpuCoreOffset.IsEnabled = _hardware.GpuOverclock.IsSupported;
        GpuMemoryOffset.IsEnabled = _hardware.GpuOverclock.IsSupported;

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) => await RefreshLiveReadingsAsync();
        Loaded += async (_, _) =>
        {
            PlayTelemetrySectionEnter();
            ApplyTelemetryAccent(FanMode.Quiet);
            _liveTimer.Start();
            await RefreshLiveReadingsAsync();
            await LoadFanTableAsync();
        };
        Unloaded += (_, _) =>
        {
            _liveTimer.Stop();
            DetachCurveGraph();
            ClearModeButtonGlowEffects();
            _modeButtonGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            _modeButtonGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        };
    }

    private void PlayTelemetrySectionEnter()
    {
        // Explicit Legion-style stagger for the LIVE TELEMETRY hexes (control.png / ui.png).
        UiAnimation.PlayEnter(CpuModule, 0);
        UiAnimation.PlayEnter(RamModule, 1);
        UiAnimation.PlayEnter(GpuModule, 2);
        if (ModePanel is FrameworkElement modeStrip)
            UiAnimation.PlayEnter(modeStrip, 3);
    }

    private void SetModeButtonsEnabled(bool enabled)
    {
        foreach (var button in new[] { ModeSilent, ModeAuto, ModePerformance, ModeMaxCooling })
        {
            button.IsEnabled = enabled;
            if (!enabled)
                button.ToolTip = _hardware.FanController.AvailabilityMessage;
        }
    }

    private async Task LoadFanTableAsync()
    {
        DetachCurveGraph();
        CurvePanel.Children.Clear();
        FixedSpeedPanel.Children.Clear();

        var table = await _hardware.FanController.ReadCustomFanTableAsync(CancellationToken.None);
        if (table is null)
        {
            ModeCustom.IsEnabled = false;
            CurvePanel.Children.Add(new TextBlock
            {
                Text = "Custom fan-table data is unavailable on this firmware. Curve editing is disabled.",
                Style = (Style)FindResource("Text.BodySecondary"),
                TextWrapping = TextWrapping.Wrap
            });
            FixedSpeedPanel.Children.Add(new TextBlock
            {
                Text = "Fixed fan speed requires a readable firmware fan table.",
                Style = (Style)FindResource("Text.BodySecondary"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        _fanTable = table;
        ModeCustom.IsEnabled = _hardware.FanController.IsSupported;
        BuildFixedSpeedControls();
        BuildCurveEditor(table);
    }

    private void DetachCurveGraph()
    {
        if (_curveGraph is null) return;
        _curveGraph.CurveChanged -= OnCurveChanged;
        _curveGraph.SelectionChanged -= OnCurveSelectionChanged;
        _curveGraph = null;
        _summaryTemp = null;
        _summaryPercent = null;
        _summaryRpm = null;
        _summaryStep = null;
        _summarySafety = null;
        _validationHint = null;
    }

    private void BuildFixedSpeedControls()
    {
        FixedSpeedPanel.Children.Clear();
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 10,
            Value = 5,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Firmware step 1 (lowest) through 10 (highest)"
        };
        _fixedSpeedSlider = slider;
        var value = new TextBlock
        {
            Text = "Firmware step 5 / 10",
            Margin = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 140
        };
        slider.ValueChanged += (_, _) => value.Text = $"Firmware step {slider.Value:0} / 10";

        var apply = new Button
        {
            Content = "Apply fixed speed",
            Style = (Style)FindResource("Button.Primary"),
            Margin = new Thickness(0)
        };
        apply.Click += async (_, _) => await ApplyFixedSpeedAsync(apply);

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(value, 1);
        Grid.SetColumn(apply, 2);
        row.Children.Add(slider);
        row.Children.Add(value);
        row.Children.Add(apply);
        FixedSpeedPanel.Children.Add(row);
    }

    private void BuildCurveEditor(FirmwareFanTable table)
    {
        DetachCurveGraph();
        CurvePanel.Children.Clear();

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220), MinWidth = 180 });

        var graph = new FanCurveGraphEditor
        {
            Height = 320,
            Margin = new Thickness(0, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        graph.LoadFirmwareTable(table);
        graph.CurveChanged += OnCurveChanged;
        graph.SelectionChanged += OnCurveSelectionChanged;
        _curveGraph = graph;
        Grid.SetColumn(graph, 0);
        layout.Children.Add(graph);

        var side = BuildCurveSummaryPanel();
        Grid.SetColumn(side, 1);
        layout.Children.Add(side);
        CurvePanel.Children.Add(layout);

        CurvePanel.Children.Add(BuildCurveLegend());
        CurvePanel.Children.Add(BuildCurveActions());

        _validationHint = new TextBlock
        {
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "Thermal safety: ≥85°C requires at least 60% · ≥95°C requires 100%. The backend rejects unsafe curves."
        };
        CurvePanel.Children.Add(_validationHint);

        UpdateCurveSummary();
        UpdateLocalValidationHint();
    }

    private Border BuildCurveSummaryPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Selected point",
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        _summaryTemp = AddSummaryRow(panel, "Temperature", "—");
        _summaryPercent = AddSummaryRow(panel, "Fan percentage", "—");
        _summaryRpm = AddSummaryRow(panel, "Estimated fan speed", "—");
        _summaryStep = AddSummaryRow(panel, "Firmware step", "—");
        _summarySafety = AddSummaryRow(panel, "Safety status", "—");

        var clear = new Button
        {
            Content = "Clear selection",
            Style = (Style)FindResource("Button.Ghost"),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        clear.Click += (_, _) => _curveGraph?.ClearSelection();
        panel.Children.Add(clear);

        panel.Children.Add(new TextBlock
        {
            Text = "Temperature positions are firmware-defined and locked. Only fan percentage is editable.",
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Style = (Style)FindResource("Card.Compact"),
            Padding = new Thickness(14),
            Child = panel
        };
    }

    private TextBlock AddSummaryRow(Panel parent, string label, string value)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        var block = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("Text.Body"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        parent.Children.Add(block);
        return block;
    }

    private StackPanel BuildCurveLegend()
    {
        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        legend.Children.Add(LegendSwatch(Color.FromRgb(0x62, 0xA8, 0xFF), "Active curve"));
        legend.Children.Add(LegendSwatch(Color.FromRgb(0xF4, 0xB9, 0x42), "Safety floor"));
        legend.Children.Add(LegendSwatch(Color.FromRgb(0xE2, 0x23, 0x1A), "Current temperature"));
        return legend;
    }

    private StackPanel LegendSwatch(Color color, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new Rectangle
        {
            Width = 14,
            Height = 3,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            RadiusX = 1,
            RadiusY = 1
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private StackPanel BuildCurveActions()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };

        var reset = new Button
        {
            Content = "Reset",
            Style = (Style)FindResource("Button.Ghost"),
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Restore default editor percentages. Does not send a firmware command."
        };
        reset.Click += (_, _) => ResetCurveEditor();

        var minimum = new Button
        {
            Content = "Minimum safe",
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Load the lowest firmware steps allowed by the table while preserving thermal safety limits, then apply."
        };
        minimum.Click += async (_, _) => await ApplyMinimumSafeCurveAsync(minimum);

        var apply = new Button
        {
            Content = "Apply curve",
            Style = (Style)FindResource("Button.Primary"),
            Margin = new Thickness(0),
            ToolTip = "Validate and send the custom curve to firmware."
        };
        apply.Click += async (_, _) => await ApplyCustomCurveAsync(apply);

        actions.Children.Add(reset);
        actions.Children.Add(minimum);
        actions.Children.Add(apply);
        return actions;
    }

    private void OnCurveChanged(object? sender, EventArgs e)
    {
        UpdateCurveSummary();
        UpdateLocalValidationHint();
    }

    private void OnCurveSelectionChanged(object? sender, EventArgs e) => UpdateCurveSummary();

    private void UpdateCurveSummary()
    {
        if (_curveGraph is null || _summaryTemp is null || _summaryPercent is null || _summaryRpm is null
            || _summaryStep is null || _summarySafety is null)
            return;

        if (!_curveGraph.TryGetSelectedSummary(out var temperature, out var percent, out var step,
            out var estimatedRpm, out var safety))
        {
            _summaryTemp.Text = "—";
            _summaryPercent.Text = "—";
            _summaryRpm.Text = "—";
            _summaryRpm.Text = "—";
            _summaryStep.Text = "—";
            _summarySafety.Text = "No point selected";
            return;
        }

        _summaryTemp.Text = $"{temperature:0}°C (locked)";
        _summaryPercent.Text = $"{percent:0}%";
        _summaryRpm.Text = estimatedRpm is int rpm ? $"~{rpm:N0} RPM" : "RPM unavailable";
        _summaryStep.Text = $"{step}/10";
        _summarySafety.Text = safety;
    }

    private void UpdateLocalValidationHint()
    {
        if (_validationHint is null || _curveGraph is null || !_curveGraph.HasPoints)
            return;

        var errors = _curveGraph.ValidateLocally();
        if (errors.Count == 0)
        {
            _validationHint.Text = "Curve looks valid locally. Apply still requires firmware acceptance.";
            _validationHint.Foreground = (Brush)FindResource("Brush.TextSecondary");
            return;
        }

        _validationHint.Text = "Validation: " + string.Join(" ", errors);
        _validationHint.Foreground = (Brush)FindResource("Brush.Warning");
    }

    private void ResetCurveEditor()
    {
        if (_curveGraph is null) return;
        _curveGraph.ResetToDefaults();
        ShowResult("Editor reset to defaults. No firmware command was sent.", accepted: null);
    }

    private async Task ApplyFixedSpeedAsync(Button apply)
    {
        var table = _fanTable;
        var slider = _fixedSpeedSlider;
        if (table is null || slider is null) return;

        apply.IsEnabled = false;
        try
        {
            var percent = slider.Value * 10;
            var points = table.TemperaturesCelsius
                .Select(temperature => new FanCurvePoint(
                    temperature,
                    temperature >= 95 ? 100 : temperature >= 85 ? Math.Max(60, percent) : percent))
                .ToArray();
            var result = await _hardware.FanController.SetFanCurveAsync(new FanCurve(points), CancellationToken.None);
            var message = result.Message;
            if (result.Accepted && table.TemperaturesCelsius.Any(temperature => temperature >= 85))
                message += " Safety minimums apply at 85°C and above.";
            ShowResult(message, result.Accepted);
            if (result.Accepted)
                HighlightMode(ModeCustom);
        }
        catch (Exception ex) when (ex is ManagementException or COMException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or ArgumentException)
        {
            ShowResult($"The firmware did not apply fixed speed: {ex.Message}", accepted: false);
        }
        finally
        {
            apply.IsEnabled = true;
        }
    }

    private async Task ApplyCustomCurveAsync(Button apply)
    {
        apply.IsEnabled = false;
        var graph = _curveGraph;
        if (graph is null || _fanTable is null)
        {
            apply.IsEnabled = true;
            return;
        }

        var curve = graph.GetCurve();
        var localErrors = FanCurveValidator.Validate(curve);
        if (localErrors.Count > 0)
        {
            ShowResult("Curve was not applied. " + string.Join(" ", localErrors), accepted: false);
            UpdateLocalValidationHint();
            apply.IsEnabled = true;
            return;
        }

        try
        {
            var result = await _hardware.FanController.SetFanCurveAsync(curve, CancellationToken.None);
            ShowResult(result.Accepted ? result.Message : $"Curve was not applied. {result.Message}", result.Accepted);
            if (result.Accepted)
                HighlightMode(ModeCustom);
        }
        catch (Exception ex) when (ex is ManagementException or COMException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or ArgumentException)
        {
            ShowResult($"The firmware did not apply the custom curve: {ex.Message}", accepted: false);
        }
        finally
        {
            apply.IsEnabled = true;
        }
    }

    private async Task ApplyMinimumSafeCurveAsync(Button minimum)
    {
        minimum.IsEnabled = false;
        try
        {
            var table = _fanTable;
            var graph = _curveGraph;
            if (table is null || graph is null)
                return;

            graph.ApplyMinimumSafePercents(table);
            var curve = graph.GetCurve();
            var localErrors = FanCurveValidator.Validate(curve);
            if (localErrors.Count > 0)
            {
                ShowResult("Minimum safe curve was prepared but not applied. " + string.Join(" ", localErrors), accepted: false);
                return;
            }

            var result = await _hardware.FanController.SetFanCurveAsync(curve, CancellationToken.None);
            ShowResult(result.Accepted ? result.Message : $"Curve was not applied. {result.Message}", result.Accepted);
            if (result.Accepted)
                HighlightMode(ModeCustom);
        }
        catch (Exception ex) when (ex is ManagementException or COMException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or ArgumentException)
        {
            ShowResult($"The firmware did not apply the safe curve: {ex.Message}", accepted: false);
        }
        finally
        {
            minimum.IsEnabled = true;
        }
    }

    private async void ModeSilentClick(object sender, RoutedEventArgs e) =>
        await ApplyModeAsync(ModeSilent, "Quiet", FanMode.Quiet);

    private async void ModeAutoClick(object sender, RoutedEventArgs e) =>
        await ApplyModeAsync(ModeAuto, "Balance", FanMode.Auto);

    private async void ModePerformanceClick(object sender, RoutedEventArgs e) =>
        await ApplyModeAsync(ModePerformance, "Performance", FanMode.Performance);

    private async void ModeMaxCoolingClick(object sender, RoutedEventArgs e) =>
        await ApplyModeAsync(ModeMaxCooling, "Max Cooling", FanMode.MaxCooling);

    private void ModeCustomClick(object sender, RoutedEventArgs e)
    {
        if (!ModeCustom.IsEnabled) return;
        HighlightMode(ModeCustom);
        ShowResult("Custom selected. Edit the curve below, then Apply curve to send it to firmware.", accepted: null);
        CurvePanel.BringIntoView();
    }

    private async void ExtremeModeClick(object sender, RoutedEventArgs e)
    {
        if (!ExtremeModeToggle.IsEnabled)
            return;

        var enabled = ExtremeModeToggle.IsChecked == true;
        ExtremeModeToggle.IsEnabled = false;
        try
        {
            var mode = enabled ? FanMode.MaxCooling : FanMode.Custom;
            ShowResult(enabled ? "Enabling Extreme Mode…" : "Disabling Extreme Mode…", accepted: null);
            var result = await _hardware.FanController.SetFanModeAsync(mode, CancellationToken.None);
            ShowResult(result.Message, result.Accepted);
            if (result.Accepted)
                HighlightMode(enabled ? ModeMaxCooling : ModeCustom);
            else
                ExtremeModeToggle.IsChecked = !enabled;
        }
        catch (Exception ex) when (ex is ManagementException or COMException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or ArgumentException)
        {
            ExtremeModeToggle.IsChecked = !enabled;
            ShowResult($"The firmware did not change Extreme Mode: {ex.Message}", accepted: false);
        }
        finally
        {
            ExtremeModeToggle.IsEnabled = _hardware.FanController.IsSupported;
        }
    }

    private async void ApplyGpuOverclockClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(GpuCoreOffset.Text, out var core) ||
            !int.TryParse(GpuMemoryOffset.Text, out var memory) ||
            core is < 0 or > 150 || memory is < 0 or > 200)
        {
            GpuOverclockStatus.Text = "Use core 0–150 MHz and VRAM 0–200 MHz.";
            return;
        }

        var result = await _hardware.GpuOverclock.ApplyAsync(core, memory, CancellationToken.None);
        GpuOverclockStatus.Text = result.Message;
    }

    private async void ResetGpuOverclockClick(object sender, RoutedEventArgs e)
    {
        var result = await _hardware.GpuOverclock.ResetAsync(CancellationToken.None);
        GpuOverclockStatus.Text = result.Message;
    }

    private async Task ApplyModeAsync(Button button, string label, FanMode mode)
    {
        button.IsEnabled = false;
        try
        {
            ShowResult($"Applying {label}…", accepted: null);
            var result = await _hardware.FanController.SetFanModeAsync(mode, CancellationToken.None);
            ShowResult(result.Message, result.Accepted);
            if (result.Accepted)
                HighlightMode(button);
        }
        catch (Exception ex) when (ex is ManagementException or COMException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException
            or ArgumentException)
        {
            ShowResult($"The firmware did not apply {label}: {ex.Message}", accepted: false);
        }
        finally
        {
            button.IsEnabled = _hardware.FanController.IsSupported;
        }
    }

    private void HighlightMode(Button selected)
    {
        var mode = FanModeForButton(selected);
        foreach (var button in AllModeButtons())
            button.Style = (Style)FindResource("Fan.ModeButton");

        selected.Style = (Style)FindResource(FanModeUiAccent.SelectedButtonStyleKey(mode));
        _selectedModeButton = selected;

        ApplyTelemetryAccent(mode);
        AnimateSelectedModeButtonGlow(selected, mode);
    }

    private IEnumerable<Button> AllModeButtons() =>
        [ModeSilent, ModeAuto, ModePerformance, ModeMaxCooling, ModeCustom];

    private FanMode FanModeForButton(Button button)
    {
        if (ReferenceEquals(button, ModeSilent)) return FanMode.Quiet;
        if (ReferenceEquals(button, ModeAuto)) return FanMode.Auto;
        if (ReferenceEquals(button, ModePerformance)) return FanMode.Performance;
        if (ReferenceEquals(button, ModeMaxCooling)) return FanMode.MaxCooling;
        if (ReferenceEquals(button, ModeCustom)) return FanMode.Custom;
        return FanMode.Auto;
    }

    private Button ModeButtonFor(FanMode mode) => mode switch
    {
        FanMode.Quiet => ModeSilent,
        FanMode.Auto or FanMode.Balanced => ModeAuto,
        FanMode.Performance => ModePerformance,
        FanMode.MaxCooling => ModeMaxCooling,
        FanMode.Custom => ModeCustom,
        _ => ModeAuto
    };

    /// <summary>
    /// Syncs hex telemetry accents to the active fan mode palette. UI-only.
    /// </summary>
    private void ApplyTelemetryAccent(FanMode mode)
    {
        CpuModule.SetModeAccent(mode);
        RamModule.SetModeAccent(mode);
        GpuModule.SetModeAccent(mode);
    }

    private void ClearModeButtonGlowEffects()
    {
        foreach (var button in AllModeButtons())
        {
            if (ReferenceEquals(button.Effect, _modeButtonGlow))
                button.Effect = null;
        }
    }

    private void AnimateSelectedModeButtonGlow(Button selected, FanMode mode)
    {
        var palette = FanModeUiAccent.For(mode);
        var reduced = AppUiPreferences.ReducedMotion;
        var duration = TimeSpan.FromMilliseconds(reduced ? 40 : 420);

        foreach (var button in AllModeButtons())
        {
            if (!ReferenceEquals(button, selected) && ReferenceEquals(button.Effect, _modeButtonGlow))
                button.Effect = null;
        }

        if (!ReferenceEquals(selected.Effect, _modeButtonGlow))
            selected.Effect = _modeButtonGlow;

        var fromOpacity = _modeButtonGlow.Opacity;
        var fromColor = _modeButtonGlow.Color;
        _modeButtonGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _modeButtonGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        _modeButtonGlow.Opacity = fromOpacity;
        _modeButtonGlow.Color = fromColor;

        if (reduced)
        {
            _modeButtonGlow.Color = palette.Bright;
            _modeButtonGlow.Opacity = palette.GlowStable;
            return;
        }

        _modeButtonGlow.BeginAnimation(DropShadowEffect.ColorProperty,
            new ColorAnimation(fromColor, palette.Bright, duration) { EasingFunction = ModeAccentEase });

        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration + TimeSpan.FromMilliseconds(160),
            FillBehavior = FillBehavior.HoldEnd
        };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(fromOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(palette.GlowPeak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), ModeAccentEase));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(palette.GlowStable * 0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270)), ModeAccentEase));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(palette.GlowPeak * 0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(370)), ModeAccentEase));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(palette.GlowStable, KeyTime.FromTimeSpan(duration + TimeSpan.FromMilliseconds(160)), ModeAccentEase));
        _modeButtonGlow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
    }

    private void ShowResult(string message, bool? accepted)
    {
        StatusText.Text = message;
        UiAnimation.NotifyBanner(ResultBanner);
        if (accepted is true)
        {
            ResultBanner.Style = (Style)FindResource("Badge.Success");
            StatusText.Foreground = (Brush)FindResource("Brush.Success");
        }
        else if (accepted is false)
        {
            ResultBanner.Style = (Style)FindResource("Badge.Warning");
            StatusText.Foreground = (Brush)FindResource("Brush.Warning");
        }
        else
        {
            ResultBanner.Style = (Style)FindResource("Badge.Neutral");
            StatusText.Foreground = (Brush)FindResource("Brush.TextPrimary");
        }
    }

    private async Task RefreshLiveReadingsAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var reading = await _hardware.Monitor.ReadAsync(CancellationToken.None);
            LastUpdatedText.Text = $"Updated {reading.Timestamp.ToLocalTime():HH:mm:ss}";
            AnimateLastUpdated();
            ApplyTelemetry(reading);
            var mode = await _hardware.FanController.GetCurrentModeAsync(CancellationToken.None);
            if (mode is FanMode currentMode)
                HighlightMode(ModeButtonFor(currentMode));
            if (_curveGraph is not null)
                _curveGraph.CurrentTemperature = reading.CpuTemperature ?? reading.GpuTemperature;
        }
        catch (Exception ex) when (ex is ManagementException or TimeoutException or UnauthorizedAccessException)
        {
            LastUpdatedText.Text = "Telemetry unavailable";
            ApplyTelemetry(null);
            if (_curveGraph is not null)
                _curveGraph.CurrentTemperature = null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyTelemetry(SensorReading? reading)
    {
        var reduced = AppUiPreferences.ReducedMotion;
        CpuModule.PreferReducedMotion = reduced;
        RamModule.PreferReducedMotion = reduced;
        GpuModule.PreferReducedMotion = reduced;

        if (reading is null)
        {
            CpuModule.Progress = null;
            CpuModule.DetailText = "—";
            CpuModule.FanRpm = null;
            RamModule.Progress = null;
            RamModule.DetailText = "—";
            GpuModule.Progress = null;
            GpuModule.DetailText = "—";
            GpuModule.FanRpm = null;
            return;
        }

        // Apply labels/RPM/progress together so CPU/GPU don't race separate animation loops.
        CpuModule.ApplyLiveMetrics(
            reading.CpuUsage,
            FormatClockTemp(reading.CpuClock, reading.CpuTemperature),
            reading.CpuFanRpm);

        RamModule.ApplyLiveMetrics(
            reading.MemoryUsage,
            reading.MemoryUsage is double mem ? $"{mem:0}% in use" : "—");

        GpuModule.ApplyLiveMetrics(
            reading.GpuUsage,
            FormatClockTemp(reading.GpuClock, reading.GpuTemperature),
            reading.GpuFanRpm);
    }

    private void AnimateLastUpdated()
    {
        if (!UiAnimation.ShouldAnimate)
            return;

        LastUpdatedText.BeginAnimation(UIElement.OpacityProperty, null);
        var fade = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(320),
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), ModeAccentEase));
        LastUpdatedText.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static string FormatClockTemp(double? clockGhz, double? tempC)
    {
        var clock = clockGhz is double c ? $"{c:0.00} GHz" : "— GHz";
        var temp = tempC is double t ? $"{t:0}°C" : "—°C";
        return $"{clock}  |  {temp}";
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LenovoLoqControl.Core;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Legion-style hardware module: hexagonal utilization gauge + stacked labels (exact ui.png layout).
/// </summary>
public sealed class HexMetricModule : Grid
{
    private const double HexSize = 88;
    private const double ProgressEpsilon = 0.004; // ~0.4% — ignore jitter between polls
    private const double ProgressAnimMinMs = 640;
    private const double ProgressAnimMaxMs = 1500;
    private static readonly TimeSpan LabelFadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan AccentAnimDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan AccentAnimReduced = TimeSpan.FromMilliseconds(40);

    private static readonly Color DefaultAccent = Color.FromRgb(0x40, 0xA9, 0xFF);
    private static readonly Color Track = Color.FromRgb(0x2A, 0x33, 0x40);
    private static readonly Color TitleColor = Color.FromRgb(0xC8, 0xD0, 0xDC);
    private static readonly Color DetailColor = Color.FromRgb(0x7A, 0x88, 0x9A);

    private static readonly CubicEase AccentEase = new() { EasingMode = EasingMode.EaseInOut };
    // Quartic EaseInOut keeps large CPU/GPU jumps from rushing; EaseOut for mid-flight retargets.
    private static readonly QuarticEase ProgressEase = new() { EasingMode = EasingMode.EaseInOut };
    private static readonly CubicEase ProgressRetargetEase = new() { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(HexMetricModule),
            new PropertyMetadata("CPU", OnLabelsChanged));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double?), typeof(HexMetricModule),
            new PropertyMetadata(null, OnProgressChanged));

    public static readonly DependencyProperty DetailTextProperty =
        DependencyProperty.Register(nameof(DetailText), typeof(string), typeof(HexMetricModule),
            new PropertyMetadata(string.Empty, OnLabelsChanged));

    public static readonly DependencyProperty FanRpmProperty =
        DependencyProperty.Register(nameof(FanRpm), typeof(double?), typeof(HexMetricModule),
            new PropertyMetadata(null, OnLabelsChanged));

    public static readonly DependencyProperty ShowFanRowProperty =
        DependencyProperty.Register(nameof(ShowFanRow), typeof(bool), typeof(HexMetricModule),
            new PropertyMetadata(true, OnLabelsChanged));

    public static readonly DependencyProperty PreferReducedMotionProperty =
        DependencyProperty.Register(nameof(PreferReducedMotion), typeof(bool), typeof(HexMetricModule),
            new PropertyMetadata(false, OnMotionPreferenceChanged));

    private readonly Canvas _hexCanvas;
    private readonly Path _outerTrack;
    private readonly Path _progressPath;
    private readonly Path _innerHex;
    private readonly Path _coreHex;
    private readonly Ellipse _progressTip;
    private readonly DropShadowEffect _tipGlow;
    private readonly TextBlock _percentText;
    private readonly TextBlock _titleText;
    private readonly TextBlock _detailText;
    private readonly StackPanel _fanRow;
    private readonly TextBlock _fanText;

    // Cached mutable brushes — animated in place; never recreated on telemetry refresh.
    private readonly SolidColorBrush _progressBrush;
    private readonly SolidColorBrush _tipBrush;
    private readonly SolidColorBrush _innerStrokeBrush;
    private readonly SolidColorBrush _coreStrokeBrush;
    private readonly DropShadowEffect _glowEffect;
    private readonly ScaleTransform _tipScale;
    private FanMode _accentMode = FanMode.Quiet;
    private bool _accentAnimating;
    private double _ambientGlowBase = 0.28;

    private bool _constructed;
    private bool _hasDrawnProgress;
    private int _suspendDpCallbacks;
    private double _displayedProgress;
    private double _displayedPercent;
    private double _displayedRpm;
    private bool _hasDisplayedRpm;
    private double _animFrom;
    private double _animTo;
    private double _percentFrom;
    private double _percentTo;
    private double _rpmFrom;
    private double _rpmTo;
    private bool _animatingRpm;
    private DateTime _animStart;
    private double _animDurationMs = ProgressAnimMinMs;
    private IEasingFunction _activeProgressEase = ProgressEase;
    private EventHandler? _renderHandler;
    private string _detailShown = string.Empty;

    public HexMetricModule()
    {
        MinWidth = 220;
        MinHeight = 100;
        Background = Brushes.Transparent;
        Focusable = false;
        SnapsToDevicePixels = true;

        _progressBrush = new SolidColorBrush(DefaultAccent);
        _tipBrush = new SolidColorBrush(DefaultAccent);
        _innerStrokeBrush = new SolidColorBrush(Color.FromArgb(0x88, DefaultAccent.R, DefaultAccent.G, DefaultAccent.B));
        _coreStrokeBrush = new SolidColorBrush(Color.FromArgb(0x55, DefaultAccent.R, DefaultAccent.G, DefaultAccent.B));
        _glowEffect = new DropShadowEffect
        {
            Color = DefaultAccent,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.28,
            RenderingBias = RenderingBias.Performance
        };
        _tipGlow = new DropShadowEffect
        {
            Color = Color.FromRgb(0x62, 0xC4, 0xFF),
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.7,
            RenderingBias = RenderingBias.Performance
        };
        _tipScale = new ScaleTransform(1, 1);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HexSize + 8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _hexCanvas = new Canvas
        {
            Width = HexSize,
            Height = HexSize,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        SetColumn(_hexCanvas, 0);
        Children.Add(_hexCanvas);

        _outerTrack = new Path
        {
            Stroke = new SolidColorBrush(Track),
            StrokeThickness = 5,
            StrokeLineJoin = PenLineJoin.Miter,
            Fill = Brushes.Transparent
        };
        _hexCanvas.Children.Add(_outerTrack);

        _progressPath = new Path
        {
            Stroke = _progressBrush,
            StrokeThickness = 6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = Brushes.Transparent,
            Effect = _glowEffect
        };
        _hexCanvas.Children.Add(_progressPath);

        _progressTip = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = _tipBrush,
            Effect = _tipGlow,
            RenderTransform = _tipScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _hexCanvas.Children.Add(_progressTip);

        _innerHex = new Path
        {
            Stroke = _innerStrokeBrush,
            StrokeThickness = 1.35,
            Fill = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x16))
        };
        _hexCanvas.Children.Add(_innerHex);

        _coreHex = new Path
        {
            Stroke = _coreStrokeBrush,
            StrokeThickness = 1,
            Fill = Brushes.Transparent
        };
        _hexCanvas.Children.Add(_coreHex);

        _percentText = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Text = "—",
            TextAlignment = TextAlignment.Center
        };
        _hexCanvas.Children.Add(_percentText);

        var labels = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        SetColumn(labels, 1);
        Children.Add(labels);

        _titleText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TitleColor),
            Text = "CPU"
        };
        labels.Children.Add(_titleText);

        _detailText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(DetailColor),
            Margin = new Thickness(0, 6, 0, 0),
            Text = "—"
        };
        labels.Children.Add(_detailText);

        _fanRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        labels.Children.Add(_fanRow);

        _fanRow.Children.Add(CreateFanIcon());
        _fanText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(DetailColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Text = "— RPM"
        };
        _fanRow.Children.Add(_fanText);

        _constructed = true;
        AutomationProperties.SetName(this, "Hardware metric");

        Loaded += OnLoaded;
        SizeChanged += (_, _) => LayoutHex();
        Unloaded += (_, _) =>
        {
            Loaded -= OnLoaded;
            StopProgressAnimation();
            StopAmbientAnimation();
            StopAccentAnimation();
            ClearLabelClocks();
        };

        LayoutHex();
        UpdateLabels(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutHex();
        UpdateLabels(animate: false);

        // First paint: draw progress in from empty (Legion Space style).
        if (!_hasDrawnProgress && Progress is double)
            UpdateProgress(animate: true);
        else if (_hasDrawnProgress)
            UpdateProgress(animate: false);
        else
        {
            _percentText.Text = "—";
            LayoutHex();
        }

        StartAmbientAnimation();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double? Progress
    {
        get => (double?)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty);
        set => SetValue(DetailTextProperty, value);
    }

    public double? FanRpm
    {
        get => (double?)GetValue(FanRpmProperty);
        set => SetValue(FanRpmProperty, value);
    }

    public bool ShowFanRow
    {
        get => (bool)GetValue(ShowFanRowProperty);
        set => SetValue(ShowFanRowProperty, value);
    }

    public bool PreferReducedMotion
    {
        get => (bool)GetValue(PreferReducedMotionProperty);
        set => SetValue(PreferReducedMotionProperty, value);
    }

    /// <summary>
    /// Transitions telemetry accents to the palette for the active fan mode.
    /// Safe to call repeatedly; brushes and animations are reused.
    /// </summary>
    public void SetModeAccent(FanMode mode) => ApplyModeAccent(mode);

    /// <summary>
    /// Applies progress, detail, and fan RPM in one pass so CPU/GPU modules
    /// don't start competing animation loops from separate DP callbacks.
    /// </summary>
    public void ApplyLiveMetrics(double? progress, string detailText, double? fanRpm = null)
    {
        if (!_constructed) return;

        _suspendDpCallbacks++;
        try
        {
            DetailText = detailText ?? string.Empty;
            if (ShowFanRow)
                FanRpm = fanRpm;
            Progress = progress;
        }
        finally
        {
            _suspendDpCallbacks--;
        }

        UpdateLabels(animate: true, allowIndependentRpmLoop: false);
        UpdateProgress(animate: true);
    }

    public void ApplyModeAccent(FanMode mode)
    {
        if (!_constructed) return;
        if (_accentMode == mode && !_accentAnimating)
        {
            // Same mode: keep colors, but ensure ambient motion is alive after load/resume.
            StartAmbientAnimation();
            return;
        }

        _accentMode = mode;
        var palette = FanModeUiAccent.For(mode);
        var reduced = PreferReducedMotion || AppUiPreferences.ReducedMotion;
        var duration = reduced ? AccentAnimReduced : AccentAnimDuration;

        var progressTo = palette.Primary;
        var innerTo = palette.InnerStroke;
        var coreTo = palette.CoreStroke;
        var glowStable = reduced ? 0.0 : palette.GlowStable;
        var glowPeak = reduced ? 0.0 : palette.GlowPeak;
        _ambientGlowBase = glowStable > 0.01 ? glowStable : 0.22;

        // Snapshot live colors before clearing clocks so handoff stays continuous.
        var progressFrom = _progressBrush.Color;
        var innerFrom = _innerStrokeBrush.Color;
        var coreFrom = _coreStrokeBrush.Color;
        var glowFrom = _glowEffect.Opacity;
        var glowColorFrom = _glowEffect.Color;

        ClearAccentClocks();
        _progressBrush.Color = progressFrom;
        _innerStrokeBrush.Color = innerFrom;
        _coreStrokeBrush.Color = coreFrom;
        _glowEffect.Opacity = glowFrom;
        _glowEffect.Color = glowColorFrom;

        EnsureGlowAttached(glowStable > 0 || glowFrom > 0.01);

        if (reduced)
        {
            _progressBrush.Color = progressTo;
            _tipBrush.Color = palette.Bright;
            _innerStrokeBrush.Color = innerTo;
            _coreStrokeBrush.Color = coreTo;
            _glowEffect.Color = palette.Bright;
            _tipGlow.Color = palette.Bright;
            _glowEffect.Opacity = glowStable;
            if (glowStable < 0.01)
                _progressPath.Effect = null;
            _accentAnimating = false;
            return;
        }

        _accentAnimating = true;

        _progressBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(progressFrom, progressTo, duration) { EasingFunction = AccentEase });
        _innerStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(innerFrom, innerTo, duration) { EasingFunction = AccentEase });
        _coreStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(coreFrom, coreTo, duration) { EasingFunction = AccentEase });
        _glowEffect.BeginAnimation(DropShadowEffect.ColorProperty,
            new ColorAnimation(glowColorFrom, palette.Bright, duration) { EasingFunction = AccentEase });

        // Gentle one-to-two pulse during transition, then settle. No infinite loop.
        var glowOpacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration + TimeSpan.FromMilliseconds(180),
            FillBehavior = FillBehavior.HoldEnd
        };
        glowOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(glowFrom, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glowOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(glowPeak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), AccentEase));
        glowOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(glowStable * 0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), AccentEase));
        glowOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(glowPeak * 0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), AccentEase));
        glowOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(glowStable, KeyTime.FromTimeSpan(duration + TimeSpan.FromMilliseconds(180)), AccentEase));

        glowOpacity.Completed += OnGlowOpacityCompleted;
        _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, glowOpacity);

        _tipBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(_tipBrush.Color, palette.Bright, duration) { EasingFunction = AccentEase });
        _tipGlow.BeginAnimation(DropShadowEffect.ColorProperty,
            new ColorAnimation(_tipGlow.Color, palette.Bright, duration) { EasingFunction = AccentEase });
    }

    private void OnGlowOpacityCompleted(object? sender, EventArgs e)
    {
        _accentAnimating = false;
        StartAmbientAnimation();
    }

    private void EnsureGlowAttached(bool attach)
    {
        if (attach)
        {
            if (!ReferenceEquals(_progressPath.Effect, _glowEffect))
                _progressPath.Effect = _glowEffect;
        }
        else if (ReferenceEquals(_progressPath.Effect, _glowEffect))
        {
            _progressPath.Effect = null;
        }
    }

    private void ClearAccentClocks()
    {
        StopAmbientAnimation();
        _progressBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _tipBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _innerStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _coreStrokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _glowEffect.BeginAnimation(DropShadowEffect.ColorProperty, null);
        _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _tipGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);
        _accentAnimating = false;
    }

    private void StopAccentAnimation() => ClearAccentClocks();

    /// <summary>
    /// Legion-style continuous motion: breathing glow, tip pulse, and soft progress shimmer.
    /// Keeps LIVE TELEMETRY alive even when sensor values are steady (control.png / ui.png).
    /// </summary>
    private void StartAmbientAnimation()
    {
        StopAmbientAnimation();
        if (!IsLoaded || PreferReducedMotion || AppUiPreferences.ReducedMotion || !UiAnimation.ShouldAnimate)
            return;

        EnsureGlowAttached(true);
        var baseOpacity = Math.Clamp(_ambientGlowBase, 0.16, 0.42);
        var low = Math.Max(0.12, baseOpacity * 0.62);
        var high = Math.Min(0.58, baseOpacity * 1.45);

        _glowEffect.Opacity = baseOpacity;
        _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation
        {
            From = low,
            To = high,
            Duration = TimeSpan.FromMilliseconds(1700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AccentEase
        });

        _progressPath.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0.82,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(1400),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AccentEase
        });

        _tipGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation
        {
            From = 0.4,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(1100),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AccentEase
        });

        var tipPulse = new DoubleAnimation
        {
            From = 0.85,
            To = 1.2,
            Duration = TimeSpan.FromMilliseconds(1100),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AccentEase
        };
        _tipScale.BeginAnimation(ScaleTransform.ScaleXProperty, tipPulse);
        _tipScale.BeginAnimation(ScaleTransform.ScaleYProperty, tipPulse.Clone());

        _innerStrokeBrush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation
        {
            From = 0.55,
            To = 0.95,
            Duration = TimeSpan.FromMilliseconds(2000),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = AccentEase
        });
    }

    private void StopAmbientAnimation()
    {
        _glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _tipGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _progressPath.BeginAnimation(UIElement.OpacityProperty, null);
        _tipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _tipScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _innerStrokeBrush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
        _progressPath.Opacity = 1;
        _tipScale.ScaleX = 1;
        _tipScale.ScaleY = 1;
        _innerStrokeBrush.Opacity = 1;
    }

    private static void OnLabelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HexMetricModule { _constructed: true, _suspendDpCallbacks: 0 } m)
            m.UpdateLabels(animate: true, allowIndependentRpmLoop: true);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HexMetricModule { _constructed: true, _suspendDpCallbacks: 0 } m)
            m.UpdateProgress(animate: true);
    }

    private static void OnMotionPreferenceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HexMetricModule { _constructed: true } m)
            return;

        m.UpdateProgress(animate: true);
        if (m.PreferReducedMotion || AppUiPreferences.ReducedMotion || !UiAnimation.ShouldAnimate)
            m.StopAmbientAnimation();
        else
            m.StartAmbientAnimation();
    }

    private static UIElement CreateFanIcon()
    {
        // Compact 5-blade fan glyph (procedural, no assets).
        var canvas = new Canvas { Width = 12, Height = 12, Opacity = 0.85 };
        var cx = 6d;
        var cy = 6d;
        for (var i = 0; i < 5; i++)
        {
            var angle = i * (360d / 5);
            var blade = new Path
            {
                Data = Geometry.Parse("M6,6 C7,2.5 9.5,2.2 10.2,4.5 C10.6,5.8 8.2,6.6 6,6 Z"),
                Fill = new SolidColorBrush(DetailColor),
                RenderTransform = new RotateTransform(angle, cx, cy)
            };
            canvas.Children.Add(blade);
        }
        canvas.Children.Add(new Ellipse
        {
            Width = 3,
            Height = 3,
            Fill = new SolidColorBrush(DetailColor),
            Margin = new Thickness(0)
        });
        Canvas.SetLeft(canvas.Children[^1], 4.5);
        Canvas.SetTop(canvas.Children[^1], 4.5);
        return canvas;
    }

    private void LayoutHex()
    {
        var cx = HexSize / 2d;
        var cy = HexSize / 2d;
        const double outerR = 38;
        const double innerR = 28;
        const double coreR = 20;

        _outerTrack.Data = BuildHexGeometry(cx, cy, outerR);
        _innerHex.Data = BuildHexGeometry(cx, cy, innerR);
        _coreHex.Data = BuildHexGeometry(cx, cy, coreR);
        ApplyProgressGeometry(cx, cy, outerR, _displayedProgress);
        UpdateProgressTip(cx, cy, outerR, _displayedProgress);

        _percentText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_percentText, cx - _percentText.DesiredSize.Width / 2d);
        Canvas.SetTop(_percentText, cy - _percentText.DesiredSize.Height / 2d);
    }

    private void UpdateLabels(bool animate, bool allowIndependentRpmLoop = true)
    {
        if (!_constructed) return;

        _titleText.Text = Title;
        AutomationProperties.SetName(this, $"{Title} metric");

        var detail = string.IsNullOrWhiteSpace(DetailText) ? "—" : DetailText;
        var reduced = PreferReducedMotion || AppUiPreferences.ReducedMotion || !UiAnimation.ShouldAnimate;
        if (!animate || reduced || string.Equals(detail, _detailShown, StringComparison.Ordinal))
        {
            _detailShown = detail;
            _detailText.Text = detail;
            _detailText.Opacity = 1;
        }
        else
        {
            CrossfadeText(_detailText, detail, () => _detailShown = detail);
        }

        if (!ShowFanRow)
        {
            _fanRow.Visibility = Visibility.Collapsed;
            return;
        }

        _fanRow.Visibility = Visibility.Visible;
        if (FanRpm is double rpm)
        {
            if (!animate || reduced)
            {
                _displayedRpm = rpm;
                _hasDisplayedRpm = true;
                _animatingRpm = false;
                _fanText.Text = $"{rpm:0} RPM";
                _fanText.Opacity = 1;
                return;
            }

            if (!_hasDisplayedRpm)
            {
                _displayedRpm = rpm;
                _hasDisplayedRpm = true;
                _fanText.Text = $"{rpm:0} RPM";
                return;
            }

            if (Math.Abs(rpm - _rpmTo) < 8 && Math.Abs(rpm - _displayedRpm) < 8 && _renderHandler is null)
            {
                _displayedRpm = rpm;
                _fanText.Text = $"{rpm:0} RPM";
                return;
            }

            _rpmFrom = _displayedRpm;
            _rpmTo = rpm;
            _animatingRpm = true;

            // When progress is updating in the same pass, let AnimateProgress own the render loop.
            if (!allowIndependentRpmLoop)
                return;

            if (_renderHandler is null)
            {
                _animDurationMs = ProgressAnimMinMs;
                StartRenderLoop(includeProgress: false, includePercent: false);
            }
        }
        else
        {
            _hasDisplayedRpm = false;
            _animatingRpm = false;
            _fanText.Text = "— RPM";
        }
    }

    private void CrossfadeText(TextBlock target, string next, Action onApplied)
    {
        target.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation(target.Opacity, 0, LabelFadeDuration)
        {
            EasingFunction = ProgressEase
        };
        fadeOut.Completed += (_, _) =>
        {
            target.Text = next;
            onApplied();
            target.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, LabelFadeDuration) { EasingFunction = ProgressEase });
        };
        target.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void ClearLabelClocks()
    {
        _detailText.BeginAnimation(OpacityProperty, null);
        _fanText.BeginAnimation(OpacityProperty, null);
        _detailText.Opacity = 1;
        _fanText.Opacity = 1;
    }

    private void UpdateProgress(bool animate)
    {
        if (!_constructed) return;

        var reduced = PreferReducedMotion || AppUiPreferences.ReducedMotion;
        if (Progress is double value)
        {
            var clamped = Math.Clamp(value, 0, 100);
            var target = clamped / 100d;
            _percentText.Foreground = Brushes.White;

            // Wait for Loaded so the first draw-in can play.
            if (!IsLoaded)
                return;

            if (!animate || reduced)
            {
                StopProgressAnimation();
                _displayedProgress = target;
                _displayedPercent = clamped;
                _hasDrawnProgress = true;
                _percentText.Text = $"{clamped:0}%";
                LayoutHex();
                SyncRpmIntoProgressAnimation();
                return;
            }

            // First live value: always draw in from empty (Legion Space style).
            if (!_hasDrawnProgress)
            {
                _displayedProgress = 0;
                _displayedPercent = 0;
                _percentText.Text = "0%";
                _hasDrawnProgress = true;
                AnimateProgress(target, clamped);
                return;
            }

            // Ignore tiny poll jitter so the ring stays calm between real changes.
            if (Math.Abs(target - _animTo) < ProgressEpsilon &&
                Math.Abs(target - _displayedProgress) < ProgressEpsilon &&
                _renderHandler is null)
            {
                _displayedPercent = clamped;
                _percentText.Text = $"{clamped:0}%";
                // Progress stable — still ease RPM if the fan row moved.
                if (_animatingRpm || (ShowFanRow && FanRpm is double rpm && Math.Abs(rpm - _displayedRpm) >= 8))
                {
                    SyncRpmIntoProgressAnimation();
                    if (_animatingRpm && _renderHandler is null)
                    {
                        _animDurationMs = ProgressAnimMinMs;
                        StartRenderLoop(includeProgress: false, includePercent: false);
                    }
                }
                return;
            }

            AnimateProgress(target, clamped);
        }
        else
        {
            // Hold the last gauge on transient WMI misses (common for GPU) instead of snapping empty.
            if (_hasDrawnProgress)
                return;

            StopProgressAnimation();
            _percentText.Text = "—";
            _percentText.Foreground = new SolidColorBrush(DetailColor);
            _displayedProgress = 0;
            _displayedPercent = 0;
            LayoutHex();
        }
    }

    private void SyncRpmIntoProgressAnimation()
    {
        if (!ShowFanRow || FanRpm is not double rpm)
            return;

        if (!_hasDisplayedRpm)
        {
            _displayedRpm = rpm;
            _hasDisplayedRpm = true;
            _fanText.Text = $"{rpm:0} RPM";
            return;
        }

        if (Math.Abs(rpm - _displayedRpm) < 8 && Math.Abs(rpm - _rpmTo) < 8)
            return;

        _rpmFrom = _displayedRpm;
        _rpmTo = rpm;
        _animatingRpm = true;
    }

    private void AnimateProgress(double targetFraction, double targetPercent)
    {
        // Mid-flight retargets use EaseOut so motion doesn't hesitate at each poll.
        _activeProgressEase = _renderHandler is null ? ProgressEase : ProgressRetargetEase;
        _animFrom = _displayedProgress;
        _animTo = targetFraction;
        _percentFrom = _displayedPercent;
        _percentTo = targetPercent;
        _animDurationMs = ResolveProgressDurationMs(_animFrom, _animTo);
        _animStart = DateTime.UtcNow;

        SyncRpmIntoProgressAnimation();
        StartRenderLoop(includeProgress: true, includePercent: true);
    }

    /// <summary>
    /// Larger utilization jumps (especially sudden drops) get a longer tween so the
    /// hex ring and % counter ease instead of snapping. ~14ms per percentage point.
    /// </summary>
    private static double ResolveProgressDurationMs(double fromFraction, double toFraction)
    {
        var delta = Math.Abs(toFraction - fromFraction); // 0..1
        var ms = ProgressAnimMinMs + delta * (ProgressAnimMaxMs - ProgressAnimMinMs);
        // Drops read harsher than rises — give them a little extra glide.
        if (toFraction < fromFraction)
            ms *= 1.18;
        return Math.Clamp(ms, ProgressAnimMinMs, ProgressAnimMaxMs);
    }

    private void StartRenderLoop(bool includeProgress, bool includePercent)
    {
        if (_renderHandler is not null)
            CompositionTarget.Rendering -= _renderHandler;

        // Capture motion endpoints at start; RPM can retarget via fields mid-flight.
        var fromProgress = _animFrom;
        var toProgress = _animTo;
        var fromPercent = _percentFrom;
        var toPercent = _percentTo;
        var fromRpm = _rpmFrom;
        var durationMs = Math.Max(1, _animDurationMs);
        var ease = _activeProgressEase;
        var animateProgress = includeProgress;
        var animatePercent = includePercent;
        _animStart = DateTime.UtcNow;

        _renderHandler = (_, _) =>
        {
            var t = Math.Clamp(
                (DateTime.UtcNow - _animStart).TotalMilliseconds / durationMs,
                0, 1);
            var eased = ease.Ease(t);

            if (animateProgress)
            {
                _displayedProgress = fromProgress + (toProgress - fromProgress) * eased;
                LayoutHex();
            }

            if (animatePercent)
            {
                _displayedPercent = fromPercent + (toPercent - fromPercent) * eased;
                _percentText.Text = $"{_displayedPercent:0}%";
                var cx = HexSize / 2d;
                var cy = HexSize / 2d;
                _percentText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(_percentText, cx - _percentText.DesiredSize.Width / 2d);
                Canvas.SetTop(_percentText, cy - _percentText.DesiredSize.Height / 2d);
            }

            if (_animatingRpm)
            {
                // Allow FanRpm updates during the tween to change the endpoint smoothly.
                var rpmTarget = _rpmTo;
                _displayedRpm = fromRpm + (rpmTarget - fromRpm) * eased;
                _fanText.Text = $"{_displayedRpm:0} RPM";
            }

            if (t >= 1)
            {
                if (animateProgress) _displayedProgress = toProgress;
                if (animatePercent)
                {
                    _displayedPercent = toPercent;
                    _percentText.Text = $"{toPercent:0}%";
                }
                if (_animatingRpm)
                {
                    _displayedRpm = _rpmTo;
                    _fanText.Text = $"{_rpmTo:0} RPM";
                    _animatingRpm = false;
                }
                StopProgressAnimation();
                if (animateProgress) LayoutHex();
            }
        };
        CompositionTarget.Rendering += _renderHandler;
    }

    private void StopProgressAnimation()
    {
        if (_renderHandler is null) return;
        CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
    }

    private void UpdateProgressTip(double cx, double cy, double radius, double progress)
    {
        if (progress <= 0.008)
        {
            _progressTip.Visibility = Visibility.Collapsed;
            return;
        }

        var point = PointOnHexPerimeter(cx, cy, radius, Math.Clamp(progress, 0, 1));
        _progressTip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_progressTip, point.X - _progressTip.Width / 2d);
        Canvas.SetTop(_progressTip, point.Y - _progressTip.Height / 2d);
    }

    private static Point PointOnHexPerimeter(double cx, double cy, double radius, double progress)
    {
        var points = HexPoints(cx, cy, radius);
        var perimeter = 0d;
        for (var i = 0; i < 6; i++)
            perimeter += Distance(points[i], points[(i + 1) % 6]);

        var targetLength = perimeter * Math.Clamp(progress, 0, 1);
        var remaining = targetLength;
        for (var i = 0; i < 6; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 6];
            var edge = Distance(a, b);
            if (remaining <= edge)
                return Lerp(a, b, edge <= 0 ? 0 : remaining / edge);
            remaining -= edge;
        }

        return points[0];
    }

    private void ApplyProgressGeometry(double cx, double cy, double radius, double progress)
    {
        if (progress <= 0.001)
        {
            _progressPath.Data = Geometry.Empty;
            return;
        }

        // Trace the hex perimeter starting from the top vertex, clockwise.
        var points = HexPoints(cx, cy, radius);
        var perimeter = 0d;
        for (var i = 0; i < 6; i++)
            perimeter += Distance(points[i], points[(i + 1) % 6]);

        var targetLength = perimeter * Math.Clamp(progress, 0.001, 1);
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
        var remaining = targetLength;

        for (var i = 0; i < 6 && remaining > 0.01; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 6];
            var edge = Distance(a, b);
            if (remaining >= edge)
            {
                figure.Segments.Add(new LineSegment(b, true));
                remaining -= edge;
            }
            else
            {
                var t = remaining / edge;
                figure.Segments.Add(new LineSegment(Lerp(a, b, t), true));
                remaining = 0;
            }
        }

        _progressPath.Data = new PathGeometry([figure]);
    }

    private static PathGeometry BuildHexGeometry(double cx, double cy, double radius)
    {
        var points = HexPoints(cx, cy, radius);
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
        for (var i = 1; i < 6; i++)
            figure.Segments.Add(new LineSegment(points[i], true));
        return new PathGeometry([figure]);
    }

    /// <summary>Pointy-top hexagon, vertex 0 at top.</summary>
    private static Point[] HexPoints(double cx, double cy, double radius)
    {
        var pts = new Point[6];
        for (var i = 0; i < 6; i++)
        {
            // Start at -90° (top), step 60° clockwise in screen space (Y down → use +angle).
            var deg = -90 + (i * 60);
            var rad = deg * Math.PI / 180d;
            pts[i] = new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
        }
        return pts;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Point Lerp(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

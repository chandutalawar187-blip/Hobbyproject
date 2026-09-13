using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Decorative LOQ 15APH8-style JIS keyboard silhouette (visual only).
/// White backlight mirrors Off/Low/High; RGB modes animate a live 4-zone preview.
/// </summary>
public sealed class KeyboardLayoutVisual : Grid
{
    private const double U = 20;
    private const double H = 18;
    private const double G = 2.8;
    private const double NumGap = 9;

    private static readonly Color KeyFace = Color.FromRgb(0x12, 0x16, 0x1C);
    private static readonly Color KeyFaceLit = Color.FromRgb(0x22, 0x2A, 0x36);
    private static readonly Color KeyEdge = Color.FromRgb(0x2A, 0x33, 0x40);
    private static readonly Color LegendDim = Color.FromRgb(0x55, 0x63, 0x74);
    private static readonly Color LegendLit = Color.FromRgb(0xEC, 0xF1, 0xF8);
    private static readonly Color GlowWhite = Color.FromRgb(0xD8, 0xE6, 0xFF);
    private static readonly Color Deck = Color.FromRgb(0x07, 0x09, 0x0E);
    private static readonly Color HudLine = Color.FromRgb(0x3A, 0xD0, 0xE8);
    private static readonly Color HudDim = Color.FromArgb(0x55, 0x3A, 0xD0, 0xE8);

    public static readonly DependencyProperty LightLevelProperty =
        DependencyProperty.Register(nameof(LightLevel), typeof(KeyboardLightLevel?), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty PreferReducedMotionProperty =
        DependencyProperty.Register(nameof(PreferReducedMotion), typeof(bool), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty ShowZoneHintsProperty =
        DependencyProperty.Register(nameof(ShowZoneHints), typeof(bool), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty RgbPreviewColorProperty =
        DependencyProperty.Register(nameof(RgbPreviewColor), typeof(Color?), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty RgbEffectProperty =
        DependencyProperty.Register(nameof(RgbEffect), typeof(KeyboardRgbEffect?), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty RgbSpeedProperty =
        DependencyProperty.Register(nameof(RgbSpeed), typeof(double), typeof(KeyboardLayoutVisual),
            new PropertyMetadata(3.0, OnVisualChanged));

    private readonly Border _frame;
    private readonly Canvas _hudOverlay = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly Canvas _canvas = new() { ClipToBounds = false };
    private readonly List<KeyVisual> _keys = new();
    private readonly List<Border> _zoneHints = new();
    private readonly List<SolidColorBrush> _zoneHintFills = new();
    private readonly List<SolidColorBrush> _zoneHintBorders = new();
    private bool _built;
    private bool _animating;
    private EventHandler? _renderHandler;
    private DateTime _animStartUtc = DateTime.UtcNow;

    public KeyboardLayoutVisual()
    {
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MinHeight = 168;

        _frame = new Border
        {
            Background = new SolidColorBrush(Deck),
            BorderBrush = new SolidColorBrush(KeyEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 14, 14, 16),
            ClipToBounds = false
        };

        var stage = new Grid();
        stage.Children.Add(_canvas);
        stage.Children.Add(_hudOverlay);
        _frame.Child = stage;
        Children.Add(_frame);

        Loaded += (_, _) =>
        {
            EnsureBuilt();
            ApplyLighting();
            FitWidth();
            SyncAnimationLoop();
        };
        Unloaded += (_, _) => StopAnimationLoop();
        SizeChanged += (_, _) => FitWidth();
        IsVisibleChanged += (_, _) => SyncAnimationLoop();
    }

    public KeyboardLightLevel? LightLevel
    {
        get => (KeyboardLightLevel?)GetValue(LightLevelProperty);
        set => SetValue(LightLevelProperty, value);
    }

    public bool PreferReducedMotion
    {
        get => (bool)GetValue(PreferReducedMotionProperty);
        set => SetValue(PreferReducedMotionProperty, value);
    }

    public bool ShowZoneHints
    {
        get => (bool)GetValue(ShowZoneHintsProperty);
        set => SetValue(ShowZoneHintsProperty, value);
    }

    public Color? RgbPreviewColor
    {
        get => (Color?)GetValue(RgbPreviewColorProperty);
        set => SetValue(RgbPreviewColorProperty, value);
    }

    public KeyboardRgbEffect? RgbEffect
    {
        get => (KeyboardRgbEffect?)GetValue(RgbEffectProperty);
        set => SetValue(RgbEffectProperty, value);
    }

    public double RgbSpeed
    {
        get => (double)GetValue(RgbSpeedProperty);
        set => SetValue(RgbSpeedProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not KeyboardLayoutVisual v) return;
        v.EnsureBuilt();
        v.UpdateHudChrome();
        v.ApplyLighting();
        v.UpdateZoneHints();
        v.SyncAnimationLoop();
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        Build();
        UpdateHudChrome();
    }

    private void FitWidth()
    {
        if (!_built || ActualWidth <= 1) return;
        var natural = _canvas.Width;
        if (natural <= 1) return;
        var scale = Math.Clamp((ActualWidth - 28) / natural, 0.68, 1.2);
        var transform = new ScaleTransform(scale, scale);
        _canvas.LayoutTransform = transform;
        _hudOverlay.LayoutTransform = transform;
        _hudOverlay.Width = _canvas.Width;
        _hudOverlay.Height = _canvas.Height;
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _keys.Clear();
        _zoneHints.Clear();
        _zoneHintFills.Clear();
        _zoneHintBorders.Clear();

        double X(double col) => col * (U + G);
        double Y(int row) => row * (H + G);
        double W(double units) => units * U + Math.Max(0, units - 1) * G;

        var num0 = X(15.2) + NumGap;
        double NX(int col) => num0 + col * (U + G);

        Add("Esc", X(0), Y(0), W(1.25), 0);
        Add("F1", X(1.6), Y(0), U, 0);
        Add("F2", X(2.6), Y(0), U, 0);
        Add("F3", X(3.6), Y(0), U, 0);
        Add("F4", X(4.6), Y(0), U, 0);
        Add("F5", X(6.0), Y(0), U, 1);
        Add("F6", X(7.0), Y(0), U, 1);
        Add("F7", X(8.0), Y(0), U, 1);
        Add("F8", X(9.0), Y(0), U, 1);
        Add("F9", X(10.4), Y(0), U, 2);
        Add("F10", X(11.4), Y(0), U, 2);
        Add("F11", X(12.4), Y(0), U, 2);
        Add("F12", X(13.4), Y(0), U, 2);
        Add("Ins", X(14.7), Y(0), U, 2);
        Add("Hom", NX(0), Y(0), U, 3);
        Add("End", NX(1), Y(0), U, 3);
        Add("P↑", NX(2), Y(0), U, 3);
        Add("P↓", NX(3), Y(0), U, 3);

        Add("半/全", X(0), Y(1), U, 0, 7);
        Add("1", X(1), Y(1), U, 0);
        Add("2", X(2), Y(1), U, 0);
        Add("3", X(3), Y(1), U, 0);
        Add("4", X(4), Y(1), U, 0);
        Add("5", X(5), Y(1), U, 1);
        Add("6", X(6), Y(1), U, 1);
        Add("7", X(7), Y(1), U, 1);
        Add("8", X(8), Y(1), U, 2);
        Add("9", X(9), Y(1), U, 2);
        Add("0", X(10), Y(1), U, 2);
        Add("-", X(11), Y(1), U, 2);
        Add("^", X(12), Y(1), U, 2);
        Add("¥", X(13), Y(1), U, 2);
        Add("Bksp", X(14), Y(1), W(1.55), 2, 8);
        Add("Num", NX(0), Y(1), U, 3, 8);
        Add("/", NX(1), Y(1), U, 3);
        Add("*", NX(2), Y(1), U, 3);
        Add("−", NX(3), Y(1), U, 3);

        Add("Tab", X(0), Y(2), W(1.45), 0, 8);
        Add("Q", X(1.45), Y(2), U, 0);
        Add("W", X(2.45), Y(2), U, 0);
        Add("E", X(3.45), Y(2), U, 0);
        Add("R", X(4.45), Y(2), U, 0);
        Add("T", X(5.45), Y(2), U, 1);
        Add("Y", X(6.45), Y(2), U, 1);
        Add("U", X(7.45), Y(2), U, 1);
        Add("I", X(8.45), Y(2), U, 2);
        Add("O", X(9.45), Y(2), U, 2);
        Add("P", X(10.45), Y(2), U, 2);
        Add("@", X(11.45), Y(2), U, 2);
        Add("[", X(12.45), Y(2), U, 2);
        Add("Enter", X(13.45), Y(2), W(1.55), 2, 8, H * 2 + G);
        Add("7", NX(0), Y(2), U, 3);
        Add("8", NX(1), Y(2), U, 3);
        Add("9", NX(2), Y(2), U, 3);
        Add("+", NX(3), Y(2), U, 3, 9, H * 2 + G);

        Add("Caps", X(0), Y(3), W(1.7), 0, 8);
        Add("A", X(1.7), Y(3), U, 0);
        Add("S", X(2.7), Y(3), U, 0);
        Add("D", X(3.7), Y(3), U, 0);
        Add("F", X(4.7), Y(3), U, 0);
        Add("G", X(5.7), Y(3), U, 1);
        Add("H", X(6.7), Y(3), U, 1);
        Add("J", X(7.7), Y(3), U, 1);
        Add("K", X(8.7), Y(3), U, 2);
        Add("L", X(9.7), Y(3), U, 2);
        Add(";", X(10.7), Y(3), U, 2);
        Add(":", X(11.7), Y(3), U, 2);
        Add("]", X(12.7), Y(3), U, 2);
        Add("4", NX(0), Y(3), U, 3);
        Add("5", NX(1), Y(3), U, 3);
        Add("6", NX(2), Y(3), U, 3);

        Add("Shift", X(0), Y(4), W(2.15), 0, 8);
        Add("Z", X(2.15), Y(4), U, 0);
        Add("X", X(3.15), Y(4), U, 0);
        Add("C", X(4.15), Y(4), U, 0);
        Add("V", X(5.15), Y(4), U, 0);
        Add("B", X(6.15), Y(4), U, 1);
        Add("N", X(7.15), Y(4), U, 1);
        Add("M", X(8.15), Y(4), U, 1);
        Add(",", X(9.15), Y(4), U, 2);
        Add(".", X(10.15), Y(4), U, 2);
        Add("/", X(11.15), Y(4), U, 2);
        Add("\\_", X(12.15), Y(4), U, 2, 8);
        Add("Shift", X(13.15), Y(4), W(1.85), 2, 8);
        Add("1", NX(0), Y(4), U, 3);
        Add("2", NX(1), Y(4), U, 3);
        Add("3", NX(2), Y(4), U, 3);
        Add("Ent", NX(3), Y(4), U, 3, 8, H * 2 + G);

        Add("Ctrl", X(0), Y(5), W(1.15), 0, 8);
        Add("Fn", X(1.15), Y(5), U, 0, 8);
        Add("Win", X(2.15), Y(5), U, 0, 8);
        Add("Alt", X(3.15), Y(5), U, 0, 8);
        Add("無変換", X(4.15), Y(5), W(1.25), 0, 7);
        Add("Space", X(5.4), Y(5), W(3.55), 1, 8);
        Add("変換", X(8.95), Y(5), W(1.2), 2, 7);
        Add("かな", X(10.15), Y(5), W(1.1), 2, 7);
        Add("Ctrl", X(11.25), Y(5), W(1.15), 2, 8);
        Add("↑", X(13.55), Y(5), U, 2);
        Add("0", NX(0), Y(5), W(2), 3);
        Add(".", NX(2), Y(5), U, 3);

        Add("←", X(12.55), Y(6), U, 2);
        Add("↓", X(13.55), Y(6), U, 2);
        Add("→", X(14.55), Y(6), U, 2);

        BuildZoneHints(num0);
        UpdateZoneHints();

        _canvas.Width = NX(4);
        _canvas.Height = Y(6) + H + 2;
        _hudOverlay.Width = _canvas.Width;
        _hudOverlay.Height = _canvas.Height;
        BuildHudOverlay();
    }

    private void BuildHudOverlay()
    {
        _hudOverlay.Children.Clear();
        var w = _canvas.Width;
        var h = _canvas.Height;
        const double arm = 14;

        void Corner(double x, double y, double dx, double dy)
        {
            var hLine = new Line
            {
                X1 = x, Y1 = y, X2 = x + dx * arm, Y2 = y,
                Stroke = new SolidColorBrush(HudLine),
                StrokeThickness = 1.4,
                Opacity = 0.85
            };
            var vLine = new Line
            {
                X1 = x, Y1 = y, X2 = x, Y2 = y + dy * arm,
                Stroke = new SolidColorBrush(HudLine),
                StrokeThickness = 1.4,
                Opacity = 0.85
            };
            _hudOverlay.Children.Add(hLine);
            _hudOverlay.Children.Add(vLine);
        }

        Corner(-4, -4, 1, 1);
        Corner(w + 4, -4, -1, 1);
        Corner(-4, h + 4, 1, -1);
        Corner(w + 4, h + 4, -1, -1);

        var tag = new TextBlock
        {
            Text = "LIVE PREVIEW · 4-ZONE",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(HudLine),
            Opacity = 0.75
        };
        Canvas.SetLeft(tag, 0);
        Canvas.SetTop(tag, -16);
        _hudOverlay.Children.Add(tag);
    }

    private void UpdateHudChrome()
    {
        var rgbMode = RgbEffect is not null || ShowZoneHints;
        _frame.BorderBrush = new SolidColorBrush(rgbMode ? Color.FromArgb(0x88, 0x3A, 0xD0, 0xE8) : KeyEdge);
        _frame.Effect = rgbMode && !(PreferReducedMotion || AppUiPreferences.ReducedMotion)
            ? new DropShadowEffect
            {
                Color = HudLine,
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.22,
                RenderingBias = RenderingBias.Performance
            }
            : null;
        _hudOverlay.Visibility = rgbMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add(
        string label,
        double left,
        double top,
        double width,
        int zone,
        double fontSize = 9.5,
        double? height = null)
    {
        var h = height ?? H;
        var face = new SolidColorBrush(KeyFace);
        var legend = new SolidColorBrush(LegendDim);
        var glow = new DropShadowEffect
        {
            Color = GlowWhite,
            BlurRadius = 0,
            ShadowDepth = 0,
            Opacity = 0,
            RenderingBias = RenderingBias.Performance
        };
        var border = new Border
        {
            Width = width,
            Height = h,
            Background = face,
            BorderBrush = new SolidColorBrush(KeyEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Effect = glow,
            Child = new TextBlock
            {
                Text = label,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = legend,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        _canvas.Children.Add(border);
        _keys.Add(new KeyVisual(border, face, legend, glow, zone));
    }

    private void BuildZoneHints(double numpadLeft)
    {
        var height = Y(6) + H;
        var edges = new[] { 0d, X(5.2), X(9.2), numpadLeft - 4, numpadLeft + 4 * (U + G) };

        for (var i = 0; i < 4; i++)
        {
            var fill = new SolidColorBrush(Color.FromArgb(0x18, 0x3A, 0xD0, 0xE8));
            var stroke = new SolidColorBrush(HudDim);
            var hint = new Border
            {
                Width = Math.Max(10, edges[i + 1] - edges[i] - 3),
                Height = height,
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Opacity = 0,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"Z{i + 1}",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(HudLine),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 0, 0),
                    Opacity = 0.7
                }
            };
            Canvas.SetLeft(hint, edges[i] + 1);
            Canvas.SetTop(hint, 0);
            Panel.SetZIndex(hint, -1);
            _canvas.Children.Insert(0, hint);
            _zoneHints.Add(hint);
            _zoneHintFills.Add(fill);
            _zoneHintBorders.Add(stroke);
        }
    }

    private static double X(double col) => col * (U + G);
    private static double Y(int row) => row * (H + G);

    private void UpdateZoneHints()
    {
        var show = ShowZoneHints || RgbEffect is not null;
        foreach (var hint in _zoneHints)
            hint.Opacity = show ? 1 : 0;
    }

    private bool NeedsLiveAnimation()
    {
        if (!IsLoaded || !IsVisible || PreferReducedMotion || AppUiPreferences.ReducedMotion)
            return false;
        return RgbEffect is KeyboardRgbEffect.Breathing
            or KeyboardRgbEffect.ColorCycle
            or KeyboardRgbEffect.Wave;
    }

    private void SyncAnimationLoop()
    {
        if (NeedsLiveAnimation())
            StartAnimationLoop();
        else
            StopAnimationLoop();
    }

    private void StartAnimationLoop()
    {
        if (_animating) return;
        _animating = true;
        _animStartUtc = DateTime.UtcNow;
        _renderHandler = (_, _) => ApplyLighting();
        CompositionTarget.Rendering += _renderHandler;
    }

    private void StopAnimationLoop()
    {
        if (!_animating) return;
        _animating = false;
        if (_renderHandler is not null)
            CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
    }

    private void ApplyLighting()
    {
        var reduced = PreferReducedMotion || AppUiPreferences.ReducedMotion;

        if (RgbEffect is KeyboardRgbEffect effect)
        {
            ApplyRgbEffect(effect, reduced);
            return;
        }

        var rgb = RgbPreviewColor;
        if (ShowZoneHints && rgb is Color solid)
        {
            ApplyZoneColors(_ => solid, intensity: 0.85, reduced);
            return;
        }

        ApplyWhiteBacklight(reduced);
    }

    private void ApplyWhiteBacklight(bool reduced)
    {
        double faceMix;
        double legendMix;
        double glowOpacity;
        double glowBlur;

        switch (LightLevel)
        {
            case KeyboardLightLevel.High:
                faceMix = 1;
                legendMix = 1;
                glowOpacity = reduced ? 0 : 0.5;
                glowBlur = 9;
                break;
            case KeyboardLightLevel.Low:
                faceMix = 0.5;
                legendMix = 0.65;
                glowOpacity = reduced ? 0 : 0.26;
                glowBlur = 7;
                break;
            case KeyboardLightLevel.Off:
                faceMix = 0;
                legendMix = 0.12;
                glowOpacity = 0;
                glowBlur = 0;
                break;
            default:
                faceMix = ShowZoneHints ? 0.22 : 0.32;
                legendMix = ShowZoneHints ? 0.38 : 0.5;
                glowOpacity = reduced || ShowZoneHints ? 0 : 0.1;
                glowBlur = 5;
                break;
        }

        foreach (var key in _keys)
        {
            key.Face.Color = Lerp(KeyFace, KeyFaceLit, faceMix);
            key.Legend.Color = Lerp(LegendDim, LegendLit, legendMix);
            SetGlow(key, GlowWhite, glowOpacity, glowBlur);
        }

        TintZoneHints(HudLine, 0.35);
    }

    private void ApplyRgbEffect(KeyboardRgbEffect effect, bool reduced)
    {
        var baseColor = RgbPreviewColor ?? Colors.White;
        var speed = Math.Clamp(RgbSpeed, 0, 10);
        var elapsed = (DateTime.UtcNow - _animStartUtc).TotalSeconds;
        // Map firmware speed 0–10 → animation rate (cycles per second-ish).
        var rate = 0.18 + speed * 0.22;

        switch (effect)
        {
            case KeyboardRgbEffect.Off:
                ApplyZoneColors(_ => Colors.Transparent, intensity: 0, reduced);
                break;

            case KeyboardRgbEffect.Static:
                ApplyZoneColors(_ => baseColor, intensity: 0.92, reduced);
                break;

            case KeyboardRgbEffect.Breathing:
            {
                var wave = reduced ? 0.75 : 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(elapsed * rate * Math.PI * 2));
                ApplyZoneColors(_ => baseColor, intensity: wave, reduced);
                break;
            }

            case KeyboardRgbEffect.ColorCycle:
            {
                if (reduced)
                {
                    ApplyZoneColors(_ => baseColor, intensity: 0.85, reduced);
                    break;
                }

                ApplyZoneColors(zone =>
                {
                    // Color Cycle changes the whole keyboard together. A
                    // per-zone phase offset makes the preview look like Wave.
                    var hue = (elapsed * rate * 120) % 360;
                    return FromHsv(hue, 0.85, 1);
                }, intensity: 0.95, reduced);
                break;
            }

            case KeyboardRgbEffect.Wave:
            {
                if (reduced)
                {
                    ApplyZoneColors(zone => ScaleColor(baseColor, 0.55 + zone * 0.12), intensity: 0.85, reduced);
                    break;
                }

                ApplyZoneColors(zone =>
                {
                    var phase = elapsed * rate * Math.PI * 2 - zone * 0.9;
                    var crest = 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(phase));
                    // Sweep hue slightly across zones for a traveling wave feel.
                    var hueShift = (elapsed * rate * 90) % 360;
                    var traveling = FromHsv((HueOf(baseColor) + hueShift + zone * 28) % 360, 0.8, 1);
                    return Lerp(ScaleColor(baseColor, 0.35), traveling, crest);
                }, intensity: 0.95, reduced);
                break;
            }

            default:
                ApplyZoneColors(_ => baseColor, intensity: 0.8, reduced);
                break;
        }
    }

    private void ApplyZoneColors(Func<int, Color> colorForZone, double intensity, bool reduced)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        var glowOpacity = reduced ? 0 : 0.18 + 0.42 * intensity;
        var glowBlur = 7 + 4 * intensity;
        var faceMix = 0.25 + 0.55 * intensity;
        var legendMix = 0.45 + 0.5 * intensity;

        Color[] zoneColors =
        [
            colorForZone(0),
            colorForZone(1),
            colorForZone(2),
            colorForZone(3)
        ];

        foreach (var key in _keys)
        {
            var c = zoneColors[Math.Clamp(key.Zone, 0, 3)];
            if (c.A == 0 || intensity <= 0.02)
            {
                key.Face.Color = KeyFace;
                key.Legend.Color = Lerp(LegendDim, LegendLit, 0.12);
                SetGlow(key, GlowWhite, 0, 0);
                continue;
            }

            key.Face.Color = Color.FromRgb(
                (byte)Math.Clamp(KeyFace.R + (c.R - KeyFace.R) * faceMix * 0.55, 0, 255),
                (byte)Math.Clamp(KeyFace.G + (c.G - KeyFace.G) * faceMix * 0.55, 0, 255),
                (byte)Math.Clamp(KeyFace.B + (c.B - KeyFace.B) * faceMix * 0.55, 0, 255));
            key.Legend.Color = Lerp(LegendDim, LegendLit, legendMix);
            SetGlow(key, c, glowOpacity, glowBlur);
        }

        for (var i = 0; i < _zoneHints.Count; i++)
            TintZoneHint(i, zoneColors[i], intensity);
    }

    private void TintZoneHints(Color color, double intensity) =>
        TintZoneHint(-1, color, intensity);

    private void TintZoneHint(int index, Color color, double intensity)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        if (index < 0)
        {
            for (var i = 0; i < _zoneHintFills.Count; i++)
                TintZoneHint(i, color, intensity);
            return;
        }

        if (index >= _zoneHintFills.Count) return;
        var aFill = (byte)Math.Clamp(18 + 40 * intensity, 0, 80);
        var aStroke = (byte)Math.Clamp(50 + 90 * intensity, 0, 160);
        _zoneHintFills[index].Color = Color.FromArgb(aFill, color.R, color.G, color.B);
        _zoneHintBorders[index].Color = Color.FromArgb(aStroke, color.R, color.G, color.B);
    }

    private static void SetGlow(KeyVisual key, Color color, double opacity, double blur)
    {
        if (opacity <= 0.01 || blur <= 0.01)
        {
            key.Glow.Opacity = 0;
            key.Glow.BlurRadius = 0;
            return;
        }

        key.Glow.Color = color;
        key.Glow.Opacity = opacity;
        key.Glow.BlurRadius = blur;
    }

    private static Color ScaleColor(Color c, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(c.R * t),
            (byte)(c.G * t),
            (byte)(c.B * t));
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static double HueOf(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta < 0.0001) return 0;
        double hue;
        if (Math.Abs(max - r) < 0.0001)
            hue = ((g - b) / delta) % 6;
        else if (Math.Abs(max - g) < 0.0001)
            hue = (b - r) / delta + 2;
        else
            hue = (r - g) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return hue;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }

    private sealed class KeyVisual
    {
        public KeyVisual(Border root, SolidColorBrush face, SolidColorBrush legend, DropShadowEffect glow, int zone)
        {
            Root = root;
            Face = face;
            Legend = legend;
            Glow = glow;
            Zone = zone;
        }

        public Border Root { get; }
        public SolidColorBrush Face { get; }
        public SolidColorBrush Legend { get; }
        public DropShadowEffect Glow { get; }
        public int Zone { get; }
    }
}

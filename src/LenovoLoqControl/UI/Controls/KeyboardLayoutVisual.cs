using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Decorative LOQ 15APH8-style JIS keyboard silhouette (visual only).
/// Lighting intensity mirrors white-backlight Off/Low/High; RGB modes may show
/// zone guides without offering color controls.
/// </summary>
public sealed class KeyboardLayoutVisual : Grid
{
    private const double U = 20;      // key unit width
    private const double H = 18;      // key height
    private const double G = 2.8;     // gap
    private const double NumGap = 9;  // gap before numpad

    private static readonly Color KeyFace = Color.FromRgb(0x12, 0x16, 0x1C);
    private static readonly Color KeyFaceLit = Color.FromRgb(0x22, 0x2A, 0x36);
    private static readonly Color KeyEdge = Color.FromRgb(0x2A, 0x33, 0x40);
    private static readonly Color LegendDim = Color.FromRgb(0x55, 0x63, 0x74);
    private static readonly Color LegendLit = Color.FromRgb(0xEC, 0xF1, 0xF8);
    private static readonly Color GlowWhite = Color.FromRgb(0xD8, 0xE6, 0xFF);
    private static readonly Color Deck = Color.FromRgb(0x0A, 0x0C, 0x10);

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

    private readonly Canvas _canvas = new() { ClipToBounds = false };
    private readonly List<KeyVisual> _keys = new();
    private readonly List<Border> _zoneHints = new();
    private bool _built;

    public KeyboardLayoutVisual()
    {
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MinHeight = 156;

        var frame = new Border
        {
            Background = new SolidColorBrush(Deck),
            BorderBrush = new SolidColorBrush(KeyEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 12)
        };
        frame.Child = _canvas;
        Children.Add(frame);

        Loaded += (_, _) =>
        {
            EnsureBuilt();
            ApplyLighting();
            FitWidth();
        };
        SizeChanged += (_, _) => FitWidth();
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

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not KeyboardLayoutVisual v) return;
        v.EnsureBuilt();
        v.ApplyLighting();
        v.UpdateZoneHints();
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        Build();
    }

    private void FitWidth()
    {
        if (!_built || ActualWidth <= 1) return;
        var natural = _canvas.Width;
        if (natural <= 1) return;
        var scale = Math.Clamp((ActualWidth - 24) / natural, 0.68, 1.2);
        _canvas.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _keys.Clear();
        _zoneHints.Clear();

        // Column helper: x position of unit column c (0-based) in main block.
        double X(double col) => col * (U + G);
        double Y(int row) => row * (H + G);
        double W(double units) => units * U + Math.Max(0, units - 1) * G;

        var num0 = X(15.2) + NumGap; // numpad left
        double NX(int col) => num0 + col * (U + G);

        // Row 0 — function
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
        // Numpad media / nav strip
        Add("Hom", NX(0), Y(0), U, 3);
        Add("End", NX(1), Y(0), U, 3);
        Add("P↑", NX(2), Y(0), U, 3);
        Add("P↓", NX(3), Y(0), U, 3);

        // Row 1 — numbers (JIS)
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

        // Row 2 — QWERTY + Enter top + numpad
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
        // JIS Enter (tall)
        Add("Enter", X(13.45), Y(2), W(1.55), 2, 8, H * 2 + G);
        Add("7", NX(0), Y(2), U, 3);
        Add("8", NX(1), Y(2), U, 3);
        Add("9", NX(2), Y(2), U, 3);
        Add("+", NX(3), Y(2), U, 3, 9, H * 2 + G);

        // Row 3 — home
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

        // Row 4 — ZXCV
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

        // Row 5 — modifiers + short space (JIS) + up arrow
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

        // Row 6 — arrow cluster (offset down like the reference)
        Add("←", X(12.55), Y(6), U, 2);
        Add("↓", X(13.55), Y(6), U, 2);
        Add("→", X(14.55), Y(6), U, 2);

        BuildZoneHints(num0);
        UpdateZoneHints();

        _canvas.Width = NX(4);
        _canvas.Height = Y(6) + H + 2;
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
        var border = new Border
        {
            Width = width,
            Height = h,
            Background = face,
            BorderBrush = new SolidColorBrush(KeyEdge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
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
        _keys.Add(new KeyVisual(border, face, legend, zone));
    }

    private void BuildZoneHints(double numpadLeft)
    {
        var height = Y(6) + H;
        var edges = new[] { 0d, X(5.2), X(9.2), numpadLeft - 4, _canvas.Width > 0 ? _canvas.Width : numpadLeft + 4 * (U + G) };
        // Width not finalized yet — use computed numpad end.
        edges[4] = numpadLeft + 4 * (U + G);

        for (var i = 0; i < 4; i++)
        {
            var hint = new Border
            {
                Width = Math.Max(10, edges[i + 1] - edges[i] - 3),
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xE2, 0x23, 0x1A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xE2, 0x23, 0x1A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Opacity = 0,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"Zone {i + 1}",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x6B, 0x63)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 0, 0)
                }
            };
            Canvas.SetLeft(hint, edges[i] + 1);
            Canvas.SetTop(hint, 0);
            Panel.SetZIndex(hint, -1);
            _canvas.Children.Insert(0, hint);
            _zoneHints.Add(hint);
        }
    }

    private static double X(double col) => col * (U + G);
    private static double Y(int row) => row * (H + G);

    private void UpdateZoneHints()
    {
        foreach (var hint in _zoneHints)
            hint.Opacity = ShowZoneHints ? 1 : 0;
    }

    private void ApplyLighting()
    {
        var reduced = PreferReducedMotion || AppUiPreferences.ReducedMotion;
        var rgb = RgbPreviewColor;
        var useRgb = ShowZoneHints && rgb is Color;

        double faceMix;
        double legendMix;
        double glowOpacity;
        double glowBlur;
        Color glowColor = GlowWhite;

        if (useRgb)
        {
            var c = rgb!.Value;
            glowColor = c;
            var off = LightLevel == KeyboardLightLevel.Off; // unused for RGB usually
            _ = off;
            faceMix = 0.55;
            legendMix = 0.85;
            glowOpacity = reduced ? 0 : 0.48;
            glowBlur = 9;
        }
        else switch (LightLevel)
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
            if (useRgb)
            {
                var c = rgb!.Value;
                // Slight per-zone brightness variation so the 4-zone LOQ layout reads clearly.
                var zoneBoost = 0.85 + key.Zone * 0.05;
                key.Face.Color = Color.FromRgb(
                    (byte)Math.Clamp(KeyFace.R + (c.R - KeyFace.R) * faceMix * zoneBoost * 0.35, 0, 255),
                    (byte)Math.Clamp(KeyFace.G + (c.G - KeyFace.G) * faceMix * zoneBoost * 0.35, 0, 255),
                    (byte)Math.Clamp(KeyFace.B + (c.B - KeyFace.B) * faceMix * zoneBoost * 0.35, 0, 255));
                key.Legend.Color = Lerp(LegendDim, LegendLit, legendMix);
                glowColor = Color.FromRgb(
                    (byte)Math.Clamp(c.R * zoneBoost, 0, 255),
                    (byte)Math.Clamp(c.G * zoneBoost, 0, 255),
                    (byte)Math.Clamp(c.B * zoneBoost, 0, 255));
            }
            else
            {
                key.Face.Color = Lerp(KeyFace, KeyFaceLit, faceMix);
                key.Legend.Color = Lerp(LegendDim, LegendLit, legendMix);
                glowColor = GlowWhite;
            }

            if (glowOpacity <= 0.01)
            {
                key.Root.Effect = null;
                continue;
            }

            key.Root.Effect = new DropShadowEffect
            {
                Color = glowColor,
                BlurRadius = glowBlur,
                ShadowDepth = 0,
                Opacity = glowOpacity,
                RenderingBias = RenderingBias.Performance
            };
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private sealed class KeyVisual
    {
        public KeyVisual(Border root, SolidColorBrush face, SolidColorBrush legend, int zone)
        {
            Root = root;
            Face = face;
            Legend = legend;
            Zone = zone;
        }

        public Border Root { get; }
        public SolidColorBrush Face { get; }
        public SolidColorBrush Legend { get; }
        public int Zone { get; }
    }
}

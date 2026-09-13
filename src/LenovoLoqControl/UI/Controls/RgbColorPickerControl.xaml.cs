using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Dark sci-fi color picker with Swatches / Precise tabs, hex field, and eyedropper.
/// </summary>
public partial class RgbColorPickerControl : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor),
            typeof(Color),
            typeof(RgbColorPickerControl),
            new FrameworkPropertyMetadata(
                Color.FromRgb(0x6F, 0x19, 0xE6),
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedColorChanged));

    private bool _syncing;
    private bool _svDragging;
    private bool _hueDragging;
    private double _hue;
    private double _sat = 1;
    private double _val = 1;
    private Window? _eyedropperOverlay;

    public RgbColorPickerControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildSwatches();
            ApplySelectedToUi(SelectedColor, raiseEvent: false);
        };
        Unloaded += (_, _) => CloseEyedropper();
        SvHost.SizeChanged += (_, _) => UpdatePreciseVisuals();
        HueHost.SizeChanged += (_, _) => UpdatePreciseVisuals();
    }

    public event EventHandler<Color>? ColorChanged;

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public void SetColor(Color color, bool raiseEvent = true) =>
        ApplySelectedToUi(color, raiseEvent);

    public void SetColor(KeyboardRgbColor color, bool raiseEvent = true) =>
        SetColor(Color.FromRgb(color.Red, color.Green, color.Blue), raiseEvent);

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RgbColorPickerControl picker && e.NewValue is Color color)
            picker.ApplySelectedToUi(color, raiseEvent: false);
    }

    private void TabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var swatches = TabSwatches.IsChecked == true;
        SwatchGrid.Visibility = swatches ? Visibility.Visible : Visibility.Collapsed;
        PrecisePanel.Visibility = swatches ? Visibility.Collapsed : Visibility.Visible;
        if (!swatches)
            UpdatePreciseVisuals();
    }

    private void BuildSwatches()
    {
        SwatchGrid.Children.Clear();

        // Row 0 — grayscale white → black
        for (var i = 0; i < 8; i++)
        {
            var g = (byte)(255 - i * 36);
            if (i == 7) g = 0;
            AddSwatch(Color.FromRgb(g, g, g));
        }

        // Rows 1–6 — hue columns with descending brightness / rising saturation
        double[] hues = [0, 28, 52, 95, 175, 210, 265, 320];
        double[] values = [0.95, 0.82, 0.68, 0.54, 0.40, 0.26];
        double[] sats = [0.45, 0.62, 0.78, 0.90, 0.95, 1.0];

        for (var row = 0; row < values.Length; row++)
        {
            for (var col = 0; col < hues.Length; col++)
                AddSwatch(FromHsv(hues[col], sats[row], values[row]));
        }
    }

    private void AddSwatch(Color color)
    {
        var face = new SolidColorBrush(color);
        var border = new Border
        {
            Width = 28,
            Height = 22,
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(5),
            Background = face,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            Tag = color
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ApplySelectedToUi(color, raiseEvent: true);
        };
        SwatchGrid.Children.Add(border);
    }

    private void ApplySelectedToUi(Color color, bool raiseEvent)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            color.A = 255;
            if (SelectedColor != color)
                SelectedColor = color;

            SelectedPreview.Fill = new SolidColorBrush(color);
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (!string.Equals(HexText.Text, hex, StringComparison.OrdinalIgnoreCase))
                HexText.Text = hex;

            ToHsv(color, out _hue, out _sat, out _val);
            UpdatePreciseVisuals();

            SliderR.Value = color.R;
            SliderG.Value = color.G;
            SliderB.Value = color.B;

            HighlightActiveSwatch(color);

            if (raiseEvent)
                ColorChanged?.Invoke(this, color);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void HighlightActiveSwatch(Color color)
    {
        foreach (var child in SwatchGrid.Children)
        {
            if (child is not Border border || border.Tag is not Color swatch)
                continue;

            var match = swatch.R == color.R && swatch.G == color.G && swatch.B == color.B;
            border.BorderBrush = new SolidColorBrush(match
                ? Color.FromRgb(0x5E, 0xC8, 0xFF)
                : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            border.BorderThickness = new Thickness(match ? 2 : 1);
            border.Effect = match
                ? new DropShadowEffect
                {
                    Color = Color.FromRgb(0x5E, 0xC8, 0xFF),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    RenderingBias = RenderingBias.Performance
                }
                : null;
        }
    }

    private void UpdatePreciseVisuals()
    {
        SvHueBase.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

        var w = Math.Max(1, SvHost.ActualWidth);
        var h = Math.Max(1, SvHost.ActualHeight);
        var cursorMaxX = Math.Max(0, w - 14);
        var cursorMaxY = Math.Max(0, h - 14);
        SvCursor.Margin = new Thickness(
            Math.Clamp(_sat * w - 7, 0, cursorMaxX),
            Math.Clamp((1 - _val) * h - 7, 0, cursorMaxY),
            0, 0);

        var hueW = Math.Max(1, HueHost.ActualWidth);
        var hueCursorMaxX = Math.Max(0, hueW - 6);
        HueCursor.Margin = new Thickness(
            Math.Clamp(_hue / 360.0 * hueW - 3, 0, hueCursorMaxX), 0, 0, 0);
    }

    private void HexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseHex(HexText.Text, out var color))
            return;
        ApplySelectedToUi(color, raiseEvent: true);
    }

    private void ChannelSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsLoaded) return;
        var color = Color.FromRgb(
            (byte)Math.Clamp((int)SliderR.Value, 0, 255),
            (byte)Math.Clamp((int)SliderG.Value, 0, 255),
            (byte)Math.Clamp((int)SliderB.Value, 0, 255));
        ApplySelectedToUi(color, raiseEvent: true);
    }

    private void SvPointerDown(object sender, MouseButtonEventArgs e)
    {
        _svDragging = true;
        SvHost.CaptureMouse();
        SampleSv(e.GetPosition(SvHost));
    }

    private void SvPointerMove(object sender, MouseEventArgs e)
    {
        if (!_svDragging || e.LeftButton != MouseButtonState.Pressed) return;
        SampleSv(e.GetPosition(SvHost));
    }

    private void SvPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_svDragging) return;
        _svDragging = false;
        SvHost.ReleaseMouseCapture();
    }

    private void SampleSv(Point p)
    {
        var w = Math.Max(1, SvHost.ActualWidth);
        var h = Math.Max(1, SvHost.ActualHeight);
        _sat = Math.Clamp(p.X / w, 0, 1);
        _val = Math.Clamp(1 - p.Y / h, 0, 1);
        ApplySelectedToUi(FromHsv(_hue, _sat, _val), raiseEvent: true);
    }

    private void HuePointerDown(object sender, MouseButtonEventArgs e)
    {
        _hueDragging = true;
        HueHost.CaptureMouse();
        SampleHue(e.GetPosition(HueHost));
    }

    private void HuePointerMove(object sender, MouseEventArgs e)
    {
        if (!_hueDragging || e.LeftButton != MouseButtonState.Pressed) return;
        SampleHue(e.GetPosition(HueHost));
    }

    private void HuePointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hueDragging) return;
        _hueDragging = false;
        HueHost.ReleaseMouseCapture();
    }

    private void SampleHue(Point p)
    {
        var w = Math.Max(1, HueHost.ActualWidth);
        _hue = Math.Clamp(p.X / w, 0, 1) * 360.0;
        ApplySelectedToUi(FromHsv(_hue, _sat, _val), raiseEvent: true);
    }

    private void EyedropperClick(object sender, RoutedEventArgs e)
    {
        if (_eyedropperOverlay is not null)
            return;

        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Cursor = Cursors.Cross,
            Title = "Eyedropper"
        };

        overlay.Loaded += (_, _) =>
        {
            overlay.Left = SystemParameters.VirtualScreenLeft;
            overlay.Top = SystemParameters.VirtualScreenTop;
            overlay.Width = SystemParameters.VirtualScreenWidth;
            overlay.Height = SystemParameters.VirtualScreenHeight;
        };

        overlay.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            var screen = overlay.PointToScreen(args.GetPosition(overlay));
            var sampled = SampleScreenColor((int)screen.X, (int)screen.Y);
            CloseEyedropper();
            ApplySelectedToUi(sampled, raiseEvent: true);
        };

        overlay.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                CloseEyedropper();
            }
        };
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_eyedropperOverlay, overlay))
                _eyedropperOverlay = null;
        };

        _eyedropperOverlay = overlay;
        overlay.Show();
        overlay.Activate();
        overlay.Focus();
    }

    private void CloseEyedropper()
    {
        if (_eyedropperOverlay is null) return;
        _eyedropperOverlay.Close();
        _eyedropperOverlay = null;
    }

    private static Color SampleScreenColor(int x, int y)
    {
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var pixel = GetPixel(hdc, x, y);
            var r = (byte)(pixel & 0xFF);
            var g = (byte)((pixel >> 8) & 0xFF);
            var b = (byte)((pixel >> 16) & 0xFF);
            return Color.FromRgb(r, g, b);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var value = text?.Trim().TrimStart('#');
        if (value?.Length != 6
            || !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(value[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(value[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static void ToHsv(Color color, out double h, out double s, out double v)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        v = max;
        s = max <= 0.0001 ? 0 : delta / max;

        if (delta < 0.0001)
        {
            h = 0;
            return;
        }

        if (Math.Abs(max - r) < 0.0001)
            h = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 0.0001)
            h = 60 * ((b - r) / delta + 2);
        else
            h = 60 * ((r - g) / delta + 4);

        if (h < 0) h += 360;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hDc, int x, int y);
}

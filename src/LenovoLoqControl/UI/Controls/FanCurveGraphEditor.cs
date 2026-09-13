using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Interactive fan-curve graph. Firmware temperature points are fixed on X; fan percentage is editable on Y.
/// Safety shading mirrors FanCurveValidator thresholds; backend validation remains authoritative.
/// </summary>
public sealed class FanCurveGraphEditor : UserControl
{
    private const double PlotLeft = 52;
    private const double PlotTop = 18;
    private const double PlotRightPad = 18;
    private const double PlotBottomPad = 36;
    private const double PointRadius = 7;
    private const double TempMin = 40;
    private const double TempMax = 100;
    private const double SafetyFloor85 = 60;
    private const double SafetyFloor95 = 100;

    private readonly Canvas _root = new();
    private readonly Canvas _plot = new();
    private readonly Path _safetyRegion = new() { IsHitTestVisible = false };
    private readonly Path _safetyFloorLine = new() { IsHitTestVisible = false };
    private readonly Path _curvePath = new() { IsHitTestVisible = false };
    private readonly Line _guideV = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Line _guideH = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Line _currentTempLine = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _tooltip = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _tooltipText = new();
    private readonly List<CurveHandle> _handles = [];
    private readonly List<TextBlock> _xLabels = [];
    private readonly List<TextBlock> _yLabels = [];
    private readonly List<Line> _gridLines = [];

    private double[] _temperatures = [];
    private double[] _percents = [];
    private int[] _fanSpeedsRpm = [];
    private int _selectedIndex = -1;
    private int _hoverIndex = -1;
    private bool _suppressEvents;
    private double? _currentTemperature;

    public event EventHandler? CurveChanged;
    public event EventHandler? SelectionChanged;

    public FanCurveGraphEditor()
    {
        MinHeight = 280;
        MinWidth = 420;
        Focusable = true;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Custom fan curve graph");
        AutomationProperties.SetHelpText(this,
            "Firmware temperature points are fixed. Drag points vertically to change fan percentage.");

        _root.Background = Brushes.Transparent;
        Content = _root;

        _plot.ClipToBounds = true;
        _root.Children.Add(_plot);

        _safetyRegion.Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xF4, 0xB9, 0x42));
        _safetyFloorLine.Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xF4, 0xB9, 0x42));
        _safetyFloorLine.StrokeThickness = 1.5;
        _safetyFloorLine.StrokeDashArray = new DoubleCollection { 4, 3 };

        _curvePath.Stroke = new SolidColorBrush(Color.FromRgb(0x62, 0xA8, 0xFF));
        _curvePath.StrokeThickness = 2.5;
        _curvePath.StrokeLineJoin = PenLineJoin.Round;
        _curvePath.Fill = new LinearGradientBrush(
            Color.FromArgb(0x33, 0x62, 0xA8, 0xFF),
            Color.FromArgb(0x00, 0x62, 0xA8, 0xFF),
            90);

        _guideV.Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x9A, 0xA8, 0xBA));
        _guideV.StrokeThickness = 1;
        _guideV.StrokeDashArray = new DoubleCollection { 3, 3 };
        _guideH.Stroke = _guideV.Stroke;
        _guideH.StrokeThickness = 1;
        _guideH.StrokeDashArray = _guideV.StrokeDashArray;

        _currentTempLine.Stroke = new SolidColorBrush(Color.FromRgb(0xE2, 0x23, 0x1A));
        _currentTempLine.StrokeThickness = 1.5;
        _currentTempLine.StrokeDashArray = new DoubleCollection { 2, 3 };

        _tooltip.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x2D));
        _tooltip.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x40));
        _tooltip.BorderThickness = new Thickness(1);
        _tooltip.CornerRadius = new CornerRadius(6);
        _tooltip.Padding = new Thickness(8, 6, 8, 6);
        _tooltip.Child = _tooltipText;
        _tooltipText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
        _tooltipText.FontSize = 11;

        _plot.Children.Add(_safetyRegion);
        _plot.Children.Add(_safetyFloorLine);
        _plot.Children.Add(_curvePath);
        _plot.Children.Add(_currentTempLine);
        _plot.Children.Add(_guideV);
        _plot.Children.Add(_guideH);

        _root.Children.Add(_tooltip);

        SizeChanged += (_, _) => Relayout();
        Loaded += (_, _) => Relayout();
        KeyDown += OnKeyDown;
    }

    public int SelectedIndex => _selectedIndex;

    public double? CurrentTemperature
    {
        get => _currentTemperature;
        set
        {
            _currentTemperature = value;
            DrawCurrentTemperature();
        }
    }

    public bool HasPoints => _temperatures.Length > 0;

    public void LoadFirmwareTable(FirmwareFanTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _temperatures = table.TemperaturesCelsius.Select(t => (double)t).ToArray();
        _fanSpeedsRpm = table.FanSpeedsRpm.ToArray();
        _percents = new double[_temperatures.Length];
        for (var i = 0; i < _temperatures.Length; i++)
            _percents[i] = DefaultFanPercent(_temperatures[i]);

        RebuildHandles();
        _selectedIndex = _temperatures.Length > 0 ? 0 : -1;
        Relayout();
        RaiseSelectionChanged();
        RaiseCurveChanged();
    }

    public void Clear()
    {
        _temperatures = [];
        _percents = [];
        _fanSpeedsRpm = [];
        ClearHandles();
        _selectedIndex = -1;
        _hoverIndex = -1;
        HideGuides();
        _curvePath.Data = Geometry.Empty;
        _safetyRegion.Data = Geometry.Empty;
        _safetyFloorLine.Data = Geometry.Empty;
        _currentTempLine.Visibility = Visibility.Collapsed;
        RaiseSelectionChanged();
    }

    public FanCurve GetCurve()
    {
        var points = _temperatures.Zip(_percents, (t, p) => new FanCurvePoint(t, p));
        return new FanCurve(points);
    }

    public IReadOnlyList<string> ValidateLocally() => FanCurveValidator.Validate(GetCurve());

    public void ResetToDefaults()
    {
        if (_temperatures.Length == 0) return;
        _suppressEvents = true;
        for (var i = 0; i < _temperatures.Length; i++)
            _percents[i] = DefaultFanPercent(_temperatures[i]);
        _suppressEvents = false;
        Relayout();
        RaiseCurveChanged();
    }

    public void ApplyMinimumSafePercents(FirmwareFanTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_temperatures.Length == 0) return;

        _suppressEvents = true;
        for (var i = 0; i < _temperatures.Length && i < table.MinimumSteps.Count; i++)
        {
            var temperature = _temperatures[i];
            var stepPercent = table.MinimumSteps[i] * 10d;
            _percents[i] = temperature >= 95 ? 100
                : temperature >= 85 ? 60
                : stepPercent;
        }
        _suppressEvents = false;
        Relayout();
        RaiseCurveChanged();
    }

    public void ClearSelection()
    {
        _selectedIndex = -1;
        _hoverIndex = -1;
        HideGuides();
        UpdateHandleVisuals();
        RaiseSelectionChanged();
    }

    public bool TryGetSelectedSummary(out double temperature, out double percent, out int firmwareStep,
        out int? estimatedRpm, out string safetyStatus)
    {
        temperature = 0;
        percent = 0;
        firmwareStep = 0;
        estimatedRpm = null;
        safetyStatus = "No point selected";
        if (_selectedIndex < 0 || _selectedIndex >= _temperatures.Length)
            return false;

        temperature = _temperatures[_selectedIndex];
        percent = _percents[_selectedIndex];
        firmwareStep = PercentToStep(percent);
        estimatedRpm = EstimateRpm(_selectedIndex, percent);
        safetyStatus = DescribeSafety(temperature, percent);
        return true;
    }

    private void RebuildHandles()
    {
        ClearHandles();
        for (var i = 0; i < _temperatures.Length; i++)
        {
            var index = i;
            var thumb = new Thumb
            {
                Width = PointRadius * 2,
                Height = PointRadius * 2,
                Cursor = Cursors.SizeNS,
                Template = CreateHandleTemplate(),
                Focusable = true,
                Tag = index
            };
            AutomationProperties.SetName(thumb, $"Fan curve point {_temperatures[i]:0}°C");
            AutomationProperties.SetHelpText(thumb,
                "Firmware temperature is locked. Use Up/Down or drag vertically to change fan percentage.");

            thumb.DragDelta += (_, e) => OnHandleDrag(index, e);
            thumb.DragStarted += (_, _) => SelectIndex(index);
            thumb.MouseEnter += (_, _) => SetHover(index);
            thumb.MouseLeave += (_, _) =>
            {
                if (_hoverIndex == index) SetHover(-1);
            };
            thumb.GotKeyboardFocus += (_, _) => SelectIndex(index);
            thumb.PreviewMouseLeftButtonDown += (_, _) => SelectIndex(index);

            var handle = new CurveHandle(thumb);
            _handles.Add(handle);
            _plot.Children.Add(thumb);
        }
    }

    private void ClearHandles()
    {
        foreach (var handle in _handles)
            _plot.Children.Remove(handle.Thumb);
        _handles.Clear();
    }

    private static ControlTemplate CreateHandleTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var factory = new FrameworkElementFactory(typeof(Ellipse));
        factory.Name = "dot";
        factory.SetValue(Shape.WidthProperty, PointRadius * 2);
        factory.SetValue(Shape.HeightProperty, PointRadius * 2);
        factory.SetValue(Shape.StrokeThicknessProperty, 2d);
        factory.SetValue(Shape.FillProperty, new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB)));
        factory.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0x62, 0xA8, 0xFF)));
        template.VisualTree = factory;

        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(0xE2, 0x23, 0x1A)), "dot"));
        focusTrigger.Setters.Add(new Setter(Shape.StrokeThicknessProperty, 2.5d, "dot"));
        template.Triggers.Add(focusTrigger);
        return template;
    }

    private void OnHandleDrag(int index, DragDeltaEventArgs e)
    {
        if (index < 0 || index >= _percents.Length) return;
        var plot = GetPlotRect();
        if (plot.Height <= 0) return;

        var currentY = PercentToY(_percents[index], plot);
        var nextY = Math.Clamp(currentY + e.VerticalChange, plot.Top, plot.Bottom);
        var nextPercent = SnapPercent(YToPercent(nextY, plot));
        SetPercent(index, nextPercent, raise: true);
        ShowGuides(index);
        ShowTooltip(index);
    }

    private void SetPercent(int index, double percent, bool raise)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (Math.Abs(_percents[index] - percent) < 0.01) return;
        _percents[index] = percent;
        PositionHandle(index);
        DrawCurve();
        if (raise && !_suppressEvents)
            RaiseCurveChanged();
    }

    private void SelectIndex(int index)
    {
        if (_selectedIndex == index) return;
        _selectedIndex = index;
        UpdateHandleVisuals();
        if (index >= 0)
        {
            ShowGuides(index);
            ShowTooltip(index);
        }
        else
        {
            HideGuides();
        }
        RaiseSelectionChanged();
    }

    private void SetHover(int index)
    {
        _hoverIndex = index;
        UpdateHandleVisuals();
        if (index >= 0)
        {
            ShowGuides(index);
            ShowTooltip(index);
        }
        else if (_selectedIndex < 0)
        {
            HideGuides();
        }
        else
        {
            ShowGuides(_selectedIndex);
            ShowTooltip(_selectedIndex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _percents.Length) return;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10d : 1d;
        if (e.Key is Key.Up or Key.OemPlus or Key.Add)
        {
            SetPercent(_selectedIndex, SnapPercent(_percents[_selectedIndex] + step), raise: true);
            ShowGuides(_selectedIndex);
            ShowTooltip(_selectedIndex);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.OemMinus or Key.Subtract)
        {
            SetPercent(_selectedIndex, SnapPercent(_percents[_selectedIndex] - step), raise: true);
            ShowGuides(_selectedIndex);
            ShowTooltip(_selectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && _selectedIndex > 0)
        {
            SelectIndex(_selectedIndex - 1);
            _handles[_selectedIndex].Thumb.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && _selectedIndex < _handles.Count - 1)
        {
            SelectIndex(_selectedIndex + 1);
            _handles[_selectedIndex].Thumb.Focus();
            e.Handled = true;
        }
    }

    private void Relayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        _root.Width = ActualWidth;
        _root.Height = ActualHeight;
        _plot.Width = ActualWidth;
        _plot.Height = ActualHeight;

        EnsureAxisChrome();
        LayoutAxisChrome();
        DrawSafetyRegion();
        DrawCurve();
        for (var i = 0; i < _handles.Count; i++)
            PositionHandle(i);
        DrawCurrentTemperature();
        UpdateHandleVisuals();

        if (_selectedIndex >= 0)
        {
            ShowGuides(_selectedIndex);
            ShowTooltip(_selectedIndex);
        }
    }

    private void EnsureAxisChrome()
    {
        if (_gridLines.Count > 0) return;

        var plotBackground = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1D)),
            RadiusX = 8,
            RadiusY = 8,
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x40)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        _plot.Children.Insert(0, plotBackground);
        // Keep reference via Tag for layout.
        plotBackground.Tag = "plot-bg";

        for (var i = 0; i < 6; i++)
        {
            var h = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x9A, 0xA8, 0xBA)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _gridLines.Add(h);
            _plot.Children.Insert(1, h);

            var label = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xBA)),
                Text = $"{i * 20}%"
            };
            _yLabels.Add(label);
            _root.Children.Add(label);
        }

        for (var t = 40; t <= 100; t += 10)
        {
            var v = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x28, 0x9A, 0xA8, 0xBA)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _gridLines.Add(v);
            _plot.Children.Insert(1, v);

            var label = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xBA)),
                Text = $"{t}°C"
            };
            _xLabels.Add(label);
            _root.Children.Add(label);
        }
    }

    private void LayoutAxisChrome()
    {
        var plot = GetPlotRect();
        foreach (UIElement child in _plot.Children)
        {
            if (child is Rectangle { Tag: "plot-bg" } bg)
            {
                bg.Width = plot.Width;
                bg.Height = plot.Height;
                Canvas.SetLeft(bg, plot.Left);
                Canvas.SetTop(bg, plot.Top);
            }
        }

        // Horizontal grid + Y labels (0..100)
        for (var i = 0; i < 6; i++)
        {
            var percent = i * 20d;
            var y = PercentToY(percent, plot);
            var line = _gridLines[i];
            line.X1 = plot.Left;
            line.X2 = plot.Right;
            line.Y1 = y;
            line.Y2 = y;

            var label = _yLabels[i];
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, plot.Left - label.DesiredSize.Width - 8);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2d);
        }

        // Vertical grid + X labels (40..100)
        for (var i = 0; i < 7; i++)
        {
            var temp = 40 + (i * 10);
            var x = TempToX(temp, plot);
            var line = _gridLines[6 + i];
            line.X1 = x;
            line.X2 = x;
            line.Y1 = plot.Top;
            line.Y2 = plot.Bottom;

            var label = _xLabels[i];
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2d);
            Canvas.SetTop(label, plot.Bottom + 8);
        }
    }

    private void DrawSafetyRegion()
    {
        var plot = GetPlotRect();
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            _safetyRegion.Data = Geometry.Empty;
            _safetyFloorLine.Data = Geometry.Empty;
            return;
        }

        // Shade the unsafe zone below the safety floor (visual cue only; validator is authoritative).
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var x85 = TempToX(85, plot);
            var x95 = TempToX(95, plot);
            var x100 = TempToX(100, plot);
            var y0 = PercentToY(0, plot);
            var y60 = PercentToY(SafetyFloor85, plot);
            var y100 = PercentToY(SafetyFloor95, plot);

            // 85–95°C: below 60% is unsafe
            ctx.BeginFigure(new Point(x85, y0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x95, y0), true, false);
            ctx.LineTo(new Point(x95, y60), true, false);
            ctx.LineTo(new Point(x85, y60), true, false);

            // ≥95°C: below 100% is unsafe
            ctx.BeginFigure(new Point(x95, y0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x100, y0), true, false);
            ctx.LineTo(new Point(x100, y100), true, false);
            ctx.LineTo(new Point(x95, y100), true, false);
        }
        geo.Freeze();
        _safetyRegion.Data = geo;

        var floor = new StreamGeometry();
        using (var ctx = floor.Open())
        {
            var x85 = TempToX(85, plot);
            var x95 = TempToX(95, plot);
            var x100 = Math.Min(plot.Right, TempToX(100, plot));
            ctx.BeginFigure(new Point(x85, PercentToY(SafetyFloor85, plot)), false, false);
            ctx.LineTo(new Point(x95, PercentToY(SafetyFloor85, plot)), true, false);
            ctx.LineTo(new Point(x95, PercentToY(SafetyFloor95, plot)), true, false);
            ctx.LineTo(new Point(x100, PercentToY(SafetyFloor95, plot)), true, false);
        }
        floor.Freeze();
        _safetyFloorLine.Data = floor;
    }

    private void DrawCurve()
    {
        var plot = GetPlotRect();
        if (_temperatures.Length == 0 || plot.Width <= 0)
        {
            _curvePath.Data = Geometry.Empty;
            return;
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var first = new Point(TempToX(_temperatures[0], plot), PercentToY(_percents[0], plot));
            ctx.BeginFigure(first, isFilled: true, isClosed: true);
            for (var i = 1; i < _temperatures.Length; i++)
                ctx.LineTo(new Point(TempToX(_temperatures[i], plot), PercentToY(_percents[i], plot)), true, false);

            // Close down to baseline for soft fill.
            ctx.LineTo(new Point(TempToX(_temperatures[^1], plot), plot.Bottom), true, false);
            ctx.LineTo(new Point(TempToX(_temperatures[0], plot), plot.Bottom), true, false);
        }
        geo.Freeze();
        _curvePath.Data = geo;
    }

    private void PositionHandle(int index)
    {
        if (index < 0 || index >= _handles.Count) return;
        var plot = GetPlotRect();
        var x = TempToX(_temperatures[index], plot) - PointRadius;
        var y = PercentToY(_percents[index], plot) - PointRadius;
        Canvas.SetLeft(_handles[index].Thumb, x);
        Canvas.SetTop(_handles[index].Thumb, y);
        _handles[index].Thumb.ToolTip =
            $"{_temperatures[index]:0}°C · {_percents[index]:0}% · " +
            $"{FormatRpm(EstimateRpm(index, _percents[index]))} · Step {PercentToStep(_percents[index])}/10";
    }

    private void UpdateHandleVisuals()
    {
        for (var i = 0; i < _handles.Count; i++)
        {
            var active = i == _selectedIndex || i == _hoverIndex;
            var scale = active ? 1.25 : 1.0;
            _handles[i].Thumb.RenderTransformOrigin = new Point(0.5, 0.5);
            _handles[i].Thumb.RenderTransform = new ScaleTransform(scale, scale);
            _handles[i].Thumb.Opacity = 1.0;
        }
    }

    private void ShowGuides(int index)
    {
        if (index < 0 || index >= _temperatures.Length)
        {
            HideGuides();
            return;
        }

        var plot = GetPlotRect();
        var x = TempToX(_temperatures[index], plot);
        var y = PercentToY(_percents[index], plot);

        _guideV.X1 = x;
        _guideV.X2 = x;
        _guideV.Y1 = plot.Top;
        _guideV.Y2 = plot.Bottom;
        _guideV.Visibility = Visibility.Visible;

        _guideH.X1 = plot.Left;
        _guideH.X2 = plot.Right;
        _guideH.Y1 = y;
        _guideH.Y2 = y;
        _guideH.Visibility = Visibility.Visible;
    }

    private void HideGuides()
    {
        _guideV.Visibility = Visibility.Collapsed;
        _guideH.Visibility = Visibility.Collapsed;
        _tooltip.Visibility = Visibility.Collapsed;
    }

    private void ShowTooltip(int index)
    {
        if (index < 0 || index >= _temperatures.Length)
        {
            _tooltip.Visibility = Visibility.Collapsed;
            return;
        }

        var plot = GetPlotRect();
        var temp = _temperatures[index];
        var percent = _percents[index];
        var step = PercentToStep(percent);
        _tooltipText.Text = $"{temp:0}°C  ·  {percent:0}%  ·  " +
            $"{FormatRpm(EstimateRpm(index, percent))}  ·  Step {step}/10";
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var x = TempToX(temp, plot) + 12;
        var y = PercentToY(percent, plot) - _tooltip.DesiredSize.Height - 8;
        if (x + _tooltip.DesiredSize.Width > ActualWidth - 4)
            x = TempToX(temp, plot) - _tooltip.DesiredSize.Width - 12;
        if (y < 2) y = PercentToY(percent, plot) + 12;

        Canvas.SetLeft(_tooltip, Math.Max(2, x));
        Canvas.SetTop(_tooltip, Math.Max(2, y));
        _tooltip.Visibility = Visibility.Visible;
    }

    private void DrawCurrentTemperature()
    {
        var plot = GetPlotRect();
        if (_currentTemperature is not double temp || temp < TempMin || temp > TempMax || plot.Width <= 0)
        {
            _currentTempLine.Visibility = Visibility.Collapsed;
            return;
        }

        var x = TempToX(temp, plot);
        _currentTempLine.X1 = x;
        _currentTempLine.X2 = x;
        _currentTempLine.Y1 = plot.Top;
        _currentTempLine.Y2 = plot.Bottom;
        _currentTempLine.Visibility = Visibility.Visible;
    }

    private Rect GetPlotRect()
    {
        var width = Math.Max(0, ActualWidth - PlotLeft - PlotRightPad);
        var height = Math.Max(0, ActualHeight - PlotTop - PlotBottomPad);
        return new Rect(PlotLeft, PlotTop, width, height);
    }

    private static double TempToX(double temp, Rect plot)
    {
        var t = Math.Clamp((temp - TempMin) / (TempMax - TempMin), 0, 1);
        return plot.Left + (t * plot.Width);
    }

    private static double PercentToY(double percent, Rect plot)
    {
        var p = Math.Clamp(percent, 0, 100) / 100d;
        return plot.Bottom - (p * plot.Height);
    }

    private static double YToPercent(double y, Rect plot)
    {
        if (plot.Height <= 0) return 0;
        var p = (plot.Bottom - y) / plot.Height;
        return Math.Clamp(p * 100d, 0, 100);
    }

    private static double SnapPercent(double percent) =>
        Math.Clamp(Math.Round(percent / 10d) * 10d, 0, 100);

    private static int PercentToStep(double percent) =>
        Math.Clamp((int)Math.Round(percent / 10d), 0, 10);

    private int? EstimateRpm(int index, double percent)
    {
        if (index < 0 || index >= _fanSpeedsRpm.Length)
            return null;
        var maximum = _fanSpeedsRpm[index];
        return maximum > 0 ? (int)Math.Round(maximum * Math.Clamp(percent, 0, 100) / 100d) : null;
    }

    private static string FormatRpm(int? rpm) =>
        rpm is int value ? $"~{value:N0} RPM" : "RPM unavailable";

    private static double DefaultFanPercent(double temperature) =>
        temperature >= 90 ? 100 : temperature >= 80 ? 75 : temperature >= 70 ? 50 : 25;

    private static string DescribeSafety(double temperature, double percent)
    {
        if (temperature >= 95)
            return percent >= 100 ? "Meets 95°C floor (100%)" : "Below 95°C safety floor";
        if (temperature >= 85)
            return percent >= 60 ? "Meets 85°C floor (≥60%)" : "Below 85°C safety floor";
        return "Within normal range";
    }

    private void RaiseCurveChanged() => CurveChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private sealed class CurveHandle(Thumb thumb)
    {
        public Thumb Thumb { get; } = thumb;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LenovoLoqControl.Core;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

/// <summary>Animated radial temperature indicator. Does not invent values — null keeps an empty ring.</summary>
public sealed class TemperatureRing : ContentControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double?), typeof(TemperatureRing),
            new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TemperatureRing),
            new PropertyMetadata(100d, OnVisualChanged));

    public static readonly DependencyProperty ThermalStateProperty =
        DependencyProperty.Register(nameof(ThermalState), typeof(ThermalState), typeof(TemperatureRing),
            new PropertyMetadata(ThermalState.Unknown, OnVisualChanged));

    public static readonly DependencyProperty IsAnimatingProperty =
        DependencyProperty.Register(nameof(IsAnimating), typeof(bool), typeof(TemperatureRing),
            new PropertyMetadata(true));

    private readonly Path _track;
    private readonly Path _progress;
    private readonly TextBlock _valueText;
    private readonly TextBlock _unitText;
    private readonly TextBlock _stateText;
    private double _displayedProgress;
    private EventHandler? _renderHandler;
    private DateTime _animStart;
    private double _animFrom;
    private double _animTo;
    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(420);

    public TemperatureRing()
    {
        Width = 132;
        Height = 132;
        Focusable = false;

        _track = new Path
        {
            StrokeThickness = 10,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = TryBrush("Brush.Overlay") ?? Brushes.DimGray
        };
        _progress = new Path
        {
            StrokeThickness = 10,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = TryBrush("Brush.TextSecondary") ?? Brushes.Gray
        };
        _valueText = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        _unitText = new TextBlock
        {
            Text = "°C",
            FontSize = 11,
            Foreground = TryBrush("Brush.TextSecondary") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        _stateText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Brush.TextSecondary") ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var readout = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        readout.Children.Add(_valueText);
        readout.Children.Add(_unitText);
        readout.Children.Add(_stateText);

        var grid = new Grid();
        grid.Children.Add(_track);
        grid.Children.Add(_progress);
        grid.Children.Add(readout);
        Content = grid;

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        Unloaded += (_, _) => StopAnimation();
    }

    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public ThermalState ThermalState
    {
        get => (ThermalState)GetValue(ThermalStateProperty);
        set => SetValue(ThermalStateProperty, value);
    }

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TemperatureRing ring)
            ring.UpdateVisual();
    }

    private void UpdateVisual()
    {
        var state = Value is null ? ThermalState.Unknown : ThermalState;
        _progress.Stroke = BrushFor(state);
        _stateText.Foreground = _progress.Stroke;
        _stateText.Text = state switch
        {
            ThermalState.Cool => "Cool",
            ThermalState.Normal => "Normal",
            ThermalState.Warm => "Warm",
            ThermalState.Hot => "Hot",
            ThermalState.Critical => "Critical",
            _ => "Unavailable"
        };

        if (Value is double value)
        {
            _valueText.Text = value.ToString("0");
            _valueText.Foreground = TryBrush("Brush.TextPrimary") ?? Brushes.White;
            _unitText.Visibility = Visibility.Visible;
            var target = Math.Clamp(value / Math.Max(1, Maximum), 0, 1);
            if (IsAnimating && IsLoaded && !AppUiPreferences.ReducedMotion)
                AnimateProgress(target);
            else
            {
                StopAnimation();
                _displayedProgress = target;
                Redraw();
            }
        }
        else
        {
            StopAnimation();
            _valueText.Text = "—";
            _valueText.Foreground = TryBrush("Brush.TextSecondary") ?? Brushes.Gray;
            _unitText.Visibility = Visibility.Collapsed;
            _displayedProgress = 0;
            Redraw();
        }
    }

    private void AnimateProgress(double target)
    {
        StopAnimation();
        _animFrom = _displayedProgress;
        _animTo = target;
        _animStart = DateTime.UtcNow;
        _renderHandler = (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - _animStart).TotalMilliseconds / AnimDuration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _displayedProgress = _animFrom + (_animTo - _animFrom) * eased;
            Redraw();
            if (t >= 1)
                StopAnimation();
        };
        CompositionTarget.Rendering += _renderHandler;
    }

    private void StopAnimation()
    {
        if (_renderHandler is null) return;
        CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
    }

    private void Redraw()
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Min(w, h) / 2 - 8;
        _track.Data = BuildArc(cx, cy, radius, 0.999);
        _progress.Data = _displayedProgress <= 0.001
            ? Geometry.Empty
            : BuildArc(cx, cy, radius, Math.Clamp(_displayedProgress, 0.001, 0.999));
    }

    private static PathGeometry BuildArc(double cx, double cy, double radius, double progress)
    {
        const double startDegrees = 140;
        const double sweepDegrees = 260;
        var start = Polar(cx, cy, radius, startDegrees);
        var end = Polar(cx, cy, radius, startDegrees + sweepDegrees * progress);
        var large = sweepDegrees * progress > 180;

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = large,
            SweepDirection = SweepDirection.Clockwise,
            IsStroked = true
        });
        return new PathGeometry(new[] { fig });
    }

    private static Point Polar(double cx, double cy, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180d;
        return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }

    private static Brush BrushFor(ThermalState state) => state switch
    {
        ThermalState.Cool => TryBrush("Brush.ThermalCool") ?? Brushes.MediumSeaGreen,
        ThermalState.Normal => TryBrush("Brush.ThermalNormal") ?? Brushes.CornflowerBlue,
        ThermalState.Warm => TryBrush("Brush.ThermalWarm") ?? Brushes.Goldenrod,
        ThermalState.Hot => TryBrush("Brush.ThermalHot") ?? Brushes.OrangeRed,
        ThermalState.Critical => TryBrush("Brush.ThermalCritical") ?? Brushes.Crimson,
        _ => TryBrush("Brush.TextSecondary") ?? Brushes.Gray
    };

    private static Brush? TryBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;
}

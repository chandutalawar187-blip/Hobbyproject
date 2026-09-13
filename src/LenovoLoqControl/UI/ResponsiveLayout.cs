using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LenovoLoqControl.UI;

/// <summary>
/// Shared responsive column / stack helpers with smooth settle motion on resize.
/// </summary>
public static class ResponsiveLayout
{
    public const double BreakpointWide = 760;
    public const double BreakpointMedium = 640;

    private static readonly Duration SettleOut = new(TimeSpan.FromMilliseconds(70));
    private static readonly Duration SettleIn = new(TimeSpan.FromMilliseconds(160));
    private const double SettleDim = 0.82;

    public static readonly DependencyProperty AdaptiveColumnsProperty =
        DependencyProperty.RegisterAttached(
            "AdaptiveColumns",
            typeof(bool),
            typeof(ResponsiveLayout),
            new PropertyMetadata(false, OnAdaptiveColumnsChanged));

    public static readonly DependencyProperty WideColumnsProperty =
        DependencyProperty.RegisterAttached(
            "WideColumns",
            typeof(int),
            typeof(ResponsiveLayout),
            new PropertyMetadata(3));

    public static readonly DependencyProperty NarrowColumnsProperty =
        DependencyProperty.RegisterAttached(
            "NarrowColumns",
            typeof(int),
            typeof(ResponsiveLayout),
            new PropertyMetadata(1));

    public static readonly DependencyProperty BreakpointProperty =
        DependencyProperty.RegisterAttached(
            "Breakpoint",
            typeof(double),
            typeof(ResponsiveLayout),
            new PropertyMetadata(BreakpointWide));

    private static readonly DependencyProperty AppliedColumnsProperty =
        DependencyProperty.RegisterAttached(
            "AppliedColumns",
            typeof(int),
            typeof(ResponsiveLayout),
            new PropertyMetadata(-1));

    private static readonly DependencyProperty IsSettlingProperty =
        DependencyProperty.RegisterAttached(
            "IsSettling",
            typeof(bool),
            typeof(ResponsiveLayout),
            new PropertyMetadata(false));

    private static readonly DependencyProperty PendingColumnsProperty =
        DependencyProperty.RegisterAttached(
            "PendingColumns",
            typeof(int),
            typeof(ResponsiveLayout),
            new PropertyMetadata(-1));

    public static void SetAdaptiveColumns(DependencyObject element, bool value) =>
        element.SetValue(AdaptiveColumnsProperty, value);

    public static bool GetAdaptiveColumns(DependencyObject element) =>
        (bool)element.GetValue(AdaptiveColumnsProperty);

    public static void SetWideColumns(DependencyObject element, int value) =>
        element.SetValue(WideColumnsProperty, value);

    public static int GetWideColumns(DependencyObject element) =>
        (int)element.GetValue(WideColumnsProperty);

    public static void SetNarrowColumns(DependencyObject element, int value) =>
        element.SetValue(NarrowColumnsProperty, value);

    public static int GetNarrowColumns(DependencyObject element) =>
        (int)element.GetValue(NarrowColumnsProperty);

    public static void SetBreakpoint(DependencyObject element, double value) =>
        element.SetValue(BreakpointProperty, value);

    public static double GetBreakpoint(DependencyObject element) =>
        (double)element.GetValue(BreakpointProperty);

    private static void OnAdaptiveColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UniformGrid grid)
            return;

        if ((bool)e.NewValue)
        {
            grid.SizeChanged += OnAdaptiveGridSizeChanged;
            grid.Loaded += OnAdaptiveGridLoaded;
            grid.Unloaded += OnAdaptiveGridUnloaded;
            if (grid.IsLoaded)
                ApplyUniformColumns(grid, animate: false);
        }
        else
        {
            grid.SizeChanged -= OnAdaptiveGridSizeChanged;
            grid.Loaded -= OnAdaptiveGridLoaded;
            grid.Unloaded -= OnAdaptiveGridUnloaded;
        }
    }

    private static void OnAdaptiveGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UniformGrid grid)
            ApplyUniformColumns(grid, animate: false);
    }

    private static void OnAdaptiveGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UniformGrid grid)
            return;

        grid.SizeChanged -= OnAdaptiveGridSizeChanged;
        grid.Loaded -= OnAdaptiveGridLoaded;
        grid.Unloaded -= OnAdaptiveGridUnloaded;
        CancelSettle(grid);
    }

    private static void OnAdaptiveGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is UniformGrid grid && e.WidthChanged && e.NewSize.Width > 0)
            ApplyUniformColumns(grid, animate: true);
    }

    private static void ApplyUniformColumns(UniformGrid grid, bool animate)
    {
        var breakpoint = GetBreakpoint(grid);
        var wide = Math.Max(1, GetWideColumns(grid));
        var narrow = Math.Max(1, GetNarrowColumns(grid));
        var width = grid.ActualWidth;
        if (width <= 0)
            return;

        var target = width < breakpoint ? narrow : wide;
        var applied = (int)grid.GetValue(AppliedColumnsProperty);
        if (applied < 0)
        {
            RestoreOpacity(grid);
            grid.Columns = target;
            grid.SetValue(AppliedColumnsProperty, target);
            return;
        }

        if (target == applied)
            return;

        if ((bool)grid.GetValue(IsSettlingProperty))
        {
            grid.SetValue(PendingColumnsProperty, target);
            return;
        }

        grid.SetValue(AppliedColumnsProperty, target);
        SetColumnsSmooth(grid, target, animate);
    }

    /// <summary>
    /// Smoothly settles a UniformGrid into a new column count.
    /// </summary>
    public static void SetColumnsSmooth(UniformGrid grid, int columns, bool animate = true)
    {
        columns = Math.Max(1, columns);
        if (grid.Columns == columns && !(bool)grid.GetValue(IsSettlingProperty))
            return;

        if (!animate || !UiAnimation.ShouldAnimate)
        {
            CancelSettle(grid);
            grid.Columns = columns;
            ResetChildVisibility(grid);
            return;
        }

        grid.SetValue(IsSettlingProperty, true);
        grid.BeginAnimation(UIElement.OpacityProperty, null);

        var from = Math.Clamp(grid.Opacity <= 0 ? 1 : grid.Opacity, SettleDim, 1);
        var fadeOut = new DoubleAnimation(from, SettleDim, SettleOut)
        {
            EasingFunction = UiAnimation.EaseInOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!grid.IsLoaded)
            {
                CancelSettle(grid);
                return;
            }

            grid.Columns = columns;
            ResetChildVisibility(grid);

            var fadeIn = new DoubleAnimation(SettleDim, 1, SettleIn)
            {
                EasingFunction = UiAnimation.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            fadeIn.Completed += (_, _) => FinishSettle(grid);
            grid.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        grid.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static void FinishSettle(FrameworkElement host)
    {
        RestoreOpacity(host);
        host.SetValue(IsSettlingProperty, false);

        if (host is UniformGrid grid)
        {
            ResetChildVisibility(grid);
            var pending = (int)grid.GetValue(PendingColumnsProperty);
            if (pending > 0 && pending != grid.Columns)
            {
                grid.SetValue(PendingColumnsProperty, -1);
                grid.SetValue(AppliedColumnsProperty, pending);
                SetColumnsSmooth(grid, pending, animate: true);
                return;
            }

            grid.SetValue(PendingColumnsProperty, -1);
        }
    }

    private static void CancelSettle(FrameworkElement host)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        RestoreOpacity(host);
        host.SetValue(IsSettlingProperty, false);
        if (host is UniformGrid grid)
            grid.SetValue(PendingColumnsProperty, -1);
    }

    private static void RestoreOpacity(FrameworkElement host)
    {
        host.BeginAnimation(UIElement.OpacityProperty, null);
        if (host.Opacity < 0.999)
            host.Opacity = 1;
    }

    /// <summary>
    /// Soft opacity settle for a panel without zeroing child opacity (avoids blank cards).
    /// </summary>
    public static void PlayChildrenSettle(Panel panel)
    {
        if (panel is null)
            return;

        ResetChildVisibility(panel);
        RestoreOpacity(panel);

        if (!UiAnimation.ShouldAnimate)
            return;

        var pulse = new DoubleAnimation(SettleDim, 1, SettleIn)
        {
            EasingFunction = UiAnimation.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        pulse.Completed += (_, _) =>
        {
            RestoreOpacity(panel);
            ResetChildVisibility(panel);
        };
        panel.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    private static void ResetChildVisibility(Panel panel)
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe)
                continue;
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            if (fe.Opacity < 0.999)
                fe.Opacity = 1;
            fe.RenderTransform = Transform.Identity;
        }
    }

    /// <summary>
    /// Cross-fades a host while <paramref name="apply"/> mutates layout, then settles children.
    /// </summary>
    public static void Transition(FrameworkElement host, Action apply, Panel? settleChildren = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(apply);

        if (!UiAnimation.ShouldAnimate)
        {
            apply();
            if (settleChildren is not null)
                PlayChildrenSettle(settleChildren);
            return;
        }

        if ((bool)host.GetValue(IsSettlingProperty))
        {
            apply();
            RestoreOpacity(host);
            return;
        }

        host.SetValue(IsSettlingProperty, true);
        host.BeginAnimation(UIElement.OpacityProperty, null);

        var from = Math.Clamp(host.Opacity <= 0 ? 1 : host.Opacity, SettleDim, 1);
        var fadeOut = new DoubleAnimation(from, SettleDim, SettleOut)
        {
            EasingFunction = UiAnimation.EaseInOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!host.IsLoaded)
            {
                CancelSettle(host);
                return;
            }

            apply();
            if (settleChildren is not null)
                ResetChildVisibility(settleChildren);
            else if (host is Panel panel)
                ResetChildVisibility(panel);

            var fadeIn = new DoubleAnimation(SettleDim, 1, SettleIn)
            {
                EasingFunction = UiAnimation.EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            fadeIn.Completed += (_, _) => FinishSettle(host);
            host.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        host.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Animates Thickness properties used by the shell (padding) for smoother window resize.
    /// </summary>
    public static void AnimateThickness(FrameworkElement element, DependencyProperty property, Thickness to)
    {
        if (element.GetValue(property) is not Thickness from)
        {
            element.SetValue(property, to);
            return;
        }

        if (ThicknessEquals(from, to))
            return;

        if (!UiAnimation.ShouldAnimate)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, to);
            return;
        }

        var animation = new ThicknessAnimation(from, to, UiAnimation.DurationNormal)
        {
            EasingFunction = UiAnimation.EaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        element.BeginAnimation(property, animation);
    }

    private static bool ThicknessEquals(Thickness a, Thickness b) =>
        Math.Abs(a.Left - b.Left) < 0.5
        && Math.Abs(a.Top - b.Top) < 0.5
        && Math.Abs(a.Right - b.Right) < 0.5
        && Math.Abs(a.Bottom - b.Bottom) < 0.5;
}

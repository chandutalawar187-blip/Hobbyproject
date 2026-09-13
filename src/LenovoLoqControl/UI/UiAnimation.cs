using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LenovoLoqControl.UI;

/// <summary>
/// Shared UI motion helpers. Honors <see cref="AppUiPreferences.ReducedMotion"/>.
/// </summary>
public static class UiAnimation
{
    public static bool ShouldAnimate => !AppUiPreferences.ReducedMotion;

    public static readonly TimeSpan DurationFast = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan DurationNormal = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan DurationPage = TimeSpan.FromMilliseconds(280);

    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static readonly DependencyProperty StaggerChildrenOnLoadProperty =
        DependencyProperty.RegisterAttached(
            "StaggerChildrenOnLoad",
            typeof(bool),
            typeof(UiAnimation),
            new PropertyMetadata(false, OnStaggerChildrenOnLoadChanged));

    public static void SetStaggerChildrenOnLoad(DependencyObject element, bool value) =>
        element.SetValue(StaggerChildrenOnLoadProperty, value);

    public static bool GetStaggerChildrenOnLoad(DependencyObject element) =>
        (bool)element.GetValue(StaggerChildrenOnLoadProperty);

    private static void OnStaggerChildrenOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnStaggerHostLoaded;
            element.Unloaded += OnStaggerHostUnloaded;
        }
        else
        {
            element.Loaded -= OnStaggerHostLoaded;
            element.Unloaded -= OnStaggerHostUnloaded;
        }
    }

    private static void OnStaggerHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.Loaded -= OnStaggerHostLoaded;
            element.Unloaded -= OnStaggerHostUnloaded;
        }
    }

    private static void OnStaggerHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Panel panel)
            return;

        var index = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement fe)
                PlayEnter(fe, index++);
        }
    }

    public static void PlayEnter(FrameworkElement element, int staggerIndex = 0)
    {
        if (!ShouldAnimate)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Clamp(staggerIndex, 0, 12) * 45);
        var duration = DurationNormal;

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var translate = new TranslateTransform(0, 12);
        var scale = new ScaleTransform(0.992, 0.992);
        element.RenderTransform = new TransformGroup { Children = { translate, scale } };
        element.Opacity = 0;

        var ease = EaseOut;
        var fade = new DoubleAnimation(0, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var slide = new DoubleAnimation(12, 0, duration)
        {
            BeginTime = delay,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scaleX = new DoubleAnimation(0.992, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scaleY = new DoubleAnimation(0.992, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        fade.Completed += (_, _) => ClearEnterTransforms(element, translate, scale);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private static void ClearEnterTransforms(FrameworkElement element, TranslateTransform translate, ScaleTransform scale)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }

    public static void AnimateShellHeader(TextBlock title, TextBlock subtitle, string newTitle, string newSubtitle)
    {
        if (string.Equals(title.Text, newTitle, StringComparison.Ordinal) &&
            string.Equals(subtitle.Text, newSubtitle, StringComparison.Ordinal))
            return;

        if (!ShouldAnimate)
        {
            title.Text = newTitle;
            subtitle.Text = newSubtitle;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(140);
        var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = EaseOut };

        var sb = new Storyboard();
        sb.Children.Add(Clone(fadeOut, title));
        sb.Children.Add(Clone(fadeOut, subtitle));
        sb.Completed += (_, _) =>
        {
            title.Text = newTitle;
            subtitle.Text = newSubtitle;
            title.Opacity = 0;
            subtitle.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut };
            title.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            subtitle.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        };
        sb.Begin();
    }

    public static void PulseNavSelection(UIElement element)
    {
        if (!ShouldAnimate)
            return;

        element.RenderTransformOrigin = new Point(0, 0.5);
        var scale = element.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
        }

        var duration = DurationFast;
        var ease = EaseOut;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.02, duration) { EasingFunction = ease, AutoReverse = true });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.02, duration) { EasingFunction = ease, AutoReverse = true });
    }

    public static void NotifyBanner(FrameworkElement banner)
    {
        if (!ShouldAnimate)
            return;

        banner.RenderTransformOrigin = new Point(0.5, 0);
        var translate = banner.RenderTransform as TranslateTransform;
        if (translate is null)
        {
            translate = new TranslateTransform();
            banner.RenderTransform = translate;
        }

        var duration = DurationNormal;
        var ease = EaseOut;
        banner.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.72, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, duration) { EasingFunction = ease });
    }

    private static DoubleAnimation Clone(DoubleAnimation source, DependencyObject target)
    {
        var clone = new DoubleAnimation(source.From ?? 0d, source.To ?? 0d, source.Duration)
        {
            EasingFunction = source.EasingFunction,
            BeginTime = source.BeginTime,
            FillBehavior = source.FillBehavior
        };
        Storyboard.SetTarget(clone, target);
        Storyboard.SetTargetProperty(clone, new PropertyPath(UIElement.OpacityProperty));
        return clone;
    }
}

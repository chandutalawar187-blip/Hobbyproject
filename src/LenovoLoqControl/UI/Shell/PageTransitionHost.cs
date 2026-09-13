using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Shell;

public enum PageTransitionDirection
{
    Neutral,
    Forward,
    Backward
}

/// <summary>
/// Hosts page content with restrained fade / slide / scale enter transitions.
/// Respects Windows client-area animation preference for reduced motion.
/// </summary>
public sealed class PageTransitionHost : ContentControl
{
    public static readonly DependencyProperty PreferReducedMotionProperty =
        DependencyProperty.Register(
            nameof(PreferReducedMotion),
            typeof(bool),
            typeof(PageTransitionHost),
            new PropertyMetadata(false));

    public static readonly DependencyProperty TransitionDirectionProperty =
        DependencyProperty.Register(
            nameof(TransitionDirection),
            typeof(PageTransitionDirection),
            typeof(PageTransitionHost),
            new PropertyMetadata(PageTransitionDirection.Neutral));

    public PageTransitionHost()
    {
        Focusable = false;
        ClipToBounds = true;
        PreferReducedMotion = AppUiPreferences.ReducedMotion;
        Loaded += (_, _) => PreferReducedMotion = AppUiPreferences.ReducedMotion;
        AppUiPreferences.Changed += OnPreferencesChanged;
        Unloaded += (_, _) => AppUiPreferences.Changed -= OnPreferencesChanged;
    }

    private void OnPreferencesChanged() =>
        Dispatcher.Invoke(() => PreferReducedMotion = AppUiPreferences.ReducedMotion);

    public bool PreferReducedMotion
    {
        get => (bool)GetValue(PreferReducedMotionProperty);
        set => SetValue(PreferReducedMotionProperty, value);
    }

    public PageTransitionDirection TransitionDirection
    {
        get => (PageTransitionDirection)GetValue(TransitionDirectionProperty);
        set => SetValue(TransitionDirectionProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (newContent is not UIElement incoming)
            return;

        if (PreferReducedMotion || AppUiPreferences.ReducedMotion)
        {
            incoming.Opacity = 1;
            incoming.RenderTransform = Transform.Identity;
            return;
        }

        AnimateIn(incoming, TransitionDirection);
    }

    private static void AnimateIn(UIElement element, PageTransitionDirection direction)
    {
        var slideFrom = direction switch
        {
            PageTransitionDirection.Forward => 18d,
            PageTransitionDirection.Backward => -18d,
            _ => 12d
        };

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var translate = new TranslateTransform(0, slideFrom);
        var scale = new ScaleTransform(0.988, 0.988);
        element.RenderTransform = new TransformGroup { Children = { translate, scale } };
        element.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var slide = new DoubleAnimation(slideFrom, 0, duration) { EasingFunction = ease };
        var scaleX = new DoubleAnimation(0.988, 1, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(0.988, 1, duration) { EasingFunction = ease };

        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
        };

        element.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }
}

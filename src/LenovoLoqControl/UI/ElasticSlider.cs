using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace LenovoLoqControl.UI;

/// <summary>
/// Attached elastic motion for <see cref="Slider"/>: click/jump overshoot and
/// rubber-band stretch at the ends / under fast drag. Visual-only — logical
/// <see cref="RangeBase.Value"/> stays correct for bindings and hardware writes.
/// </summary>
public static class ElasticSlider
{
    private const double MaxRubberStretch = 0.32;
    private const double MaxVelocityStretch = 0.14;
    private const double MaxSettlePixels = 120;
    private const double ClickSettleMs = 200;
    private const double ClickMoveSlop = 10;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ElasticSlider),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(ElasticState),
            typeof(ElasticSlider),
            new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        if ((bool)e.NewValue)
            Attach(slider);
        else
            Detach(slider);
    }

    private static void Attach(Slider slider)
    {
        Detach(slider);
        var state = new ElasticState(slider);
        slider.SetValue(StateProperty, state);
        state.Attach();
    }

    private static void Detach(Slider slider)
    {
        if (slider.GetValue(StateProperty) is not ElasticState state)
            return;

        state.Detach();
        slider.ClearValue(StateProperty);
    }

    private sealed class ElasticState
    {
        private readonly Slider _slider;
        private Thumb? _thumb;
        private FrameworkElement? _fill;
        private TranslateTransform? _thumbTranslate;
        private ScaleTransform? _thumbScale;
        private ScaleTransform? _fillScale;
        private Storyboard? _settleStoryboard;

        private bool _gesture;
        private bool _dragging;
        private bool _relaxedSnap;
        private bool _savedSnapToTick;
        private bool _suppressValueAnimate;
        private double _pressValue;
        private Point _pressPos;
        private DateTime _pressUtc;
        private double _lastMouseX;
        private DateTime _lastMoveUtc;
        private double _velocityPxPerSec;
        private bool _hooksWired;

        public ElasticState(Slider slider) => _slider = slider;

        public void Attach()
        {
            _slider.Loaded += OnLoaded;
            _slider.Unloaded += OnUnloaded;
            if (_slider.IsLoaded)
                OnLoaded(_slider, new RoutedEventArgs());
        }

        public void Detach()
        {
            StopSettle();
            if (_relaxedSnap)
            {
                _relaxedSnap = false;
                _slider.IsSnapToTickEnabled = _savedSnapToTick;
            }

            ResetTransformsInstant();
            UnhookInteraction();
            _slider.Loaded -= OnLoaded;
            _slider.Unloaded -= OnUnloaded;
            _thumb = null;
            _fill = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureParts();
            HookInteraction();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopSettle();
            ResetTransformsInstant();
            UnhookInteraction();
        }

        private void HookInteraction()
        {
            if (_hooksWired) return;
            _hooksWired = true;
            _slider.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            _slider.PreviewMouseMove += OnPreviewMouseMove;
            _slider.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            _slider.LostMouseCapture += OnLostCapture;
            _slider.PreviewKeyDown += OnPreviewKeyDown;
            _slider.ValueChanged += OnValueChanged;
        }

        private void UnhookInteraction()
        {
            if (!_hooksWired) return;
            _hooksWired = false;
            _slider.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            _slider.PreviewMouseMove -= OnPreviewMouseMove;
            _slider.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
            _slider.LostMouseCapture -= OnLostCapture;
            _slider.PreviewKeyDown -= OnPreviewKeyDown;
            _slider.ValueChanged -= OnValueChanged;
            if (_thumb is not null)
            {
                _thumb.DragStarted -= OnDragStarted;
                _thumb.DragCompleted -= OnDragCompleted;
            }
        }

        private void EnsureParts()
        {
            _thumb ??= FindVisualChild<Thumb>(_slider);
            if (_thumb is null)
                return;

            _thumb.DragStarted -= OnDragStarted;
            _thumb.DragCompleted -= OnDragCompleted;
            _thumb.DragStarted += OnDragStarted;
            _thumb.DragCompleted += OnDragCompleted;

            EnsureThumbTransforms();
            _fill ??= FindDecreaseFill(_slider);
            EnsureFillTransform();
        }

        private void EnsureThumbTransforms()
        {
            if (_thumb is null) return;

            if (_thumb.RenderTransform is TransformGroup existing
                && existing.Children.Count >= 2
                && existing.Children[0] is ScaleTransform scale
                && existing.Children[1] is TranslateTransform translate)
            {
                _thumbScale = scale;
                _thumbTranslate = translate;
                return;
            }

            _thumbScale = new ScaleTransform(1, 1);
            _thumbTranslate = new TranslateTransform(0, 0);
            _thumb.RenderTransformOrigin = new Point(0.5, 0.5);
            _thumb.RenderTransform = new TransformGroup
            {
                Children = { _thumbScale, _thumbTranslate }
            };
        }

        private void EnsureFillTransform()
        {
            if (_fill is null) return;

            if (_fill.RenderTransform is ScaleTransform existing)
            {
                _fillScale = existing;
                return;
            }

            _fillScale = new ScaleTransform(1, 1);
            _fill.RenderTransformOrigin = new Point(0, 0.5);
            _fill.RenderTransform = _fillScale;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, _slider)
                && e.OriginalSource is not DependencyObject)
                return;

            EnsureParts();
            _gesture = true;
            _pressValue = _slider.Value;
            _pressPos = e.GetPosition(_slider);
            _pressUtc = DateTime.UtcNow;
            _lastMouseX = _pressPos.X;
            _lastMoveUtc = _pressUtc;
            _velocityPxPerSec = 0;
            StopSettle();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            {
                _gesture = true;
                _pressValue = _slider.Value;
                StopSettle();
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            EnsureParts();
            var now = DateTime.UtcNow;
            var x = e.GetPosition(_slider).X;
            var dt = (now - _lastMoveUtc).TotalSeconds;
            if (dt > 0.001 && dt < 0.25)
            {
                var instant = (x - _lastMouseX) / dt;
                _velocityPxPerSec = (_velocityPxPerSec * 0.65) + (instant * 0.35);
            }

            _lastMouseX = x;
            _lastMoveUtc = now;
            ApplyLiveRubber(e.GetPosition(GetTrackHost()));
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) =>
            FinishPointerGesture(e.GetPosition(_slider));

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_dragging || _gesture)
                FinishPointerGesture(e.GetPosition(_slider));
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            _dragging = true;
            StopSettle();
            ResetTransformsInstant();
            RelaxSnapDuringDrag();
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragging = false;
            RestoreSnapAfterDrag();
            FinishPointerGesture(Mouse.GetPosition(_slider));
        }

        private void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressValueAnimate || !_gesture || _dragging)
                return;
            if (!UiAnimation.ShouldAnimate || !IsInteractable())
            {
                _gesture = false;
                return;
            }

            // Keyboard / non-drag value jumps: elastic settle.
            EnsureParts();
            PlayJumpSettle(e.OldValue, e.NewValue);
            _gesture = false;
        }

        private void FinishPointerGesture(Point releasePos)
        {
            if (!_gesture && !_dragging)
                return;

            _dragging = false;
            if (!UiAnimation.ShouldAnimate || !IsInteractable())
            {
                ResetTransformsInstant();
                _gesture = false;
                return;
            }

            EnsureParts();
            var elapsedMs = (DateTime.UtcNow - _pressUtc).TotalMilliseconds;
            var travel = Math.Abs(releasePos.X - _pressPos.X);
            var isClickJump = elapsedMs <= ClickSettleMs || travel <= ClickMoveSlop;

            if (isClickJump && Math.Abs(_slider.Value - _pressValue) > double.Epsilon)
                PlayJumpSettle(_pressValue, _slider.Value);
            else
                PlayReleaseBounce();

            _gesture = false;
        }

        private void RelaxSnapDuringDrag()
        {
            if (!_slider.IsSnapToTickEnabled || _relaxedSnap)
                return;

            _savedSnapToTick = true;
            _relaxedSnap = true;
            _slider.IsSnapToTickEnabled = false;
        }

        private void RestoreSnapAfterDrag()
        {
            if (!_relaxedSnap)
                return;

            _relaxedSnap = false;
            _slider.IsSnapToTickEnabled = _savedSnapToTick;
            if (!_savedSnapToTick)
                return;

            var tick = _slider.TickFrequency;
            if (tick <= 0 || !IsFinite(tick))
                return;

            var range = _slider.Maximum - _slider.Minimum;
            if (range <= 0)
                return;

            var steps = Math.Round((_slider.Value - _slider.Minimum) / tick);
            var snapped = Math.Clamp(_slider.Minimum + (steps * tick), _slider.Minimum, _slider.Maximum);
            if (Math.Abs(snapped - _slider.Value) <= double.Epsilon)
                return;

            _suppressValueAnimate = true;
            try
            {
                _slider.Value = snapped;
            }
            finally
            {
                _suppressValueAnimate = false;
            }
        }

        private void ApplyLiveRubber(Point posInTrackHost)
        {
            if (_thumbScale is null || _thumb is null)
                return;

            var range = _slider.Maximum - _slider.Minimum;
            if (range <= 0 || !IsFinite(posInTrackHost.X))
                return;

            var host = GetTrackHost();
            var width = Math.Max(1, host.ActualWidth);
            var thumbW = Math.Max(1, _thumb.ActualWidth);
            var usable = Math.Max(1, width - thumbW);
            var frac = (_slider.Value - _slider.Minimum) / range;
            var thumbCenter = (thumbW * 0.5) + (frac * usable);

            double stretch = 1;
            double originX = 0.5;

            var atMin = _slider.Value <= _slider.Minimum + (range * 0.001);
            var atMax = _slider.Value >= _slider.Maximum - (range * 0.001);

            if (atMin && posInTrackHost.X < thumbCenter)
            {
                var over = thumbCenter - posInTrackHost.X;
                stretch = 1 + Math.Min(MaxRubberStretch, over / 90.0);
                originX = 0;
            }
            else if (atMax && posInTrackHost.X > thumbCenter)
            {
                var over = posInTrackHost.X - thumbCenter;
                stretch = 1 + Math.Min(MaxRubberStretch, over / 90.0);
                originX = 1;
            }
            else
            {
                var speed = Math.Abs(_velocityPxPerSec);
                stretch = 1 + Math.Min(MaxVelocityStretch, speed / 2800.0);
                originX = _velocityPxPerSec >= 0 ? 0.15 : 0.85;
            }

            _thumb.RenderTransformOrigin = new Point(originX, 0.5);
            _thumbScale.ScaleX = stretch;
            _thumbScale.ScaleY = 1 - Math.Min(0.1, (stretch - 1) * 0.35);

            if (_fillScale is not null)
            {
                _fill!.RenderTransformOrigin = new Point(0, 0.5);
                // Mild fill stretch only near the max end so the bar feels elastic too.
                _fillScale.ScaleX = atMax && stretch > 1
                    ? 1 + ((stretch - 1) * 0.45)
                    : 1;
            }
        }

        private void PlayJumpSettle(double fromValue, double toValue)
        {
            if (_thumbTranslate is null || _thumbScale is null)
                return;

            var deltaPx = ValueDeltaToPixels(fromValue, toValue);
            if (!IsFinite(deltaPx) || Math.Abs(deltaPx) < 0.5)
            {
                PlayReleaseBounce();
                return;
            }

            deltaPx = Math.Clamp(deltaPx, -MaxSettlePixels, MaxSettlePixels);
            StopSettle();

            _thumbTranslate.X = deltaPx;
            var jumpStretch = 1 + Math.Min(0.2, Math.Abs(deltaPx) / 420.0);
            _thumb!.RenderTransformOrigin = new Point(deltaPx > 0 ? 0 : 1, 0.5);
            _thumbScale.ScaleX = jumpStretch;
            _thumbScale.ScaleY = 1 - Math.Min(0.08, (jumpStretch - 1) * 0.4);

            if (_fillScale is not null)
            {
                var fromFrac = ValueToFraction(fromValue);
                var toFrac = ValueToFraction(toValue);
                if (toFrac > 0.001 && IsFinite(fromFrac) && IsFinite(toFrac))
                {
                    var fillFrom = Math.Clamp(fromFrac / toFrac, 0.05, 4.0);
                    _fill!.RenderTransformOrigin = new Point(0, 0.5);
                    _fillScale.ScaleX = fillFrom;
                }
            }

            var duration = TimeSpan.FromMilliseconds(Math.Clamp(160 + Math.Abs(deltaPx) * 0.55, 220, 420));
            var ease = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut };
            var soft = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sb = new Storyboard();
            sb.Children.Add(CreateDoubleAnimation(_thumbTranslate, TranslateTransform.XProperty, 0, duration, ease));
            sb.Children.Add(CreateDoubleAnimation(_thumbScale, ScaleTransform.ScaleXProperty, 1, duration, ease));
            sb.Children.Add(CreateDoubleAnimation(_thumbScale, ScaleTransform.ScaleYProperty, 1, duration, soft));
            if (_fillScale is not null)
                sb.Children.Add(CreateDoubleAnimation(_fillScale, ScaleTransform.ScaleXProperty, 1, duration, ease));

            _settleStoryboard = sb;
            sb.Completed += OnSettleCompleted;
            sb.Begin();
        }

        private void PlayReleaseBounce()
        {
            if (_thumbTranslate is null || _thumbScale is null)
                return;

            StopSettle();

            var impulse = Math.Clamp(_velocityPxPerSec * 0.045, -28, 28);
            if (Math.Abs(impulse) > 1.5 && Math.Abs(_thumbTranslate.X) < 0.5)
                _thumbTranslate.X = impulse;

            var stretch = _thumbScale.ScaleX;
            if (stretch < 1.02 && Math.Abs(_thumbTranslate.X) < 0.5)
            {
                ResetTransformsInstant();
                return;
            }

            _thumb!.RenderTransformOrigin = new Point(
                _velocityPxPerSec >= 0 ? 0.2 : 0.8,
                0.5);

            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut };
            var soft = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sb = new Storyboard();
            sb.Children.Add(CreateDoubleAnimation(_thumbTranslate, TranslateTransform.XProperty, 0, duration, ease));
            sb.Children.Add(CreateDoubleAnimation(_thumbScale, ScaleTransform.ScaleXProperty, 1, duration, ease));
            sb.Children.Add(CreateDoubleAnimation(_thumbScale, ScaleTransform.ScaleYProperty, 1, duration, soft));
            if (_fillScale is not null)
                sb.Children.Add(CreateDoubleAnimation(_fillScale, ScaleTransform.ScaleXProperty, 1, duration, ease));

            _settleStoryboard = sb;
            sb.Completed += OnSettleCompleted;
            sb.Begin();
        }

        private void OnSettleCompleted(object? sender, EventArgs e)
        {
            if (sender is Storyboard sb)
                sb.Completed -= OnSettleCompleted;
            if (ReferenceEquals(_settleStoryboard, sender))
                _settleStoryboard = null;
            ResetTransformsInstant();
        }

        private void StopSettle()
        {
            if (_settleStoryboard is null) return;
            _settleStoryboard.Completed -= OnSettleCompleted;
            _settleStoryboard.Stop();
            _settleStoryboard.Remove();
            _settleStoryboard = null;
        }

        private void ResetTransformsInstant()
        {
            if (_thumbTranslate is not null) _thumbTranslate.X = 0;
            if (_thumbScale is not null)
            {
                _thumbScale.ScaleX = 1;
                _thumbScale.ScaleY = 1;
            }

            if (_fillScale is not null) _fillScale.ScaleX = 1;
            if (_thumb is not null) _thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private double ValueDeltaToPixels(double fromValue, double toValue)
        {
            var range = _slider.Maximum - _slider.Minimum;
            if (range <= 0 || _thumb is null)
                return 0;

            var host = GetTrackHost();
            var usable = Math.Max(1, host.ActualWidth - _thumb.ActualWidth);
            return ((fromValue - toValue) / range) * usable;
        }

        private double ValueToFraction(double value)
        {
            var range = _slider.Maximum - _slider.Minimum;
            if (range <= 0) return 0;
            return Math.Clamp((value - _slider.Minimum) / range, 0, 1);
        }

        private FrameworkElement GetTrackHost()
        {
            var track = FindVisualChild<Track>(_slider);
            if (track is not null && track.ActualWidth > 0)
                return track;
            return _slider;
        }

        private bool IsInteractable() =>
            _slider.IsLoaded
            && _slider.IsVisible
            && _slider.IsEnabled
            && _slider.ActualWidth > 0;

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            DependencyProperty property,
            double to,
            TimeSpan duration,
            IEasingFunction ease)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, new PropertyPath(property));
            return anim;
        }

        private static FrameworkElement? FindDecreaseFill(Slider slider)
        {
            var track = FindVisualChild<Track>(slider);
            var button = track?.DecreaseRepeatButton;
            if (button is null)
                return null;

            // Prefer the templated border so corner radius scales cleanly.
            return FindVisualChild<Border>(button) as FrameworkElement ?? button;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent is null) return null;
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;
                var nested = FindVisualChild<T>(child);
                if (nested is not null)
                    return nested;
            }

            return null;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

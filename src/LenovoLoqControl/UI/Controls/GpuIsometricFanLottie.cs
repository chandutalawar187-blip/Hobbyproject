using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LottieSharp.WPF;
using LottieSharp.WPF.Transforms;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Compact looping playback of the isometric Lottie for hex fan rows,
/// with an amplified vertical hover float.
/// </summary>
public sealed class GpuIsometricFanLottie : Border
{
    private const double ViewWidth = 84;
    private const double ViewHeight = 60;
    private const double HoverAmplitudeBasePx = 7;
    private static readonly TimeSpan HoverPeriod = TimeSpan.FromMilliseconds(1600);
    private static readonly SineEase HoverEase = new() { EasingMode = EasingMode.EaseInOut };

    private readonly LottieAnimationView _view;
    private readonly TranslateTransform _hoverTranslate;
    private bool _hooksWired;
    private bool _playRequested;
    private double _displayScale = 1;
    private double _hoverAmplitudePx = HoverAmplitudeBasePx;

    public GpuIsometricFanLottie()
    {
        _hoverTranslate = new TranslateTransform(0, 0);
        _view = new LottieAnimationView
        {
            Width = ViewWidth,
            Height = ViewHeight,
            AutoPlay = false,
            RepeatCount = -1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AnimationScale = new CenterTransform { ScaleX = 1, ScaleY = 1 },
            RenderTransform = _hoverTranslate,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        ApplyViewSize();
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        ClipToBounds = false;
        ToolTip = "Cooling";
        AutomationProperties.SetName(this, "Isometric fan animation");

        // Prefer a real file path — Skottie loops more reliably than pack:// streams.
        var filePath = ResolveAnimationFilePath();
        if (filePath is not null)
            _view.FileName = filePath;
        else
            _view.ResourcePath = "pack://application:,,,/LoqControl;component/Assets/GpuIsometric.json";

        Child = _view;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public void SetPlaybackEnabled(bool enabled)
    {
        _playRequested = enabled;
        if (!IsLoaded || !IsVisible || !enabled)
        {
            TryStop();
            return;
        }

        TryPlay();
    }

    /// <summary>
    /// Scales the Lottie chrome with the parent hex module density (1 = full size).
    /// </summary>
    public void SetDisplayScale(double scale)
    {
        var clamped = Math.Clamp(scale, 0.55, 1.0);
        if (Math.Abs(clamped - _displayScale) < 0.02)
            return;

        _displayScale = clamped;
        _hoverAmplitudePx = HoverAmplitudeBasePx * clamped;
        ApplyViewSize();

        if (_playRequested && IsLoaded && IsVisible)
            StartHoverFloat();
    }

    private void ApplyViewSize()
    {
        var w = ViewWidth * _displayScale;
        var h = ViewHeight * _displayScale;
        Width = w;
        Height = h + _hoverAmplitudePx * 2;
        Margin = new Thickness(0, 0, Math.Max(4, 8 * _displayScale), 0);
        if (_view is not null)
        {
            _view.Width = w;
            _view.Height = h;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooksWired)
        {
            AppUiPreferences.Changed += OnPreferencesChanged;
            _hooksWired = true;
        }

        // Defer so LottieSharp finishes decoding before PlayAnimation.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_playRequested)
                SyncPlayback();
            else
                SyncPlayback(forcePlayIfAllowed: true);
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooksWired)
        {
            AppUiPreferences.Changed -= OnPreferencesChanged;
            _hooksWired = false;
        }

        TryStop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncPlayback();

    private void OnPreferencesChanged() =>
        Dispatcher.BeginInvoke(SyncPlayback);

    private void SyncPlayback(bool forcePlayIfAllowed = false)
    {
        var allow = IsLoaded
            && IsVisible
            && !AppUiPreferences.ReducedMotion
            && UiAnimation.ShouldAnimate
            && (_playRequested || forcePlayIfAllowed);

        if (allow)
        {
            _playRequested = true;
            TryPlay();
        }
        else
        {
            TryStop();
        }
    }

    private void TryPlay()
    {
        try
        {
            if (!_view.IsPlaying)
                _view.PlayAnimation();
            StartHoverFloat();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            // Keep telemetry UI alive if the Lottie asset fails to decode.
            Visibility = Visibility.Collapsed;
        }
    }

    private void TryStop()
    {
        try
        {
            if (_view.IsPlaying)
                _view.StopAnimation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Ignore stop failures during teardown.
        }

        StopHoverFloat();
    }

    private void StartHoverFloat()
    {
        if (AppUiPreferences.ReducedMotion || !UiAnimation.ShouldAnimate)
        {
            StopHoverFloat();
            return;
        }

        _hoverTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = _hoverAmplitudePx,
            To = -_hoverAmplitudePx,
            Duration = HoverPeriod,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = HoverEase
        });
    }

    private void StopHoverFloat()
    {
        _hoverTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _hoverTranslate.Y = 0;
    }

    private static string? ResolveAnimationFilePath()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Assets", "GpuIsometric.json"),
                Path.Combine(baseDir, "GpuIsometric.json")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to pack URI.
        }

        return null;
    }
}

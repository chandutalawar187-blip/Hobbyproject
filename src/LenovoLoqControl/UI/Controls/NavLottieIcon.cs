using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LottieSharp.WPF;
using LottieSharp.WPF.Transforms;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Compact looping nav icon. Supports Lottie JSON (vector) and pre-rendered GIF
/// (for image-based Lotties that Skottie cannot decode).
/// </summary>
public sealed class NavLottieIcon : Border
{
    public static readonly DependencyProperty AssetFileNameProperty =
        DependencyProperty.Register(
            nameof(AssetFileName),
            typeof(string),
            typeof(NavLottieIcon),
            new PropertyMetadata(string.Empty, OnAssetFileNameChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(NavLottieIcon),
            new PropertyMetadata(28d, OnLayoutMetricsChanged));

    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(NavLottieIcon),
            new PropertyMetadata(1d, OnLayoutMetricsChanged));

    public static readonly DependencyProperty ZoomScaleProperty =
        DependencyProperty.Register(
            nameof(ZoomScale),
            typeof(double),
            typeof(NavLottieIcon),
            new PropertyMetadata(1d, OnZoomScaleChanged));

    public static readonly DependencyProperty LabelVisibleProperty =
        DependencyProperty.Register(
            nameof(LabelVisible),
            typeof(bool),
            typeof(NavLottieIcon),
            new PropertyMetadata(true, OnLabelVisibleChanged));

    private readonly LottieAnimationView _lottie;
    private readonly Image _gifImage;
    private BitmapDecoder? _gifDecoder;
    private DispatcherTimer? _gifTimer;
    private int _gifFrameIndex;
    private bool _hooksWired;
    private bool _assetBound;
    private bool _useGif;

    public NavLottieIcon()
    {
        _lottie = new LottieAnimationView
        {
            AutoPlay = false,
            RepeatCount = -1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AnimationScale = new CenterTransform { ScaleX = 1, ScaleY = 1 }
        };
        _gifImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(_gifImage, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ApplyLayoutSize();
        ApplyLabelSpacing(LabelVisible);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        ClipToBounds = false;
        Child = _lottie;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string AssetFileName
    {
        get => (string)GetValue(AssetFileNameProperty);
        set => SetValue(AssetFileNameProperty, value);
    }

    /// <summary>Icon height in DIPs. Width is <see cref="IconSize"/> × <see cref="AspectRatio"/>.</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Width/height ratio. Use ~1.4 for the wide GPU isometric asset.</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>Lottie center zoom (&gt;1 crops empty composition padding).</summary>
    public double ZoomScale
    {
        get => (double)GetValue(ZoomScaleProperty);
        set => SetValue(ZoomScaleProperty, value);
    }

    public bool LabelVisible
    {
        get => (bool)GetValue(LabelVisibleProperty);
        set => SetValue(LabelVisibleProperty, value);
    }

    private static void OnAssetFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavLottieIcon icon)
        {
            icon._assetBound = false;
            icon.BindAsset();
            icon.SyncPlayback();
        }
    }

    private static void OnLabelVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavLottieIcon icon && e.NewValue is bool visible)
            icon.ApplyLabelSpacing(visible);
    }

    private void ApplyLabelSpacing(bool labelVisible) =>
        Margin = new Thickness(0, 0, labelVisible ? 12 : 0, 0);

    private static void OnLayoutMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavLottieIcon icon)
            icon.ApplyLayoutSize();
    }

    private static void OnZoomScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NavLottieIcon icon || e.NewValue is not double zoom)
            return;

        zoom = Math.Clamp(zoom, 0.5, 2.5);
        var z = (float)zoom;
        icon._lottie.AnimationScale = new CenterTransform { ScaleX = z, ScaleY = z };
    }

    private void ApplyLayoutSize()
    {
        var height = Math.Clamp(IconSize, 16, 72);
        var ratio = Math.Clamp(AspectRatio, 0.6, 2.0);
        var width = Math.Clamp(height * ratio, 16, 96);

        Width = width;
        Height = height;
        _lottie.Width = width;
        _lottie.Height = height;
        _gifImage.Width = width;
        _gifImage.Height = height;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooksWired)
        {
            AppUiPreferences.Changed += OnPreferencesChanged;
            _hooksWired = true;
        }

        BindAsset();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncPlayback);
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

    private void BindAsset()
    {
        if (_assetBound || string.IsNullOrWhiteSpace(AssetFileName))
            return;

        var fileName = AssetFileName;
        _useGif = fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (_useGif)
        {
            BindGif(fileName);
            return;
        }

        Child = _lottie;
        var filePath = ResolveAssetPath(fileName);
        if (filePath is not null)
        {
            _lottie.FileName = filePath;
            _assetBound = true;
            return;
        }

        var packName = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".json";
        _lottie.ResourcePath = $"pack://application:,,,/LoqControl;component/Assets/{packName}";
        _assetBound = true;
    }

    private void BindGif(string fileName)
    {
        try
        {
            var path = ResolveAssetPath(fileName);
            if (path is null)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            _gifDecoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (_gifDecoder.Frames.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            _gifFrameIndex = 0;
            _gifImage.Source = _gifDecoder.Frames[0];
            Child = _gifImage;
            _assetBound = true;
            Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Visibility = Visibility.Collapsed;
        }
    }

    private void SyncPlayback()
    {
        var allow = IsLoaded
            && IsVisible
            && !AppUiPreferences.ReducedMotion
            && UiAnimation.ShouldAnimate
            && _assetBound;

        if (allow)
            TryPlay();
        else
            TryStop();
    }

    private void TryPlay()
    {
        if (_useGif)
        {
            TryPlayGif();
            return;
        }

        try
        {
            if (!_lottie.IsPlaying)
                _lottie.PlayAnimation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            Visibility = Visibility.Collapsed;
        }
    }

    private void TryPlayGif()
    {
        if (_gifDecoder is null || _gifDecoder.Frames.Count <= 1)
            return;

        _gifTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _gifTimer.Tick -= OnGifTick;
        _gifTimer.Tick += OnGifTick;
        if (!_gifTimer.IsEnabled)
            _gifTimer.Start();
    }

    private void OnGifTick(object? sender, EventArgs e)
    {
        if (_gifDecoder is null || _gifDecoder.Frames.Count == 0)
            return;

        _gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
        _gifImage.Source = _gifDecoder.Frames[_gifFrameIndex];
    }

    private void TryStop()
    {
        if (_useGif)
        {
            if (_gifTimer is not null)
            {
                _gifTimer.Stop();
                _gifTimer.Tick -= OnGifTick;
            }

            if (_gifDecoder is not null && _gifDecoder.Frames.Count > 0)
            {
                _gifFrameIndex = 0;
                _gifImage.Source = _gifDecoder.Frames[0];
            }

            return;
        }

        try
        {
            if (_lottie.IsPlaying)
                _lottie.StopAnimation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Ignore stop failures during teardown.
        }
    }

    private static string? ResolveAssetPath(string assetFileName)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Assets", assetFileName),
                Path.Combine(baseDir, assetFileName)
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to pack URI / collapse.
        }

        return null;
    }
}

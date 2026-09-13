using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LottieSharp.WPF;
using LottieSharp.WPF.Transforms;
using LenovoLoqControl.UI;

namespace LenovoLoqControl.UI.Controls;

/// <summary>
/// Compact looping Lottie used as a navigation rail icon.
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
            new PropertyMetadata(22d, OnIconSizeChanged));

    private readonly LottieAnimationView _view;
    private bool _hooksWired;
    private bool _assetBound;

    public NavLottieIcon()
    {
        _view = new LottieAnimationView
        {
            AutoPlay = false,
            RepeatCount = -1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AnimationScale = new CenterTransform { ScaleX = 1, ScaleY = 1 }
        };

        Width = IconSize;
        Height = IconSize;
        _view.Width = IconSize;
        _view.Height = IconSize;
        Margin = new Thickness(0, 0, 10, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Child = _view;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string AssetFileName
    {
        get => (string)GetValue(AssetFileNameProperty);
        set => SetValue(AssetFileNameProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
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

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NavLottieIcon icon || e.NewValue is not double size)
            return;

        size = Math.Clamp(size, 14, 40);
        icon.Width = size;
        icon.Height = size;
        icon._view.Width = size;
        icon._view.Height = size;
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

        var filePath = ResolveAnimationFilePath(AssetFileName);
        if (filePath is not null)
        {
            _view.FileName = filePath;
            _assetBound = true;
            return;
        }

        var packName = AssetFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? AssetFileName
            : AssetFileName + ".json";
        _view.ResourcePath = $"pack://application:,,,/LoqControl;component/Assets/{packName}";
        _assetBound = true;
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
        try
        {
            if (!_view.IsPlaying)
                _view.PlayAnimation();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException or IOException)
        {
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
    }

    private static string? ResolveAnimationFilePath(string assetFileName)
    {
        try
        {
            var fileName = assetFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? assetFileName
                : assetFileName + ".json";
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Assets", fileName),
                Path.Combine(baseDir, fileName)
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLoqControl.Core;
using LenovoLoqControl.Hardware;

namespace LenovoLoqControl.UI;

public partial class ProfilesView : UserControl
{
    private readonly IHardwareBackend _hardware;
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private string? _activeProfile;

    public ProfilesView(IHardwareBackend? hardware = null)
    {
        InitializeComponent();
        _hardware = hardware ?? new HardwareBackend();
        UnsupportedDeviceBanner.Visibility = _hardware.Monitor.Identity.IsLenovoLoq
            ? Visibility.Collapsed
            : Visibility.Visible;
        BuildCards();
    }

    private void BuildCards()
    {
        ProfileGrid.Children.Clear();
        _cards.Clear();

        AddCard("Quiet", "Browsing, documents, and video playback",
            "Maps to Silent firmware mode", "Lower fan aggression when the firmware allows it", FanMode.Quiet);
        AddCard("Balanced", "Everyday workloads",
            "Maps to Automatic firmware mode", "Firmware manages cooling automatically", FanMode.Auto);
        AddCard("Performance", "Gaming, compilation, and rendering",
            "Maps to Performance firmware mode", "May require AC power on supported systems", FanMode.Performance);
        AddCard("Custom", "User-defined curve when supported",
            "Uses the Fan Control curve editor", "Open Fan Control to edit and apply a custom table", mode: null);
    }

    private void AddCard(string name, string description, string mapping, string fanBehavior, FanMode? mode)
    {
        var card = new Border { Style = (Style)FindResource("Card.Metric"), Margin = new Thickness(0, 0, 14, 14) };
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("Text.Title") });
        root.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("Text.BodySecondary"),
            Margin = new Thickness(0, 6, 0, 12)
        });

        root.Children.Add(MetaRow("Mode mapping", mapping));
        root.Children.Add(MetaRow("Fan behavior", fanBehavior));

        var availability = _hardware.FanController.IsSupported
            ? (mode is null ? "Custom table depends on Fan Control page" : "Firmware command available")
            : _hardware.FanController.AvailabilityMessage;
        root.Children.Add(MetaRow("Hardware", availability));

        var active = new TextBlock
        {
            Name = "ActiveLabel",
            Text = "Inactive",
            Style = (Style)FindResource("Text.Caption"),
            Margin = new Thickness(0, 14, 0, 10),
            Tag = "active"
        };
        root.Children.Add(active);

        var apply = new Button
        {
            Content = mode is null ? "Open Fan Control guidance" : "Apply profile",
            Style = (Style)FindResource(mode is null ? "Button.Ghost" : "Button.Primary"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),
            IsEnabled = _hardware.Monitor.Identity.IsLenovoLoq &&
                (mode is null || _hardware.FanController.IsSupported),
            ToolTip = mode is null
                ? "Custom profiles are applied from Fan Control after editing the curve."
                : _hardware.FanController.AvailabilityMessage
        };
        apply.Click += async (_, _) =>
        {
            if (mode is null)
            {
                ShowResult("Use Fan Control to edit and apply a custom curve. This page will not claim Custom is active until firmware accepts a curve.", accepted: null);
                return;
            }

            apply.IsEnabled = false;
            ShowResult($"Applying {name}…", accepted: null);
            var result = await _hardware.FanController.SetFanModeAsync(mode.Value, CancellationToken.None);
            ShowResult(result.Message, result.Accepted);
            if (result.Accepted)
                SetActive(name);
            apply.IsEnabled = _hardware.Monitor.Identity.IsLenovoLoq &&
                _hardware.FanController.IsSupported;
        };
        root.Children.Add(apply);

        card.Child = root;
        _cards[name] = card;
        ProfileGrid.Children.Add(card);
        var index = ProfileGrid.Children.Count - 1;
        card.Loaded += (_, _) => UiAnimation.PlayEnter(card, index);
    }

    private static TextBlock MetaRow(string label, string value) =>
        new()
        {
            Text = $"{label}  ·  {value}",
            Style = (Style)Application.Current.FindResource("Text.BodySecondary"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap
        };

    private void SetActive(string name)
    {
        _activeProfile = name;
        foreach (var (key, card) in _cards)
        {
            if (card.Child is not StackPanel stack) continue;
            var label = stack.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "active"));
            if (label is null) continue;
            var isActive = key == name;
            label.Text = isActive ? "Active · firmware accepted" : "Inactive";
            label.Foreground = (Brush)FindResource(isActive ? "Brush.Success" : "Brush.TextSecondary");
            card.BorderBrush = (Brush)FindResource(isActive ? "Brush.Accent" : "Brush.BorderSubtle");
        }
    }

    private void ShowResult(string message, bool? accepted)
    {
        ResultBanner.Visibility = Visibility.Visible;
        ResultText.Text = message;
        if (accepted is true)
            ResultBanner.Style = (Style)FindResource("Badge.Success");
        else if (accepted is false)
            ResultBanner.Style = (Style)FindResource("Badge.Warning");
        else
            ResultBanner.Style = (Style)FindResource("Badge.Neutral");
    }
}

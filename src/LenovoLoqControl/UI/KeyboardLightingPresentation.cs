using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI;

public enum KeyboardLightingStatusKind
{
    Neutral,
    Success,
    Warning,
    Error
}

public enum KeyboardLightingUiMode
{
    Unsupported,
    WhiteBacklit,
    FourZoneRgb,
    TwentyFourZoneRgbComingLater,
    RgbLayoutUnknown
}

/// <summary>Immutable snapshot of keyboard-lighting UI state (no hardware I/O).</summary>
public sealed record KeyboardLightingState(
    KeyboardZoneType DetectedZoneType,
    string ZoneDescription,
    string AvailabilityMessage,
    bool IsSupported,
    KeyboardLightingUiMode UiMode,
    bool IsLoading,
    bool IsApplying,
    KeyboardLightLevel? CurrentLevel,
    KeyboardLightLevel? SelectedLevel,
    string StatusMessage,
    KeyboardLightingStatusKind StatusKind)
{
    public bool IsBusy => IsLoading || IsApplying;

    public bool ShowWhiteControls => UiMode == KeyboardLightingUiMode.WhiteBacklit;

    public bool ShowRgbComingSoon =>
        UiMode is KeyboardLightingUiMode.FourZoneRgb
            or KeyboardLightingUiMode.TwentyFourZoneRgbComingLater
            or KeyboardLightingUiMode.RgbLayoutUnknown;

    public bool ControlsEnabled => ShowWhiteControls && !IsBusy;

    public string TypeBadgeText => KeyboardLightingPresentation.TypeBadgeLabel(DetectedZoneType, IsSupported);

    public string Headline => KeyboardLightingPresentation.HeadlineFor(UiMode);

    public string DetailBody => KeyboardLightingPresentation.DetailFor(UiMode, AvailabilityMessage, ZoneDescription);

    public string CurrentLevelLabel => CurrentLevel is KeyboardLightLevel level
        ? $"Current level · {level}"
        : "Current level · Unknown";

    /// <summary>Last RGB settings confirmed by a successful HID transmission.</summary>
    public KeyboardRgbSettings? LastConfirmedRgbSettings { get; init; }
}

/// <summary>Pure presentation helpers for keyboard lighting capability states.</summary>
public static class KeyboardLightingPresentation
{
    public static KeyboardLightingUiMode ResolveUiMode(KeyboardZoneType zoneType, bool isSupported)
    {
        if (!isSupported || zoneType == KeyboardZoneType.Unsupported)
            return KeyboardLightingUiMode.Unsupported;

        return zoneType switch
        {
            KeyboardZoneType.WhiteBacklit => KeyboardLightingUiMode.WhiteBacklit,
            KeyboardZoneType.FourZoneRgb => KeyboardLightingUiMode.FourZoneRgb,
            KeyboardZoneType.TwentyFourZoneRgb => KeyboardLightingUiMode.TwentyFourZoneRgbComingLater,
            KeyboardZoneType.RgbLayoutUnknown => KeyboardLightingUiMode.RgbLayoutUnknown,
            _ => KeyboardLightingUiMode.Unsupported
        };
    }

    public static string TypeBadgeLabel(KeyboardZoneType zoneType, bool isSupported)
    {
        if (!isSupported || zoneType == KeyboardZoneType.Unsupported)
            return "Keyboard lighting unsupported";

        return zoneType switch
        {
            KeyboardZoneType.WhiteBacklit => "White backlit keyboard",
            KeyboardZoneType.FourZoneRgb => "4-zone RGB keyboard",
            KeyboardZoneType.TwentyFourZoneRgb => "24-zone RGB keyboard",
            KeyboardZoneType.RgbLayoutUnknown => "RGB keyboard — layout unknown",
            _ => "Keyboard lighting unsupported"
        };
    }

    public static string HeadlineFor(KeyboardLightingUiMode mode) => mode switch
    {
        KeyboardLightingUiMode.WhiteBacklit => "Keyboard Lighting",
        KeyboardLightingUiMode.FourZoneRgb => "4-zone RGB keyboard detected",
        KeyboardLightingUiMode.TwentyFourZoneRgbComingLater => "24-zone RGB keyboard detected",
        KeyboardLightingUiMode.RgbLayoutUnknown => "RGB keyboard detected",
        _ => "Keyboard lighting unavailable"
    };

    public static string DetailFor(
        KeyboardLightingUiMode mode,
        string availabilityMessage,
        string zoneDescription) => mode switch
    {
        KeyboardLightingUiMode.WhiteBacklit =>
            string.IsNullOrWhiteSpace(availabilityMessage) ? zoneDescription : availabilityMessage,
        KeyboardLightingUiMode.FourZoneRgb =>
            "Apply verified 4-zone RGB effects to the keyboard.",
        KeyboardLightingUiMode.TwentyFourZoneRgbComingLater =>
            "RGB controls are detected but not implemented yet.",
        KeyboardLightingUiMode.RgbLayoutUnknown =>
            "Zone layout unavailable. The application will not guess the layout.",
        _ => string.IsNullOrWhiteSpace(availabilityMessage) ? zoneDescription : availabilityMessage
    };

    public static string RgbInfoPanelText(KeyboardLightingUiMode mode) => mode switch
    {
        KeyboardLightingUiMode.FourZoneRgb =>
            "Only the verified Lenovo LOQ 4-zone HID path can receive RGB commands.",
        KeyboardLightingUiMode.TwentyFourZoneRgbComingLater =>
            "Per-zone RGB control requires a separate verified HID implementation.",
        KeyboardLightingUiMode.RgbLayoutUnknown =>
            "RGB lighting was detected, but the zone map is not exposed. Controls stay hidden until a verified layout is available.",
        _ => string.Empty
    };

    public static KeyboardLightingState CreateInitial(IKeyboardLightController controller)
    {
        var mode = ResolveUiMode(controller.ZoneType, controller.IsSupported);
        return new KeyboardLightingState(
            DetectedZoneType: controller.ZoneType,
            ZoneDescription: controller.ZoneDescription,
            AvailabilityMessage: controller.AvailabilityMessage,
            IsSupported: controller.IsSupported,
            UiMode: mode,
            IsLoading: false,
            IsApplying: false,
            CurrentLevel: null,
            SelectedLevel: null,
            StatusMessage: mode == KeyboardLightingUiMode.WhiteBacklit
                ? "Ready. No lighting command has been sent yet."
                : DetailFor(mode, controller.AvailabilityMessage, controller.ZoneDescription),
            StatusKind: mode == KeyboardLightingUiMode.Unsupported
                ? KeyboardLightingStatusKind.Warning
                : KeyboardLightingStatusKind.Neutral);
    }
}

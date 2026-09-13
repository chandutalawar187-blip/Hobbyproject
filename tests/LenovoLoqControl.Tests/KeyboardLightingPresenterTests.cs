using LenovoLoqControl.Core;
using LenovoLoqControl.UI;
using Xunit;

namespace LenovoLoqControl.Tests;

public class KeyboardLightingPresenterTests
{
    [Theory]
    [InlineData(KeyboardZoneType.WhiteBacklit, true, KeyboardLightingUiMode.WhiteBacklit, true)]
    [InlineData(KeyboardZoneType.FourZoneRgb, true, KeyboardLightingUiMode.FourZoneRgb, false)]
    [InlineData(KeyboardZoneType.TwentyFourZoneRgb, true, KeyboardLightingUiMode.TwentyFourZoneRgbComingLater, false)]
    [InlineData(KeyboardZoneType.RgbLayoutUnknown, true, KeyboardLightingUiMode.RgbLayoutUnknown, false)]
    [InlineData(KeyboardZoneType.Unsupported, false, KeyboardLightingUiMode.Unsupported, false)]
    [InlineData(KeyboardZoneType.WhiteBacklit, false, KeyboardLightingUiMode.Unsupported, false)]
    public void ResolveUiMode_MapsCapabilities(
        KeyboardZoneType zone,
        bool supported,
        KeyboardLightingUiMode expectedMode,
        bool expectWhiteControls)
    {
        var mode = KeyboardLightingPresentation.ResolveUiMode(zone, supported);
        Assert.Equal(expectedMode, mode);

        var fake = new FakeKeyboardLightController
        {
            IsSupported = supported,
            ZoneType = zone,
            ZoneDescription = "desc",
            AvailabilityMessage = "avail"
        };
        var state = KeyboardLightingPresentation.CreateInitial(fake);
        Assert.Equal(expectedMode, state.UiMode);
        Assert.Equal(expectWhiteControls, state.ShowWhiteControls);
        Assert.Equal(!expectWhiteControls && expectedMode != KeyboardLightingUiMode.Unsupported,
            state.ShowRgbComingSoon);
    }

    [Fact]
    public async Task WhiteBacklit_Refresh_LoadsCurrentLevel()
    {
        var fake = FakeKeyboardLightController.White(KeyboardLightLevel.Low);
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, fake.GetCount);
        Assert.Equal(0, fake.SetCount);
        Assert.Equal(KeyboardLightLevel.Low, presenter.State.CurrentLevel);
        Assert.Equal(KeyboardLightLevel.Low, presenter.State.SelectedLevel);
        Assert.True(presenter.State.ShowWhiteControls);
        Assert.False(presenter.State.IsBusy);
    }

    [Fact]
    public async Task FourZoneRgb_Refresh_DoesNotCallWhiteLevelRead()
    {
        var fake = FakeKeyboardLightController.Rgb(KeyboardZoneType.FourZoneRgb);
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, fake.GetCount);
        Assert.Equal(0, fake.SetCount);
        Assert.Equal(KeyboardLightingUiMode.FourZoneRgb, presenter.State.UiMode);
        Assert.False(presenter.State.ShowWhiteControls);
        Assert.True(presenter.State.ShowRgbComingSoon);
        Assert.Contains("Apply verified", presenter.State.DetailBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FourZoneRgb_RefreshLoadsCurrentDeviceState()
    {
        var settings = new KeyboardRgbSettings(
            KeyboardRgbEffect.ColorCycle,
            new KeyboardRgbColor(12, 34, 56),
            7);
        var fake = FakeKeyboardLightController.Rgb(KeyboardZoneType.FourZoneRgb);
        fake.CurrentRgb = settings;
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(settings, presenter.State.LastConfirmedRgbSettings);
        Assert.Equal(1, fake.RgbGetCount);
    }

    [Fact]
    public async Task TwentyFourZoneRgb_ShowsComingLaterWithoutControls()
    {
        var fake = FakeKeyboardLightController.Rgb(KeyboardZoneType.TwentyFourZoneRgb);
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(KeyboardLightingUiMode.TwentyFourZoneRgbComingLater, presenter.State.UiMode);
        Assert.False(presenter.State.ShowWhiteControls);
        Assert.Equal(0, fake.SetCount);
    }

    [Fact]
    public async Task RgbLayoutUnknown_ShowsNoControls()
    {
        var fake = FakeKeyboardLightController.Rgb(KeyboardZoneType.RgbLayoutUnknown);
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(KeyboardLightingUiMode.RgbLayoutUnknown, presenter.State.UiMode);
        Assert.False(presenter.State.ShowWhiteControls);
        Assert.Contains("will not guess", presenter.State.DetailBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_ShowsUnavailableState()
    {
        var fake = new FakeKeyboardLightController
        {
            IsSupported = false,
            ZoneType = KeyboardZoneType.Unsupported,
            ZoneDescription = "No supported keyboard lighting detected.",
            AvailabilityMessage = "No supported keyboard lighting detected."
        };
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.RefreshAsync(CancellationToken.None);

        Assert.Equal(KeyboardLightingUiMode.Unsupported, presenter.State.UiMode);
        Assert.False(presenter.State.ShowWhiteControls);
        Assert.Equal(KeyboardLightingStatusKind.Warning, presenter.State.StatusKind);
        Assert.Equal(0, fake.GetCount);
        Assert.Equal(0, fake.SetCount);
    }

    [Fact]
    public async Task SuccessfulSetLevel_UpdatesSelection()
    {
        var fake = FakeKeyboardLightController.White(KeyboardLightLevel.Off);
        var presenter = new KeyboardLightingPresenter(fake);
        await presenter.RefreshAsync(CancellationToken.None);

        await presenter.SelectLevelAsync(KeyboardLightLevel.High, CancellationToken.None);

        Assert.Equal(1, fake.SetCount);
        Assert.Equal(KeyboardLightLevel.High, fake.Current);
        Assert.Equal(KeyboardLightLevel.High, presenter.State.CurrentLevel);
        Assert.Equal(KeyboardLightLevel.High, presenter.State.SelectedLevel);
        Assert.Equal(KeyboardLightingStatusKind.Success, presenter.State.StatusKind);
        Assert.Contains("High", presenter.State.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedSetLevel_RestoresPreviousSelectionAndShowsError()
    {
        var fake = FakeKeyboardLightController.White(KeyboardLightLevel.Low);
        fake.SetResult = new FanControlResult(false, "Firmware rejected keyboard lighting change.");
        var presenter = new KeyboardLightingPresenter(fake);
        await presenter.RefreshAsync(CancellationToken.None);

        await presenter.SelectLevelAsync(KeyboardLightLevel.High, CancellationToken.None);

        Assert.Equal(KeyboardLightLevel.Low, presenter.State.CurrentLevel);
        Assert.Equal(KeyboardLightLevel.Low, presenter.State.SelectedLevel);
        Assert.Equal(KeyboardLightingStatusKind.Error, presenter.State.StatusKind);
        Assert.Equal("Firmware rejected keyboard lighting change.", presenter.State.StatusMessage);
        Assert.Equal(KeyboardLightLevel.Low, fake.Current);
    }

    [Fact]
    public async Task Cancellation_DoesNotApplyStaleSuccess()
    {
        var fake = FakeKeyboardLightController.White(KeyboardLightLevel.Off);
        fake.SetDelay = TimeSpan.FromMilliseconds(200);
        var presenter = new KeyboardLightingPresenter(fake);
        await presenter.RefreshAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var apply = presenter.SelectLevelAsync(KeyboardLightLevel.High, cts.Token);
        cts.Cancel();
        await apply;

        Assert.Equal(KeyboardLightLevel.Off, presenter.State.SelectedLevel);
        Assert.NotEqual(KeyboardLightingStatusKind.Success, presenter.State.StatusKind);
    }

    [Fact]
    public async Task DuplicateApply_IsIgnoredWhileBusy()
    {
        var fake = FakeKeyboardLightController.White(KeyboardLightLevel.Off);
        fake.SetDelay = TimeSpan.FromMilliseconds(150);
        var presenter = new KeyboardLightingPresenter(fake);
        await presenter.RefreshAsync(CancellationToken.None);

        var first = presenter.SelectLevelAsync(KeyboardLightLevel.Low, CancellationToken.None);
        var second = presenter.SelectLevelAsync(KeyboardLightLevel.High, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, fake.SetCount);
        Assert.Equal(KeyboardLightLevel.Low, fake.Current);
    }

    [Fact]
    public async Task RgbSelectLevel_DoesNotCallBackend()
    {
        var fake = FakeKeyboardLightController.Rgb(KeyboardZoneType.FourZoneRgb);
        var presenter = new KeyboardLightingPresenter(fake);

        await presenter.SelectLevelAsync(KeyboardLightLevel.High, CancellationToken.None);

        Assert.Equal(0, fake.SetCount);
        Assert.False(presenter.State.ShowWhiteControls);
    }

    [Fact]
    public void RgbSettings_RejectsUnknownEffectAndOutOfRangeSpeed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyboardRgbSettings((KeyboardRgbEffect)99, KeyboardRgbColor.White, 3).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyboardRgbSettings(KeyboardRgbEffect.Static, KeyboardRgbColor.White, 11).Validate());
    }
}

internal sealed class FakeKeyboardLightController : IKeyboardLightController
{
    public bool IsSupported { get; set; }
    public KeyboardZoneType ZoneType { get; set; }
    public string ZoneDescription { get; set; } = "";
    public string AvailabilityMessage { get; set; } = "";
    public KeyboardLightLevel? Current { get; set; }
    public FanControlResult SetResult { get; set; } = new(true, "Keyboard lighting set to High.");
    public TimeSpan GetDelay { get; set; }
    public TimeSpan SetDelay { get; set; }
    public int GetCount { get; private set; }
    public int SetCount { get; private set; }
    public int RgbGetCount { get; private set; }
    public KeyboardRgbSettings? CurrentRgb { get; set; }

    public static FakeKeyboardLightController White(KeyboardLightLevel level) => new()
    {
        IsSupported = true,
        ZoneType = KeyboardZoneType.WhiteBacklit,
        ZoneDescription = "White backlit keyboard detected.",
        AvailabilityMessage = "Verified Lenovo keyboard lighting control is available.",
        Current = level,
        SetResult = new FanControlResult(true, $"Keyboard lighting set to {level}.")
    };

    public static FakeKeyboardLightController Rgb(KeyboardZoneType zone) => new()
    {
        IsSupported = true,
        ZoneType = zone,
        ZoneDescription = zone switch
        {
            KeyboardZoneType.FourZoneRgb => "4-zone RGB keyboard detected.",
            KeyboardZoneType.TwentyFourZoneRgb => "24-zone RGB keyboard detected.",
            _ => "RGB keyboard detected; zone layout is not exposed by this firmware."
        },
        AvailabilityMessage = "RGB keyboard detected."
    };

    public async Task<KeyboardLightLevel?> GetCurrentLevelAsync(CancellationToken cancellationToken)
    {
        GetCount++;
        if (GetDelay > TimeSpan.Zero)
            await Task.Delay(GetDelay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Current;
    }

    public Task<KeyboardRgbSettings?> GetCurrentRgbSettingsAsync(CancellationToken cancellationToken)
    {
        RgbGetCount++;
        return Task.FromResult(CurrentRgb);
    }

    public async Task<FanControlResult> SetLevelAsync(KeyboardLightLevel level, CancellationToken cancellationToken)
    {
        SetCount++;
        if (SetDelay > TimeSpan.Zero)
            await Task.Delay(SetDelay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SetResult.Accepted)
            return SetResult;
        Current = level;
        return new FanControlResult(true, $"Keyboard lighting set to {level}.");
    }

    public Task<FanControlResult> SetRgbEffectAsync(KeyboardRgbSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(FanControlResult.Unsupported("RGB test backend"));

    public void Dispose()
    {
    }
}

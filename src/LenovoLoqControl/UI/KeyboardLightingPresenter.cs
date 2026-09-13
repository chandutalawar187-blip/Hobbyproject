using System.Management;
using LenovoLoqControl.Core;

namespace LenovoLoqControl.UI;

/// <summary>
/// Orchestrates keyboard-lighting UI state against <see cref="IKeyboardLightController"/>.
/// No raw hardware commands — only the shared backend API.
/// </summary>
public sealed class KeyboardLightingPresenter
{
    private readonly IKeyboardLightController _controller;
    private readonly object _gate = new();
    private KeyboardLightingState _state;
    private bool _operationInFlight;

    public KeyboardLightingPresenter(IKeyboardLightController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _state = KeyboardLightingPresentation.CreateInitial(controller);
    }

    public event Action? StateChanged;

    public KeyboardLightingState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public void SyncFromBackendMetadata()
    {
        var previous = State;
        var mode = KeyboardLightingPresentation.ResolveUiMode(_controller.ZoneType, _controller.IsSupported);
        Replace(previous with
        {
            DetectedZoneType = _controller.ZoneType,
            ZoneDescription = _controller.ZoneDescription,
            AvailabilityMessage = _controller.AvailabilityMessage,
            IsSupported = _controller.IsSupported,
            UiMode = mode,
            StatusMessage = mode == KeyboardLightingUiMode.WhiteBacklit
                ? previous.StatusMessage
                : KeyboardLightingPresentation.DetailFor(mode, _controller.AvailabilityMessage, _controller.ZoneDescription),
            StatusKind = mode == KeyboardLightingUiMode.Unsupported
                ? KeyboardLightingStatusKind.Warning
                : previous.StatusKind == KeyboardLightingStatusKind.Error
                    ? previous.StatusKind
                    : KeyboardLightingStatusKind.Neutral
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
            return;

        var snapshot = State;
        try
        {
            SyncFromBackendMetadata();
            snapshot = State;

            if (snapshot.UiMode != KeyboardLightingUiMode.WhiteBacklit)
            {
                if (snapshot.UiMode == KeyboardLightingUiMode.FourZoneRgb)
                {
                    var rgb = await _controller.GetCurrentRgbSettingsAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Replace(State with
                    {
                        IsLoading = false,
                        IsApplying = false,
                        LastConfirmedRgbSettings = rgb,
                        StatusMessage = rgb is null
                            ? "Keyboard state readback is unavailable."
                            : $"Synchronized with keyboard: {rgb.Effect}.",
                        StatusKind = rgb is null
                            ? KeyboardLightingStatusKind.Warning
                            : KeyboardLightingStatusKind.Success
                    });
                    return;
                }

                Replace(State with
                {
                    IsLoading = false,
                    IsApplying = false,
                    StatusMessage = KeyboardLightingPresentation.DetailFor(
                        State.UiMode, State.AvailabilityMessage, State.ZoneDescription),
                    StatusKind = State.UiMode == KeyboardLightingUiMode.Unsupported
                        ? KeyboardLightingStatusKind.Warning
                        : KeyboardLightingStatusKind.Neutral
                });
                return;
            }

            Replace(State with
            {
                IsLoading = true,
                IsApplying = false,
                StatusMessage = "Reading keyboard lighting…",
                StatusKind = KeyboardLightingStatusKind.Neutral
            });

            var level = await _controller.GetCurrentLevelAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Replace(State with
            {
                IsLoading = false,
                IsApplying = false,
                CurrentLevel = level,
                SelectedLevel = level,
                StatusMessage = level is KeyboardLightLevel known
                    ? $"Current level · {known}"
                    : "Firmware did not report a keyboard lighting level.",
                StatusKind = level is null
                    ? KeyboardLightingStatusKind.Warning
                    : KeyboardLightingStatusKind.Neutral
            });
        }
        catch (OperationCanceledException)
        {
            Replace(State with
            {
                IsLoading = false,
                IsApplying = false
            });
        }
        catch (Exception ex) when (ex is TimeoutException
                                   or UnauthorizedAccessException
                                   or ManagementException
                                   or InvalidOperationException
                                   or System.IO.IOException)
        {
            Replace(State with
            {
                IsLoading = false,
                IsApplying = false,
                StatusMessage = ex.Message,
                StatusKind = KeyboardLightingStatusKind.Error
            });
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task SelectLevelAsync(KeyboardLightLevel level, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
            return;

        var before = State;
        if (before.UiMode != KeyboardLightingUiMode.WhiteBacklit)
        {
            EndOperation();
            return;
        }

        if (before.SelectedLevel == level && before.CurrentLevel == level && !before.IsBusy)
        {
            EndOperation();
            return;
        }

        var previousSelection = before.SelectedLevel;
        var previousCurrent = before.CurrentLevel;

        try
        {
            Replace(before with
            {
                IsApplying = true,
                IsLoading = false,
                SelectedLevel = level,
                StatusMessage = $"Applying {level}…",
                StatusKind = KeyboardLightingStatusKind.Neutral
            });

            var result = await _controller.SetLevelAsync(level, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Accepted)
            {
                Replace(State with
                {
                    IsApplying = false,
                    IsLoading = false,
                    CurrentLevel = level,
                    SelectedLevel = level,
                    StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                        ? $"Keyboard lighting set to {level}."
                        : result.Message,
                    StatusKind = KeyboardLightingStatusKind.Success
                });
            }
            else
            {
                Replace(State with
                {
                    IsApplying = false,
                    IsLoading = false,
                    CurrentLevel = previousCurrent,
                    SelectedLevel = previousSelection,
                    StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                        ? "Keyboard lighting was not changed."
                        : result.Message,
                    StatusKind = KeyboardLightingStatusKind.Error
                });
            }
        }

        catch (OperationCanceledException)
        {
            Replace(State with
            {
                IsApplying = false,
                IsLoading = false,
                CurrentLevel = previousCurrent,
                SelectedLevel = previousSelection
            });
        }
        catch (Exception ex) when (ex is TimeoutException
                                   or UnauthorizedAccessException
                                   or ManagementException
                                   or InvalidOperationException)
        {
            Replace(State with
            {
                IsApplying = false,
                IsLoading = false,
                CurrentLevel = previousCurrent,
                SelectedLevel = previousSelection,
                StatusMessage = ex.Message,
                StatusKind = KeyboardLightingStatusKind.Error
            });
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ApplyRgbEffectAsync(KeyboardRgbSettings settings, CancellationToken cancellationToken)
    {
        await SendRgbEffectAsync(settings, "Applying", cancellationToken).ConfigureAwait(false);
    }

    public async Task PreviewRgbEffectAsync(KeyboardRgbSettings settings, CancellationToken cancellationToken)
    {
        await SendRgbEffectAsync(settings, "Previewing", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRgbEffectAsync(
        KeyboardRgbSettings settings,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
            return;
        try
        {
            Replace(State with { IsApplying = true, StatusMessage = $"{operationName} {settings.Effect}…", StatusKind = KeyboardLightingStatusKind.Neutral });
            var result = await _controller.SetRgbEffectAsync(settings, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Replace(State with
            {
                IsApplying = false,
                LastConfirmedRgbSettings = result.Accepted ? settings : State.LastConfirmedRgbSettings,
                StatusMessage = result.Accepted && operationName == "Previewing"
                    ? $"Preview active: {settings.Effect}."
                    : result.Message,
                StatusKind = result.Accepted ? KeyboardLightingStatusKind.Success : KeyboardLightingStatusKind.Error
            });
        }
        catch (OperationCanceledException)
        {
            Replace(State with { IsApplying = false });
        }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or InvalidOperationException or System.IO.IOException)
        {
            Replace(State with { IsApplying = false, StatusMessage = ex.Message, StatusKind = KeyboardLightingStatusKind.Error });
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBeginOperation()
    {
        lock (_gate)
        {
            if (_operationInFlight)
                return false;
            _operationInFlight = true;
            return true;
        }
    }

    private void EndOperation()
    {
        lock (_gate)
            _operationInFlight = false;
    }

    private void Replace(KeyboardLightingState next)
    {
        lock (_gate)
            _state = next;
        StateChanged?.Invoke();
    }
}

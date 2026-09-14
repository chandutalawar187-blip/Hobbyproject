namespace LenovoLoqControl.UI;

/// <summary>Lightweight UI preferences shared across shell and pages (no hardware coupling).</summary>
public static class AppUiPreferences
{
    // Opt-in via Settings. Do not mirror SystemParameters.ClientAreaAnimation —
    // that flag is often false on gaming PCs even when in-app motion is desired.
    private static bool _reducedMotion;

    public static event Action? Changed;
    public static event Action? AppearanceChanged;

    public static AppearanceMode Appearance { get; set; } = AppearanceMode.System;

    public static void SetAppearance(AppearanceMode mode)
    {
        if (Appearance == mode) return;
        Appearance = mode;
        AppearanceChanged?.Invoke();
    }

    public static bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (_reducedMotion == value) return;
            _reducedMotion = value;
            Changed?.Invoke();
        }
    }
}

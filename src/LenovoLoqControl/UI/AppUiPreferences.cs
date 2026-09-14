using Microsoft.Win32;
using System.Security;

namespace LenovoLoqControl.UI;

/// <summary>Lightweight UI preferences shared across shell and pages (no hardware coupling).</summary>
public static class AppUiPreferences
{
    // Opt-in via Settings. Do not mirror SystemParameters.ClientAreaAnimation —
    // that flag is often false on gaming PCs even when in-app motion is desired.
    private static bool _reducedMotion;
    private static bool? _runInTrayOnClose;

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

    public static bool RunInTrayOnClose
        {
            get
            {
                if (_runInTrayOnClose is bool value)
                    return value;

                try
                {
                    _runInTrayOnClose = (Registry.GetValue(
                        @"HKEY_CURRENT_USER\Software\LOQ Control",
                        "RunInTrayOnClose",
                        1) is int stored && stored != 0);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
                {
                    _runInTrayOnClose = true;
                }

                return _runInTrayOnClose.Value;
            }
            set
            {
                if (RunInTrayOnClose == value)
                    return;

                _runInTrayOnClose = value;
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\LOQ Control");
                    key?.SetValue("RunInTrayOnClose", value ? 1 : 0, RegistryValueKind.DWord);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
                {
                    // The in-memory preference still applies for this session.
                }

                Changed?.Invoke();
        }
    }
}

namespace Blocker.Core;

/// <summary>
/// Constants governing allowed-application launch, window-discovery, and mini-HUD lifecycle.
/// </summary>
public static class ZenLaunchConstants
{
    /// <summary>
    /// Minimum width and height in pixels for a window to be considered a genuine, non-helper application top-level window.
    /// Excludes system indicator windows, tray popups, toasts, and tooltips.
    /// </summary>
    public const int MinWindowDimension = 100;

    /// <summary>
    /// Short debounce interval (in milliseconds) after an allowed application window is minimized, hidden,
    /// or destroyed, before the mini-HUD is hidden and the Zen screen is restored.
    /// </summary>
    public const int DefaultDebounceMs = 750;

    /// <summary>
    /// Grace period (in milliseconds) during an asynchronous application launch or window discovery.
    /// </summary>
    public const int LaunchGracePeriodMs = 4000;

    /// <summary>
    /// Watchdog polling backstop interval (in milliseconds).
    /// </summary>
    public const int WatchdogIntervalMs = 500;
}

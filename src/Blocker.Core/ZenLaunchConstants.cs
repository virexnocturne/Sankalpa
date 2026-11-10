using System;

namespace Blocker.Core;

/// <summary>
/// Constants governing allowed-application launch, window-discovery, and mini-HUD lifecycle.
/// Adheres to Shared Contract C-5: single definition point for launch timeouts, poll intervals, and grace periods.
/// </summary>
public static class ZenLaunchConstants
{
    /// <summary>
    /// Discovery timeout for locating a USABLE application window (reduced from 30s to 8s).
    /// </summary>
    public static readonly TimeSpan MaxDiscoveryTimeout = TimeSpan.FromSeconds(8);
    public const int MaxDiscoveryTimeoutMs = 8000;

    /// <summary>
    /// Performance SLA targets: warm switch <= 2s, cold launch <= 5s.
    /// </summary>
    public static readonly TimeSpan WarmSwitchTarget = TimeSpan.FromSeconds(2);
    public const int WarmSwitchTargetMs = 2000;

    public static readonly TimeSpan ColdLaunchTarget = TimeSpan.FromSeconds(5);
    public const int ColdLaunchTargetMs = 5000;

    /// <summary>
    /// Watchdog foreground grace period while actively launching an allowed app.
    /// </summary>
    public static readonly TimeSpan LaunchGracePeriod = TimeSpan.FromSeconds(4);
    public const int LaunchGracePeriodMs = 4000;

    /// <summary>
    /// Minimum width and height in pixels for a window to be considered a genuine, usable, non-helper application top-level window.
    /// Excludes system indicator windows, tray popups, toasts, and tooltips.
    /// </summary>
    public const int MinWindowDimension = 100;

    /// <summary>
    /// Short debounce interval (in milliseconds) after an allowed application window is minimized, hidden,
    /// or destroyed, before the mini-HUD is hidden and the Zen screen is restored.
    /// </summary>
    public const int DefaultDebounceMs = 750;

    /// <summary>
    /// Watchdog polling backstop interval (in milliseconds).
    /// </summary>
    public const int WatchdogIntervalMs = 500;

    /// <summary>
    /// Window discovery polling interval in milliseconds.
    /// </summary>
    public const int PollIntervalMs = 100;

    /// <summary>
    /// Settle delay for rapid UI interactions / click debouncing in milliseconds.
    /// </summary>
    public const int SettleDelayMs = 300;

    /// <summary>
    /// Maximum unauthorized breaches before enforcing Zen screen restore.
    /// </summary>
    public const int MaxUnauthorizedBreaches = 3;
}

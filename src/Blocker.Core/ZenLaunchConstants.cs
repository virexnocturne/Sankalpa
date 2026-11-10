using System;

namespace Blocker.Core;

/// <summary>
/// Single-source constants for Meditation (Zen) mode application launching, switching, and timeouts.
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
    /// Minimum width and height for a window to be considered USABLE per Shared Contract C-1.
    /// </summary>
    public const int MinWindowDimension = 100;

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

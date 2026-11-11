using System;

namespace Blocker.Core;

/// <summary>
/// Describes the UI actions needed for a Mini-HUD visibility evaluation.
/// </summary>
public readonly record struct MiniHudShowPlan(
    bool ShouldShowOrPositionHud,
    bool ShouldActivateAllowedApp,
    bool ShouldReassertHudTopmost,
    bool ShouldUpdateBackdrops);

/// <summary>
/// Coordinates Mini-HUD show/hide transitions so repeated "still visible" evaluations are
/// idempotent and never disturb the allowed application's focus or z-order.
/// </summary>
public sealed class MiniHudPresentationCoordinator
{
    private static readonly TimeSpan DefaultTopmostReassertCooldown = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _topmostReassertCooldown;
    private bool _explicitSwitchPending;
    private DateTime _lastTopmostReassertUtc = DateTime.MinValue;

    public bool IsHudVisible { get; private set; }
    public IntPtr ActiveAllowedHwnd { get; private set; }

    public MiniHudPresentationCoordinator(TimeSpan? topmostReassertCooldown = null)
    {
        _topmostReassertCooldown = topmostReassertCooldown ?? DefaultTopmostReassertCooldown;
    }

    /// <summary>
    /// Marks the next show evaluation as an explicit warm or cold launch switch.
    /// </summary>
    public void RequestExplicitSwitch()
    {
        _explicitSwitchPending = true;
    }

    /// <summary>
    /// Records that the Mini-HUD has been hidden (e.g. returning to Zen screen or lost focus debounce).
    /// </summary>
    public void NotifyHidden()
    {
        IsHudVisible = false;
        ActiveAllowedHwnd = IntPtr.Zero;
        _explicitSwitchPending = false;
    }

    /// <summary>
    /// Computes which UI actions are required for the current state.
    /// Re-issuing focus/z-order changes on the allowed app occurs only on real transitions:
    /// HUD hidden->shown, retarget to a different HWND, or an explicit warm/cold switch.
    /// </summary>
    public MiniHudShowPlan EvaluateShow(
        IntPtr allowedHwnd,
        bool isHudWindowCurrentlyVisible,
        bool hudIsActuallyTopmost,
        bool isModalDialogOpen,
        DateTime utcNow)
    {
        if (isModalDialogOpen)
        {
            return new MiniHudShowPlan(false, false, false, false);
        }

        bool wasExplicitSwitch = _explicitSwitchPending;
        _explicitSwitchPending = false;

        bool isTransition =
            !IsHudVisible ||
            !isHudWindowCurrentlyVisible ||
            (allowedHwnd != IntPtr.Zero && allowedHwnd != ActiveAllowedHwnd) ||
            wasExplicitSwitch;

        if (isTransition)
        {
            IsHudVisible = true;
            ActiveAllowedHwnd = allowedHwnd;
            _lastTopmostReassertUtc = utcNow;
            return new MiniHudShowPlan(
                ShouldShowOrPositionHud: true,
                ShouldActivateAllowedApp: allowedHwnd != IntPtr.Zero,
                ShouldReassertHudTopmost: false,
                ShouldUpdateBackdrops: true);
        }

        bool shouldReassertHudTopmost = false;
        if (!hudIsActuallyTopmost && (utcNow - _lastTopmostReassertUtc) >= _topmostReassertCooldown)
        {
            _lastTopmostReassertUtc = utcNow;
            shouldReassertHudTopmost = true;
        }

        return new MiniHudShowPlan(
            ShouldShowOrPositionHud: false,
            ShouldActivateAllowedApp: false,
            ShouldReassertHudTopmost: shouldReassertHudTopmost,
            ShouldUpdateBackdrops: false);
    }
}

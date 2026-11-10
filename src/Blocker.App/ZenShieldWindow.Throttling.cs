using System;
using System.Windows.Threading;
using Blocker.Core;

namespace Blocker.App;

public partial class ZenShieldWindow
{
    private BackgroundAppThrottler? _backgroundThrottler;
    private DispatcherTimer? _throttlerTickTimer;
    private bool _throttlingInitialized;

    private void EnsureThrottlingStarted()
    {
        if (_throttlingInitialized) return;
        _throttlingInitialized = true;

        _backgroundThrottler = BackgroundAppThrottler.Default;
        _backgroundThrottler.StartSession(_allowedAppProcessNames, _allowedAppRoots);

        _throttlerTickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _throttlerTickTimer.Tick += (s, e) => _backgroundThrottler?.Tick();
        _throttlerTickTimer.Start();

        Closed += (s, e) =>
        {
            _throttlerTickTimer?.Stop();
            _throttlerTickTimer = null;
            _backgroundThrottler?.EndSession();
        };
    }

    private void OnForegroundAllowedAppChangedForThrottling(int pid, IntPtr hwnd)
    {
        EnsureThrottlingStarted();
        _backgroundThrottler?.UpdateForegroundApp(pid, hwnd);
    }
}

using System;
using System.Linq;
using System.Windows.Threading;
using Blocker.Core;

namespace Blocker.App;

public partial class ZenShieldWindow
{
    private ThrottlerBackgroundWorker? _throttlerWorker;
    private DispatcherTimer? _throttlerTickTimer;
    private bool _throttlingInitialized;
    private bool _throttlingEnded;
    private bool _requireSyncRestoreOnClose;

    private void EnsureThrottlingStarted()
    {
        if (_throttlingInitialized) return;
        _throttlingInitialized = true;
        _throttlingEnded = false;

        _throttlerWorker = ThrottlerBackgroundWorker.Default;
        _throttlerWorker.EnqueueStartSession(_allowedAppProcessNames?.ToList(), _allowedAppRoots);

        _throttlerTickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _throttlerTickTimer.Tick += (s, e) => _throttlerWorker?.EnqueueTick();
        _throttlerTickTimer.Start();

        Closed += (s, e) => CompleteThrottlingOnClose(_requireSyncRestoreOnClose);
    }

    private void CompleteThrottlingOnClose(bool waitForCompletion)
    {
        _throttlerTickTimer?.Stop();
        _throttlerTickTimer = null;

        if (!_throttlingInitialized || _throttlingEnded) return;
        _throttlingEnded = true;

        _throttlerWorker?.EnqueueEndSession(waitForCompletion: waitForCompletion, boundedWaitMs: 3000);
    }

    private void OnForegroundAllowedAppChangedForThrottling(int pid, IntPtr hwnd)
    {
        EnsureThrottlingStarted();
        _throttlerWorker?.EnqueueForegroundUpdate(pid, hwnd);
    }
}

using System;
using System.Collections.Concurrent;

namespace Blocker.Core;

public class NotificationThrottler
{
    private readonly TimeSpan _throttleWindow;
    private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

    public NotificationThrottler(TimeSpan? throttleWindow = null)
    {
        _throttleWindow = throttleWindow ?? TimeSpan.FromSeconds(5);
    }

    public bool ShouldNotify(string appName, DateTime nowUtc, string kind = "App")
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        string cleanKind = string.IsNullOrWhiteSpace(kind) ? "App" : kind.Trim();
        string key = $"{cleanKind}:{appName.Trim()}";

        bool shouldNotify = false;
        _lastNotified.AddOrUpdate(
            key,
            _ =>
            {
                shouldNotify = true;
                return nowUtc;
            },
            (_, lastTime) =>
            {
                if (nowUtc - lastTime >= _throttleWindow)
                {
                    shouldNotify = true;
                    return nowUtc;
                }
                shouldNotify = false;
                return lastTime;
            });

        return shouldNotify;
    }

    public void Reset()
    {
        _lastNotified.Clear();
    }
}

using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class SecurityHardenerTests
{
    [Fact]
    public void LockdownAndUnlock_RoundTrip_Succeeds()
    {
        bool lockSuccess = SecurityHardener.LockdownCurrentProcess(out string? lockError);
        Assert.True(lockSuccess, $"Lockdown failed: {lockError}");
        Assert.True(SecurityHardener.IsLockedDown);

        bool unlockSuccess = SecurityHardener.UnlockCurrentProcess(out string? unlockError);
        Assert.True(unlockSuccess, $"Unlock failed: {unlockError}");
        Assert.False(SecurityHardener.IsLockedDown);
    }
}

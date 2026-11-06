using System;
using System.Collections.Generic;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class DelayedUnlockTests
{
    [Fact]
    public void DelayedUnlockManager_StartAndCancel_WorksProperly()
    {
        string profileName = "TestProfile_" + Guid.NewGuid().ToString("N");
        string token = DelayedUnlockManager.StartDelayedUnlock(profileName, 900);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(DelayedUnlockManager.IsDelayedUnlockActive(profileName));
        Assert.True(DelayedUnlockManager.GetRemainingSeconds(profileName) > 850);

        // Cancel
        bool cancelled = DelayedUnlockManager.CancelDelayedUnlock(profileName, token);
        Assert.True(cancelled);
        Assert.False(DelayedUnlockManager.IsDelayedUnlockActive(profileName));
    }

    [Fact]
    public void DelayedUnlockManager_ValidateUnlock_RejectsEarlyUnlock()
    {
        string profileName = "EarlyProfile_" + Guid.NewGuid().ToString("N");
        string token = DelayedUnlockManager.StartDelayedUnlock(profileName, 900); // 15 minutes

        bool valid = DelayedUnlockManager.ValidateUnlock(profileName, token, out var error);
        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("still running", error);

        // Clean up
        DelayedUnlockManager.CancelDelayedUnlock(profileName, token);
    }

    [Fact]
    public void DelayedUnlockManager_ValidateUnlock_RejectsInvalidToken()
    {
        string profileName = "InvalidTokenProfile_" + Guid.NewGuid().ToString("N");
        string token = DelayedUnlockManager.StartDelayedUnlock(profileName, 900);

        bool valid = DelayedUnlockManager.ValidateUnlock(profileName, "wrong_token", out var error);
        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("Invalid or expired", error);

        DelayedUnlockManager.CancelDelayedUnlock(profileName, token);
    }

    [Fact]
    public void DelayedUnlockManager_CancelDelayedUnlock_WithoutTokenOrWrongToken_IsRefused()
    {
        string profileName = "RefuseCancelProfile_" + Guid.NewGuid().ToString("N");
        string validToken = DelayedUnlockManager.StartDelayedUnlock(profileName, 900);

        // Cancel with empty / whitespace / null / wrong token should return false
        Assert.False(DelayedUnlockManager.CancelDelayedUnlock(profileName, ""));
        Assert.False(DelayedUnlockManager.CancelDelayedUnlock(profileName, "   "));
        Assert.False(DelayedUnlockManager.CancelDelayedUnlock(profileName, null!));
        Assert.False(DelayedUnlockManager.CancelDelayedUnlock(profileName, "wrong_token"));

        // Verify delayed unlock remains active
        Assert.True(DelayedUnlockManager.IsDelayedUnlockActive(profileName));

        // Cancel with valid token should succeed
        bool cancelled = DelayedUnlockManager.CancelDelayedUnlock(profileName, validToken);
        Assert.True(cancelled);
        Assert.False(DelayedUnlockManager.IsDelayedUnlockActive(profileName));
    }

    [Fact]
    public void ProfileStateLogic_LockedNow_ReturnsTrueForDelayedUnlockWhenActive()
    {
        var profile = new Profile
        {
            Id = 10,
            Name = "FocusBlock",
            Enabled = true,
            LockMode = LockMode.DelayedUnlock,
            DelayedUnlockMinutes = 15
        };

        bool isLocked = ProfileStateLogic.IsProfileLockedNow(profile, null, DateTime.Now);
        Assert.True(isLocked);
    }
}

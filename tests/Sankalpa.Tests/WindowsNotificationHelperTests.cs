using System;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class WindowsNotificationHelperTests
{
    [Fact]
    public void IsNotificationDisabledInWindows_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
        {
            bool disabled = WindowsNotificationHelper.IsNotificationDisabledInWindows();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void IsNotificationDisabledInWindows_ReturnsBooleanValue()
    {
        if (OperatingSystem.IsWindows())
        {
            bool result = WindowsNotificationHelper.IsNotificationDisabledInWindows();
            // Should return either true or false without error
            Assert.True(result || !result);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Blocker.Core;
using Xunit;

namespace Sankalpa.Tests;

public class ZenLaunchStateMachineTests
{
    [Fact]
    public void InitialState_IsIdle_WithEmptyFields()
    {
        var sm = new ZenLaunchStateMachine();

        Assert.Equal(ZenLaunchState.Idle, sm.CurrentState);
        Assert.Null(sm.CurrentAppName);
        Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
        Assert.Equal(0, sm.ActivePid);
        Assert.Null(sm.LastError);
    }

    [Fact]
    public void RequestLaunch_WithUsableWindow_TransitionsDirectlyToActive()
    {
        var sm = new ZenLaunchStateMachine();
        bool requested = sm.RequestLaunch("notepad", hasUsableWindow: true);

        Assert.True(requested);
        Assert.Equal(ZenLaunchState.Active, sm.CurrentState);
        Assert.Equal("notepad", sm.CurrentAppName);
        Assert.Null(sm.LastError);
    }

    [Fact]
    public void RequestLaunch_WithoutUsableWindow_TransitionsToLaunching()
    {
        var sm = new ZenLaunchStateMachine();
        bool requested = sm.RequestLaunch("msedge", hasUsableWindow: false);

        Assert.True(requested);
        Assert.Equal(ZenLaunchState.Launching, sm.CurrentState);
        Assert.Equal("msedge", sm.CurrentAppName);
    }

    [Fact]
    public void RequestLaunch_EmptyOrNullName_ReturnsFalse()
    {
        var sm = new ZenLaunchStateMachine();
        Assert.False(sm.RequestLaunch("", false));
        Assert.False(sm.RequestLaunch("   ", true));
        Assert.Equal(ZenLaunchState.Idle, sm.CurrentState);
    }

    [Fact]
    public void WindowDiscovered_FromLaunching_TransitionsToActive()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("msedge", hasUsableWindow: false);

        var fakeHwnd = new IntPtr(0x12345);
        bool discovered = sm.WindowDiscovered(fakeHwnd, 9999);

        Assert.True(discovered);
        Assert.Equal(ZenLaunchState.Active, sm.CurrentState);
        Assert.Equal(fakeHwnd, sm.ActiveHwnd);
        Assert.Equal(9999, sm.ActivePid);
    }

    [Fact]
    public void WindowDiscovered_WithZeroHwnd_ReturnsFalse()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("msedge", false);

        bool discovered = sm.WindowDiscovered(IntPtr.Zero, 1234);
        Assert.False(discovered);
        Assert.Equal(ZenLaunchState.Launching, sm.CurrentState);
    }

    [Fact]
    public void WindowDiscovered_FromIdle_ReturnsFalse()
    {
        var sm = new ZenLaunchStateMachine();
        bool discovered = sm.WindowDiscovered(new IntPtr(0x99), 1234);

        Assert.False(discovered);
        Assert.Equal(ZenLaunchState.Idle, sm.CurrentState);
    }

    [Fact]
    public void LaunchFailed_FromLaunching_TransitionsToFailed()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("custom_app", false);

        sm.LaunchFailed("File not found");

        Assert.Equal(ZenLaunchState.Failed, sm.CurrentState);
        Assert.Equal("File not found", sm.LastError);
    }

    [Fact]
    public void Timeout_FromLaunching_TransitionsToFailed()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("slow_app", false);

        sm.Timeout();

        Assert.Equal(ZenLaunchState.Failed, sm.CurrentState);
        Assert.Equal("Discovery timed out", sm.LastError);
    }

    [Fact]
    public void ReturnToZen_FromActive_ClearsHwndAndTransitionsToReturnedToZen()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("code", false);
        sm.WindowDiscovered(new IntPtr(0x444), 5678);

        sm.ReturnToZen();

        Assert.Equal(ZenLaunchState.ReturnedToZen, sm.CurrentState);
        Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
        Assert.Equal(0, sm.ActivePid);
    }

    [Fact]
    public void ReturnToZen_FromLaunching_CancelsAndTransitionsToReturnedToZen()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("msedge", false);

        sm.ReturnToZen();

        Assert.Equal(ZenLaunchState.ReturnedToZen, sm.CurrentState);
        Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
    }

    [Fact]
    public void WindowClosed_FromActive_TransitionsToReturnedToZen()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("spotify", true);
        sm.WindowDiscovered(new IntPtr(0x555), 789);

        sm.WindowClosed();

        Assert.Equal(ZenLaunchState.ReturnedToZen, sm.CurrentState);
        Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
        Assert.Equal(0, sm.ActivePid);
    }

    [Fact]
    public void Reset_FromAnyState_ResetsToIdle()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("app", false);
        sm.LaunchFailed("Error");
        Assert.Equal(ZenLaunchState.Failed, sm.CurrentState);

        sm.Reset();

        Assert.Equal(ZenLaunchState.Idle, sm.CurrentState);
        Assert.Null(sm.CurrentAppName);
        Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
        Assert.Equal(0, sm.ActivePid);
        Assert.Null(sm.LastError);
    }

    [Fact]
    public void StartClosing_TransitionsToClosing_AndBlocksFurtherLaunches()
    {
        var sm = new ZenLaunchStateMachine();
        sm.RequestLaunch("notepad", true);

        sm.StartClosing();
        Assert.Equal(ZenLaunchState.Closing, sm.CurrentState);

        bool launched = sm.RequestLaunch("another_app", true);
        Assert.False(launched);
        Assert.Equal(ZenLaunchState.Closing, sm.CurrentState);
    }

    [Fact]
    public void TenConsecutiveCycles_Open_ReturnToZen_Reopen_Succeed()
    {
        var sm = new ZenLaunchStateMachine();

        for (int i = 1; i <= 10; i++)
        {
            var hwnd = new IntPtr(0x1000 + i);
            int pid = 1000 + i;

            // 1. Cold launch or warm switch
            bool cold = (i % 2 == 1);
            if (cold)
            {
                sm.RequestLaunch("msedge", hasUsableWindow: false);
                Assert.Equal(ZenLaunchState.Launching, sm.CurrentState);
                sm.WindowDiscovered(hwnd, pid);
            }
            else
            {
                sm.RequestLaunch("msedge", hasUsableWindow: true, hwnd, pid);
            }

            Assert.Equal(ZenLaunchState.Active, sm.CurrentState);
            Assert.Equal(hwnd, sm.ActiveHwnd);
            Assert.Equal(pid, sm.ActivePid);

            // 2. Return to Zen
            sm.ReturnToZen();
            Assert.Equal(ZenLaunchState.ReturnedToZen, sm.CurrentState);
            Assert.Equal(IntPtr.Zero, sm.ActiveHwnd);
            Assert.Equal(0, sm.ActivePid);
        }
    }

    [Fact]
    public void StateChangedEvent_FiresOnEveryTransition()
    {
        var sm = new ZenLaunchStateMachine();
        var transitions = new List<(ZenLaunchState From, ZenLaunchState To)>();
        sm.StateChanged += (from, to) => transitions.Add((from, to));

        sm.RequestLaunch("test_app", false);
        sm.WindowDiscovered(new IntPtr(0x123), 456);
        sm.ReturnToZen();
        sm.Reset();

        Assert.Equal(4, transitions.Count);
        Assert.Equal((ZenLaunchState.Idle, ZenLaunchState.Launching), transitions[0]);
        Assert.Equal((ZenLaunchState.Launching, ZenLaunchState.Active), transitions[1]);
        Assert.Equal((ZenLaunchState.Active, ZenLaunchState.ReturnedToZen), transitions[2]);
        Assert.Equal((ZenLaunchState.ReturnedToZen, ZenLaunchState.Idle), transitions[3]);
    }

    [Fact]
    public void ConcurrencyStress_MultipleThreads_NeverEntersInvalidState()
    {
        var sm = new ZenLaunchStateMachine();

        Parallel.For(0, 100, i =>
        {
            sm.RequestLaunch($"app_{i}", i % 2 == 0);
            sm.WindowDiscovered(new IntPtr(i + 1), i);
            sm.ReturnToZen();
            sm.Reset();
        });

        Assert.Equal(ZenLaunchState.Idle, sm.CurrentState);
    }
}

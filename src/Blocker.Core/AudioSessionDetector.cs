using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Blocker.Core;

/// <summary>
/// Detects processes with active Core Audio sessions (e.g. playing music or media)
/// using Windows Core Audio COM APIs (IAudioSessionManager2 / IAudioSessionEnumerator).
/// Allows background audio players (Spotify, VLC, browser tabs with audio) to be
/// exempt from CPU/power throttling so playback never stutters.
/// </summary>
public static class AudioSessionDetector
{
    private const int S_OK = 0;
    private const int CLSCTX_ALL = 23;
    private const int AudioSessionStateActive = 1;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCoClass
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice(string id, out IntPtr device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId(out IntPtr strId);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        int RegisterSessionNotification(IntPtr client);
        int UnregisterSessionNotification(IntPtr client);
        int RegisterDuckNotification(string sessionId, IntPtr client);
        int UnregisterDuckNotification(IntPtr client);
    }

    [ComImport]
    [Guid("E2F5EE52-DD07-4EE6-B1F1-0E247E748473")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);
        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);
        int GetDisplayName(out IntPtr displayName);
        int SetDisplayName(string displayName, ref Guid eventContext);
        int GetIconPath(out IntPtr iconPath);
        int SetIconPath(string iconPath, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c31")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        new int GetState(out int state);
        new int GetDisplayName(out IntPtr displayName);
        new int SetDisplayName(string displayName, ref Guid eventContext);
        new int GetIconPath(out IntPtr iconPath);
        new int SetIconPath(string iconPath, ref Guid eventContext);
        new int GetGroupingParam(out Guid groupingParam);
        new int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        new int RegisterAudioSessionNotification(IntPtr client);
        new int UnregisterAudioSessionNotification(IntPtr client);

        int GetSessionIdentifier(out IntPtr retVal);
        int GetSessionInstanceIdentifier(out IntPtr retVal);
        [PreserveSig]
        int GetProcessId(out uint retVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>
    /// Returns the set of process IDs that currently have an active audio playback session.
    /// Silently returns an empty set if audio hardware is absent, disabled, or on non-Windows.
    /// </summary>
    public static HashSet<int> GetActiveAudioProcessIds()
    {
        var activePids = new HashSet<int>();

        if (!OperatingSystem.IsWindows())
        {
            return activePids;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? defaultDevice = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            // eRender = 0, eMultimedia = 1
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) != S_OK || defaultDevice == null)
            {
                return activePids;
            }

            var iid = IID_IAudioSessionManager2;
            if (defaultDevice.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object sessionManagerObj) != S_OK || sessionManagerObj == null)
            {
                return activePids;
            }

            sessionManager = (IAudioSessionManager2)sessionManagerObj;
            if (sessionManager.GetSessionEnumerator(out sessionEnumerator) != S_OK || sessionEnumerator == null)
            {
                return activePids;
            }

            if (sessionEnumerator.GetCount(out int count) != S_OK)
            {
                return activePids;
            }

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? sessionControl = null;
                try
                {
                    if (sessionEnumerator.GetSession(i, out sessionControl) == S_OK && sessionControl != null)
                    {
                        if (sessionControl.GetState(out int state) == S_OK && state == AudioSessionStateActive)
                        {
                            if (sessionControl is IAudioSessionControl2 sessionControl2)
                            {
                                if (sessionControl2.GetProcessId(out uint pid) == S_OK && pid > 4)
                                {
                                    activePids.Add((int)pid);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore individual session inspection failures
                }
                finally
                {
                    if (sessionControl != null)
                    {
                        try { Marshal.ReleaseComObject(sessionControl); } catch { }
                    }
                }
            }
        }
        catch
        {
            // Fail safely: audio hardware unavailable or COM initialization issue
        }
        finally
        {
            if (sessionEnumerator != null)
            {
                try { Marshal.ReleaseComObject(sessionEnumerator); } catch { }
            }
            if (sessionManager != null)
            {
                try { Marshal.ReleaseComObject(sessionManager); } catch { }
            }
            if (defaultDevice != null)
            {
                try { Marshal.ReleaseComObject(defaultDevice); } catch { }
            }
            if (enumerator != null)
            {
                try { Marshal.ReleaseComObject(enumerator); } catch { }
            }
        }

        return activePids;
    }
}

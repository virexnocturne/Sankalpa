using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Blocker.Core;

/// <summary>
/// Thread-safe Win32 low-level keyboard hook (WH_KEYBOARD_LL = 13) that blocks system keys
/// such as Windows keys, Alt+Tab, and Ctrl/Alt+Esc during system lockdown and meditation mode.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_S = 0x53;
    private const int VK_SNAPSHOT = 0x2C;

    private const uint LLKHF_ALTDOWN = 0x20;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private readonly object _syncLock = new();
    private readonly HookProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _isActive;
    private bool _disposed;

    // Dedicated Polling Thread for Emergency Chord
    private Thread? _pollingThread;
    private volatile bool _stopPolling;
    private readonly ManualResetEventSlim _pollStopEvent = new(false);

    // Single-fire guard for emergency escape chord across hook and polling subsystems
    private int _emergencyTriggered = 0;

    private DateTime _emergencyChordStartTime = DateTime.MinValue;
    private CancellationTokenSource? _emergencyHoldCts;
    private readonly object _emergencyLock = new();

    public bool IsActive
    {
        get
        {
            lock (_syncLock)
            {
                return _isActive;
            }
        }
    }

    public bool IsEmergencyTriggered => Volatile.Read(ref _emergencyTriggered) != 0;

    /// <summary>
    /// Triggered when the user holds Ctrl + Shift + Alt + Escape for 3 seconds continuously.
    /// </summary>
    public event Action? EmergencyEscapeTriggered;

    /// <summary>
    /// Optional callback invoked when the user triggers Win + Shift + S to snip the screen,
    /// allowing lockdown shells to immediately yield topmost z-order to ScreenClippingHost.
    /// </summary>
    public Action? OnSnippingRequested { get; set; }

    public LowLevelKeyboardHook()
    {
        _hookProc = HookCallback;
    }

    public void Start()
    {
        lock (_syncLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LowLevelKeyboardHook));
            if (_isActive) return;

            Interlocked.Exchange(ref _emergencyTriggered, 0);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _isActive = true;
                return;
            }

            using var readyEvent = new ManualResetEventSlim(false);
            Exception? initError = null;

            _hookThread = new Thread(() =>
            {
                _hookThreadId = GetCurrentThreadId();
                try
                {
                    using var curProcess = Process.GetCurrentProcess();
                    using var curModule = curProcess.MainModule;
                    IntPtr hMod = curModule?.ModuleName != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

                    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
                    if (_hookHandle == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        initError = new InvalidOperationException($"SetWindowsHookEx failed with Win32 error code {err}.");
                    }
                }
                catch (Exception ex)
                {
                    initError = ex;
                }
                finally
                {
                    readyEvent.Set();
                }

                if (_hookHandle != IntPtr.Zero)
                {
                    // Run standard Win32 message pump for the low-level hook
                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "LowLevelKeyboardHookThread"
            };

            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Priority = ThreadPriority.Highest;
            _hookThread.Start();

            if (!readyEvent.Wait(TimeSpan.FromSeconds(5)))
            {
                Stop();
                throw new TimeoutException("Timed out waiting for LowLevelKeyboardHook thread initialization.");
            }

            if (initError != null)
            {
                Stop();
                throw initError;
            }

            _isActive = _hookHandle != IntPtr.Zero;

            if (_isActive)
            {
                _stopPolling = false;
                _pollStopEvent.Reset();
                _pollingThread = new Thread(EmergencyPollingLoop)
                {
                    IsBackground = true,
                    Name = "EmergencyChordPollingThread",
                    Priority = ThreadPriority.AboveNormal
                };
                _pollingThread.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            if (!_isActive && _hookThread == null && _pollingThread == null) return;

            lock (_emergencyLock)
            {
                if (_emergencyHoldCts != null)
                {
                    _emergencyHoldCts.Cancel();
                    _emergencyHoldCts.Dispose();
                    _emergencyHoldCts = null;
                }
            }

            _stopPolling = true;
            _pollStopEvent.Set();

            if (_pollingThread != null && _pollingThread.IsAlive)
            {
                _pollingThread.Join(TimeSpan.FromSeconds(1));
                _pollingThread = null;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }

                if (_hookThreadId != 0)
                {
                    PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                    _hookThreadId = 0;
                }

                if (_hookThread != null && _hookThread.IsAlive)
                {
                    _hookThread.Join(TimeSpan.FromSeconds(2));
                    _hookThread = null;
                }
            }

            _isActive = false;
        }
    }

    /// <summary>
    /// Safely reinstalls the low-level keyboard hook without gaps where keys could leak through.
    /// Always installs the new hook before removing the old hook.
    /// </summary>
    public bool Rehook()
    {
        lock (_syncLock)
        {
            if (_disposed || !_isActive) return false;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;

            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr hMod = curModule?.ModuleName != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

                IntPtr newHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
                if (newHookHandle != IntPtr.Zero)
                {
                    IntPtr oldHookHandle = _hookHandle;
                    _hookHandle = newHookHandle;
                    if (oldHookHandle != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(oldHookHandle);
                    }
                    return true;
                }
            }
            catch { }

            return false;
        }
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private static bool IsCtrlDown() =>
        IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);

    private static bool IsAltDown() =>
        IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);

    private static bool IsShiftDown() =>
        IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT);

    private static bool IsEscapeDown() =>
        IsKeyDown(VK_ESCAPE);

    public static bool IsEmergencyChordDown()
    {
        return IsCtrlDown() && IsAltDown() && IsShiftDown() && IsEscapeDown();
    }

    private void EmergencyPollingLoop()
    {
        var tracker = new EmergencyChordTracker();
        var sw = Stopwatch.StartNew();

        while (!_stopPolling && !_disposed)
        {
            if (Volatile.Read(ref _emergencyTriggered) != 0)
            {
                break;
            }

            try
            {
                bool chordDown = IsEmergencyChordDown();
                long now = sw.ElapsedMilliseconds;

                if (tracker.Update(chordDown, now))
                {
                    TriggerEmergency("poll");
                    break;
                }
            }
            catch { }

            if (_pollStopEvent.Wait(50))
            {
                break;
            }
        }
    }

    private void TriggerEmergency(string subsystem)
    {
        if (Interlocked.CompareExchange(ref _emergencyTriggered, 1, 0) == 0)
        {
            string logMessage = subsystem == "hook" ? "[CHORD] via hook" : "[CHORD] via poll";
            try
            {
                Trace.WriteLine(logMessage);
                Debug.WriteLine(logMessage);
                Console.WriteLine(logMessage);
            }
            catch { }

            Task.Run(() =>
            {
                try
                {
                    EmergencyEscapeTriggered?.Invoke();
                }
                catch { }

                try
                {
                    WriteEmergencyUnlockFiles();
                }
                catch { }
            });
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = kbd.vkCode;
            uint msg = (uint)wParam;

            bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                           (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
                             (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
                             (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;

            bool altDown = (kbd.flags & LLKHF_ALTDOWN) != 0 ||
                           (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
                           (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
                           (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

            // Emergency Escape Chord: Ctrl + Shift + Alt + Escape held continuously for 3.0 seconds
            bool isEscDown = (vk == VK_ESCAPE) || ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0);
            bool isChordDown = isEscDown && ctrlDown && altDown && shiftDown;

            lock (_emergencyLock)
            {
                if (isChordDown && Volatile.Read(ref _emergencyTriggered) == 0)
                {
                    if (_emergencyHoldCts == null)
                    {
                        _emergencyChordStartTime = DateTime.UtcNow;
                        _emergencyHoldCts = new CancellationTokenSource();
                        var token = _emergencyHoldCts.Token;
                        Task.Run(async () =>
                        {
                            try
                            {
                                var tracker = new EmergencyChordTracker();
                                var sw = Stopwatch.StartNew();
                                tracker.Update(true, 0);

                                while (!token.IsCancellationRequested && Volatile.Read(ref _emergencyTriggered) == 0)
                                {
                                    await Task.Delay(50, token).ConfigureAwait(false);
                                    if (token.IsCancellationRequested) return;

                                    bool chordDown = IsEmergencyChordDown();
                                    long now = sw.ElapsedMilliseconds;

                                    if (tracker.Update(chordDown, now))
                                    {
                                        TriggerEmergency("hook");
                                        return;
                                    }

                                    if (!tracker.IsHolding)
                                    {
                                        return;
                                    }
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch { }
                            finally
                            {
                                lock (_emergencyLock)
                                {
                                    _emergencyHoldCts?.Dispose();
                                    _emergencyHoldCts = null;
                                }
                            }
                        }, token);
                    }

                    if (vk == VK_ESCAPE)
                    {
                        return (IntPtr)1;
                    }
                }
            }

            // Whitelist Win + Shift + S, Ctrl + Shift + S, and PrintScreen for In-House Sankalpa Snip
            if ((vk == VK_S && winDown && shiftDown) ||
                (vk == VK_S && ctrlDown && shiftDown) ||
                (vk == VK_SNAPSHOT))
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    try
                    {
                        OnSnippingRequested?.Invoke();
                    }
                    catch { }
                }
                return (IntPtr)1;
            }

            bool block = false;

            // 0. Block VK_TAB (0x09) whenever Win is held (Win + Tab) as well as when Alt is held (Alt + Tab)
            if (vk == VK_TAB && (winDown || altDown))
            {
                block = true;
            }
            // 1. VK_LWIN (0x5B) and VK_RWIN (0x5C) - suppresses Start Menu
            else if (vk == VK_LWIN || vk == VK_RWIN)
            {
                block = true;
            }
            // 2. Prevent touchpad swipe down bypass and any shortcut while Win key is down (Win+D, Win+M, Win+E, Win+Tab, etc.)
            else if (winDown)
            {
                // Allow Shift keypress through so Win+Shift+S combo can be formed cleanly
                if (vk != VK_SHIFT && vk != VK_LSHIFT && vk != VK_RSHIFT)
                {
                    block = true;
                }
            }
            // 3. VK_TAB (0x09) when Alt is held (Alt+Tab)
            else if (vk == VK_TAB && altDown)
            {
                block = true;
            }
            // 4. VK_ESCAPE (0x1B) when Alt is held or Ctrl is held (Alt+Esc, Ctrl+Esc)
            else if (vk == VK_ESCAPE && (altDown || ctrlDown))
            {
                block = true;
            }

            if (block)
            {
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public static void WriteEmergencyUnlockFiles()
    {
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string f1 = Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK");
            string f2 = Path.Combine(appData, "Blocker", "EMERGENCY_UNLOCK");
            string f3 = Path.Combine(appData, "Sankalpa", "EMERGENCY_UNLOCK");
            string f4 = Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK");
            string f5 = Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK");

            var targets = new[] { f1, f2, f3, f4, f5 };
            foreach (var file in targets)
            {
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(file, "EMERGENCY_RELEASE");
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _pollStopEvent.Dispose();
        }
    }
}

/// <summary>
/// Tracks the emergency chord hold duration with 300 ms debounce / gap tolerance
/// so keyboard matrix drops / lack of NKRO on laptop keyboards do not falsely cancel the hold.
/// Only real held time is counted toward the 3,000 ms threshold.
/// </summary>
public sealed class EmergencyChordTracker
{
    private long _accumulatedHeldMs;
    private long _lastHeldTime;
    private long _releasedTime = -1;
    private bool _isHolding;
    private DateTime _firstPressedTime = DateTime.MinValue;

    public bool IsHolding => _isHolding;
    public long AccumulatedHeldMs => _accumulatedHeldMs;
    public DateTime FirstPressedTime => _firstPressedTime;

    public bool Update(bool isChordDown, long nowMs)
    {
        if (isChordDown)
        {
            if (!_isHolding)
            {
                _isHolding = true;
                _firstPressedTime = DateTime.UtcNow;
                _accumulatedHeldMs = 0;
                _lastHeldTime = nowMs;
                _releasedTime = -1;
            }
            else
            {
                if (_releasedTime >= 0)
                {
                    long gap = nowMs - _releasedTime;
                    if (gap <= 300)
                    {
                        // Recovered within 300 ms grace tolerance; continue counting real held time
                        _releasedTime = -1;
                        _lastHeldTime = nowMs;
                    }
                    else
                    {
                        // Released for > 300 ms continuously; cancel and start new hold
                        _firstPressedTime = DateTime.UtcNow;
                        _accumulatedHeldMs = 0;
                        _lastHeldTime = nowMs;
                        _releasedTime = -1;
                    }
                }
                else
                {
                    long delta = nowMs - _lastHeldTime;
                    if (delta > 0)
                    {
                        _accumulatedHeldMs += delta;
                        _lastHeldTime = nowMs;
                    }
                }
            }
        }
        else
        {
            if (_isHolding)
            {
                if (_releasedTime < 0)
                {
                    _releasedTime = nowMs;
                }
                else if (nowMs - _releasedTime > 300)
                {
                    // Released for more than 300 ms continuously
                    Reset();
                }
            }
        }

        return _isHolding && _accumulatedHeldMs >= 3000;
    }

    public void Reset()
    {
        _isHolding = false;
        _accumulatedHeldMs = 0;
        _lastHeldTime = 0;
        _releasedTime = -1;
        _firstPressedTime = DateTime.MinValue;
    }
}

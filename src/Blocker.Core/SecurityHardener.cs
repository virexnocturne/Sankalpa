using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Blocker.Core;

public static class SecurityHardener
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string StringSecurityDescriptor,
        int StringSDRevision,
        out IntPtr SecurityDescriptor,
        out UIntPtr SecurityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(
        IntPtr Handle,
        int SecurityInformation,
        IntPtr pSecurityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int SDDL_REVISION_1 = 1;
    private const int DACL_SECURITY_INFORMATION = 4;

    // Deny PROCESS_TERMINATE (0x0001), PROCESS_SUSPEND_RESUME (0x0800), PROCESS_VM_WRITE (0x0020)
    // to Everyone (WD) and Builtin Administrators (BA).
    // Allow Query Limited Info (0x1000) to Everyone.
    // Allow Full Control (FA) to Local System (SY).
    public const string LockedDaclSddl = "D:(D;;0x0821;;;WD)(D;;0x0821;;;BA)(A;;0x1000;;;WD)(A;;FA;;;SY)";

    // Default permissive DACL allowing standard management
    public const string UnlockedDaclSddl = "D:(A;;FA;;;WD)(A;;FA;;;BA)(A;;FA;;;SY)";

    // Deny SERVICE_STOP (0x0020), SERVICE_PAUSE_CONTINUE (0x0040), DELETE (0x00010000)
    // to Administrators (BA) and Everyone (WD).
    // Allow Full Control (KA) to Local System (SY).
    // Allow Read/Query/Start to Authenticated Users (AU).
    public const string LockedServiceSddl = "D:(D;;0x0020;;;BA)(D;;0x0020;;;WD)(D;;0x00010000;;;BA)(D;;0x00010000;;;WD)(A;;CCLCSWRPWPDTLOCRRC;;;AU)(A;;KA;;;SY)";

    // Default permissive service SDDL allowing administrative control and standard user query/start
    public const string UnlockedServiceSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;SU)(A;;CCLCSWRPWPDTLOCRRC;;;AU)";

    public static bool IsLockedDown { get; private set; }
    public static bool IsServiceLockedDown { get; private set; }

    public static bool LockdownService(out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c sc.exe sdset SankalpaService \"{LockedServiceSddl}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            IsServiceLockedDown = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool UnlockService(out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c sc.exe sdset SankalpaService \"{UnlockedServiceSddl}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            IsServiceLockedDown = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool LockdownCurrentProcess(out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(LockedDaclSddl, SDDL_REVISION_1, out IntPtr pSd, out _))
            {
                int err = Marshal.GetLastWin32Error();
                error = $"Failed to parse locked SDDL: win32 error {err}";
                return false;
            }

            try
            {
                if (!SetKernelObjectSecurity(GetCurrentProcess(), DACL_SECURITY_INFORMATION, pSd))
                {
                    int err = Marshal.GetLastWin32Error();
                    error = $"Failed to set kernel object security: win32 error {err}";
                    return false;
                }
            }
            finally
            {
                LocalFree(pSd);
            }

            IsLockedDown = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool UnlockCurrentProcess(out string? error)
    {
        error = null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(UnlockedDaclSddl, SDDL_REVISION_1, out IntPtr pSd, out _))
            {
                int err = Marshal.GetLastWin32Error();
                error = $"Failed to parse unlocked SDDL: win32 error {err}";
                return false;
            }

            try
            {
                if (!SetKernelObjectSecurity(GetCurrentProcess(), DACL_SECURITY_INFORMATION, pSd))
                {
                    int err = Marshal.GetLastWin32Error();
                    error = $"Failed to restore kernel object security: win32 error {err}";
                    return false;
                }
            }
            finally
            {
                LocalFree(pSd);
            }

            IsLockedDown = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

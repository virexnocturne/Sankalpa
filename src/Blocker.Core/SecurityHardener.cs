using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Blocker.Core;

public record ServiceProcessResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool ProcessCreationFailed = false
);

public interface IServiceSecurityRunner
{
    ServiceProcessResult RunSc(string[] arguments, int timeoutMs = 3000);

    ServiceProcessResult SetServiceSecurityDescriptor(string serviceName, string sddl, int timeoutMs = 3000)
        => RunSc(["sdset", serviceName, sddl], timeoutMs);

    ServiceProcessResult GetServiceSecurityDescriptor(string serviceName, int timeoutMs = 3000)
        => RunSc(["sdshow", serviceName], timeoutMs);
}

public class DefaultServiceSecurityRunner : IServiceSecurityRunner
{
    private readonly string _scPath;

    public DefaultServiceSecurityRunner(string scPath = "sc.exe")
    {
        _scPath = scPath;
    }

    public virtual ServiceProcessResult RunSc(string[] arguments, int timeoutMs = 3000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _scPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                return new ServiceProcessResult(
                    Success: false,
                    ExitCode: -1,
                    StandardOutput: string.Empty,
                    StandardError: $"Failed to start {_scPath}: {ex.Message}",
                    ProcessCreationFailed: true
                );
            }

            if (proc == null)
            {
                return new ServiceProcessResult(
                    Success: false,
                    ExitCode: -1,
                    StandardOutput: string.Empty,
                    StandardError: $"Failed to start {_scPath}: Process.Start returned null.",
                    ProcessCreationFailed: true
                );
            }

            using (proc)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch { }

                    return new ServiceProcessResult(
                        Success: false,
                        ExitCode: -1,
                        StandardOutput: string.Empty,
                        StandardError: $"{_scPath} process timed out after {timeoutMs}ms and was killed.",
                        TimedOut: true
                    );
                }

                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);
                string stdout = stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty;
                string stderr = stderrTask.IsCompleted ? stderrTask.Result : string.Empty;

                int exitCode = proc.ExitCode;
                return new ServiceProcessResult(
                    Success: exitCode == 0,
                    ExitCode: exitCode,
                    StandardOutput: stdout,
                    StandardError: stderr
                );
            }
        }
        catch (Exception ex)
        {
            return new ServiceProcessResult(
                Success: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: ex.Message
            );
        }
    }

    public virtual ServiceProcessResult SetServiceSecurityDescriptor(string serviceName, string sddl, int timeoutMs = 3000)
        => RunSc(["sdset", serviceName, sddl], timeoutMs);

    public virtual ServiceProcessResult GetServiceSecurityDescriptor(string serviceName, int timeoutMs = 3000)
        => RunSc(["sdshow", serviceName], timeoutMs);
}

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

    public const string ServiceName = "SankalpaService";
    public const int DefaultTimeoutMs = 3000;

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

    private static IServiceSecurityRunner _serviceSecurityRunner = new DefaultServiceSecurityRunner();

    public static IServiceSecurityRunner ServiceSecurityRunner
    {
        get => _serviceSecurityRunner;
        set => _serviceSecurityRunner = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static bool IsLockedDown { get; private set; }
    public static bool IsServiceLockedDown { get; internal set; }

    public static void ResetServiceLockdownStateForTesting(bool isLockedDown = false)
    {
        IsServiceLockedDown = isLockedDown;
    }

    public static void ResetStateForTesting()
    {
        IsServiceLockedDown = false;
        IsLockedDown = false;
        _serviceSecurityRunner = new DefaultServiceSecurityRunner();
    }

    public static string NormalizeSddl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("O:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("G:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        return raw.Trim();
    }

    public static string CanonicalizeSddlTokens(string sddl)
    {
        if (string.IsNullOrEmpty(sddl)) return sddl;
        return sddl
            .Replace("0x0020", "WP", StringComparison.OrdinalIgnoreCase)
            .Replace("0x20", "WP", StringComparison.OrdinalIgnoreCase)
            .Replace("0x00010000", "SD", StringComparison.OrdinalIgnoreCase)
            .Replace("0x10000", "SD", StringComparison.OrdinalIgnoreCase);
    }

    public static bool VerifySddlMatch(string actualOutput, string expectedSddl)
    {
        string actual = NormalizeSddl(actualOutput).Replace(" ", "");
        string expected = NormalizeSddl(expectedSddl).Replace(" ", "");

        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        string actualNorm = CanonicalizeSddlTokens(actual);
        string expectedNorm = CanonicalizeSddlTokens(expected);

        if (string.Equals(actualNorm, expectedNorm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int daclIndex = actual.IndexOf("D:", StringComparison.OrdinalIgnoreCase);
        if (daclIndex >= 0)
        {
            string actualDacl = actual.Substring(daclIndex);
            int saclIndex = actualDacl.IndexOf("S:", StringComparison.OrdinalIgnoreCase);
            if (saclIndex > 0)
            {
                actualDacl = actualDacl.Substring(0, saclIndex);
            }

            string actualDaclNorm = CanonicalizeSddlTokens(actualDacl);
            if (string.Equals(actualDaclNorm, expectedNorm, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool LockdownService(
        out string? error,
        IServiceSecurityRunner? runner = null,
        string serviceName = ServiceName,
        int timeoutMs = DefaultTimeoutMs)
    {
        error = null;
        runner ??= ServiceSecurityRunner;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && runner is DefaultServiceSecurityRunner)
        {
            return true;
        }

        try
        {
            // 1. Invoke sc.exe sdset
            var setResult = runner.SetServiceSecurityDescriptor(serviceName, LockedServiceSddl, timeoutMs);
            if (setResult.ProcessCreationFailed)
            {
                error = string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? "Failed to create process for sc.exe"
                    : setResult.StandardError.Trim();
                return false;
            }

            if (setResult.TimedOut)
            {
                error = string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? $"sc.exe sdset timed out after {timeoutMs}ms"
                    : setResult.StandardError.Trim();
                return false;
            }

            if (setResult.ExitCode != 0 || !setResult.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? setResult.StandardError.Trim()
                    : setResult.StandardOutput.Trim();
                error = $"sc.exe sdset failed with exit code {setResult.ExitCode}" +
                        (string.IsNullOrEmpty(detail) ? "" : $": {detail}");
                return false;
            }

            // 2. Query and verify service security descriptor
            var queryResult = runner.GetServiceSecurityDescriptor(serviceName, timeoutMs);
            if (queryResult.ProcessCreationFailed)
            {
                error = string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? "Failed to create process when querying service security descriptor"
                    : queryResult.StandardError.Trim();
                return false;
            }

            if (queryResult.TimedOut)
            {
                error = string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? $"Querying service security descriptor timed out after {timeoutMs}ms"
                    : queryResult.StandardError.Trim();
                return false;
            }

            if (queryResult.ExitCode != 0 || !queryResult.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? queryResult.StandardError.Trim()
                    : queryResult.StandardOutput.Trim();
                error = $"Querying service security descriptor failed with exit code {queryResult.ExitCode}" +
                        (string.IsNullOrEmpty(detail) ? "" : $": {detail}");
                return false;
            }

            if (!VerifySddlMatch(queryResult.StandardOutput, LockedServiceSddl))
            {
                string actual = NormalizeSddl(queryResult.StandardOutput);
                error = $"Service security descriptor verification failed. Expected: '{LockedServiceSddl}', Actual: '{actual}'";
                return false;
            }

            IsServiceLockedDown = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool UnlockService(
        out string? error,
        IServiceSecurityRunner? runner = null,
        string serviceName = ServiceName,
        int timeoutMs = DefaultTimeoutMs)
    {
        error = null;
        runner ??= ServiceSecurityRunner;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && runner is DefaultServiceSecurityRunner)
        {
            return true;
        }

        try
        {
            // 1. Invoke sc.exe sdset
            var setResult = runner.SetServiceSecurityDescriptor(serviceName, UnlockedServiceSddl, timeoutMs);
            if (setResult.ProcessCreationFailed)
            {
                error = string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? "Failed to create process for sc.exe"
                    : setResult.StandardError.Trim();
                return false;
            }

            if (setResult.TimedOut)
            {
                error = string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? $"sc.exe sdset timed out after {timeoutMs}ms"
                    : setResult.StandardError.Trim();
                return false;
            }

            if (setResult.ExitCode != 0 || !setResult.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(setResult.StandardError)
                    ? setResult.StandardError.Trim()
                    : setResult.StandardOutput.Trim();
                error = $"sc.exe sdset failed with exit code {setResult.ExitCode}" +
                        (string.IsNullOrEmpty(detail) ? "" : $": {detail}");
                return false;
            }

            // 2. Query and verify service security descriptor
            var queryResult = runner.GetServiceSecurityDescriptor(serviceName, timeoutMs);
            if (queryResult.ProcessCreationFailed)
            {
                error = string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? "Failed to create process when querying service security descriptor"
                    : queryResult.StandardError.Trim();
                return false;
            }

            if (queryResult.TimedOut)
            {
                error = string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? $"Querying service security descriptor timed out after {timeoutMs}ms"
                    : queryResult.StandardError.Trim();
                return false;
            }

            if (queryResult.ExitCode != 0 || !queryResult.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(queryResult.StandardError)
                    ? queryResult.StandardError.Trim()
                    : queryResult.StandardOutput.Trim();
                error = $"Querying service security descriptor failed with exit code {queryResult.ExitCode}" +
                        (string.IsNullOrEmpty(detail) ? "" : $": {detail}");
                return false;
            }

            if (!VerifySddlMatch(queryResult.StandardOutput, UnlockedServiceSddl))
            {
                string actual = NormalizeSddl(queryResult.StandardOutput);
                error = $"Service security descriptor verification failed. Expected: '{UnlockedServiceSddl}', Actual: '{actual}'";
                return false;
            }

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

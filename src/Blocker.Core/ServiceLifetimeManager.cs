using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace Blocker.Core;

public static class ServiceLifetimeManager
{
    private static readonly object _syncLock = new();

    public const string ServiceName = "SankalpaService";
    public const string ServiceDisplayName = "Sankalpa Enforcer Service";
    public const string ServiceDescription = "Continuous tamper-resistant enforcer for Sankalpa blocker.";

    public static bool IsElevated()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.IsSystem || identity.User?.Value == "S-1-5-18")
                {
                    return true;
                }
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch { }
        return false;
    }

    public static bool IsServiceProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("Blocker.Service").Length > 0 ||
                   Process.GetProcessesByName("Sankalpa.Service").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryResolveServiceExePath(out string serviceExePath)
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(baseDir, "Blocker.Service.exe"),
            Path.Combine(baseDir, "Sankalpa.Service.exe"),
            Path.Combine(baseDir, "..", "service", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "Blocker.Service", "bin", "Debug", "net8.0", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "Blocker.Service", "bin", "Release", "net8.0", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "Blocker.Service", "bin", "Debug", "net8.0", "win-x64", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "Blocker.Service", "bin", "Release", "net8.0", "win-x64", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "Blocker.Service", "bin", "Debug", "net8.0", "Blocker.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "Blocker.Service", "bin", "Release", "net8.0", "Blocker.Service.exe")
        };

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c))
                {
                    serviceExePath = Path.GetFullPath(c);
                    return true;
                }
            }
            catch { }
        }

        serviceExePath = string.Empty;
        return false;
    }

    public static bool RunSc(string[] args, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            output = (stdout + " " + stderr).Trim();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    public static bool IsServiceInstalled()
    {
        RunSc(["query", ServiceName], out string output);
        return !output.Contains("1060");
    }

    public static bool IsServiceRunning()
    {
        RunSc(["query", ServiceName], out string output);
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> EnsureServiceRunningAsync(IPipeClient pipeClient, int maxWaitSeconds = 6)
    {
        lock (_syncLock)
        {
            bool elevated = IsElevated();
            bool hasServiceExe = TryResolveServiceExePath(out string serviceExe);

            // 1. Ensure Windows Service is registered in SCM to auto-start on boot
            if (elevated && hasServiceExe)
            {
                if (!IsServiceInstalled())
                {
                    RunSc(["create", ServiceName, $"binPath= \"{serviceExe}\"", "start= auto", $"DisplayName= {ServiceDisplayName}"], out _);
                    RunSc(["description", ServiceName, ServiceDescription], out _);
                    RunSc(["failure", ServiceName, "reset= 0", "actions= restart/0/restart/0/restart/0"], out _);
                    RunSc(["sdset", ServiceName, SecurityHardener.UnlockedServiceSddl], out _);
                    RunSc(["start", ServiceName], out _);
                }
                else if (!IsServiceRunning())
                {
                    RunSc(["config", ServiceName, $"binPath= \"{serviceExe}\""], out _);
                    RunSc(["start", ServiceName], out _);
                }

                // Register native messaging host for all browsers
                try
                {
                    ExtensionHelper.RegisterHost();
                }
                catch { }
            }
            else
            {
                // Try starting registered service in case it's already installed
                RunSc(["start", ServiceName], out _);
            }

            // 2. Only launch directly in background if we are ELEVATED (e.g. CLI or elevated run)
            // NEVER launch an un-elevated background service process from a standard-user UI session,
            // because un-elevated processes lack permissions for hosts/DNS, conflict with SCM,
            // and lead to an unkillable respawn loop.
            if (!IsServiceRunning() && !IsServiceProcessRunning() && hasServiceExe && elevated)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = serviceExe,
                        WorkingDirectory = Path.GetDirectoryName(serviceExe) ?? AppContext.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ServiceLifetimeManager] Failed to launch background process: {ex.Message}");
                }
            }
        }

        // 3. Quick check: Is pipe already responsive?
        try
        {
            var ping = await pipeClient.SendRequestAsync(new Request { Command = CommandNames.Ping });
            if (ping.Connected && ping.Response?.Ok == true)
            {
                return true;
            }
        }
        catch { }

        // 4. Poll for pipe readiness
        int iterations = Math.Max(1, maxWaitSeconds * 4);
        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(250);
            try
            {
                var check = await pipeClient.SendRequestAsync(new Request { Command = CommandNames.Ping });
                if (check.Connected && check.Response?.Ok == true)
                {
                    return true;
                }
            }
            catch { }
        }

        return false;
    }
}

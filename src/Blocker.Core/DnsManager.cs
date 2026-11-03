using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Blocker.Core;

public static class DnsManager
{
    public static List<string> GetActiveInterfaceNames()
    {
        var list = new List<string>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    list.Add(ni.Name);
                }
            }
        }
        catch { }

        // Fallback: include standard Windows interface names if list is empty
        if (list.Count == 0)
        {
            list.Add("Wi-Fi");
            list.Add("Ethernet");
        }

        return list;
    }

    public static bool ApplyDns(string primary, string secondary, out string output)
    {
        return ApplyDns(primary, secondary, string.Empty, string.Empty, out output);
    }

    public static bool ApplyDns(string primary, string secondary, string primaryIpv6, string secondaryIpv6, out string output)
    {
        output = string.Empty;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            output = "DNS configuration is only supported on Windows.";
            return false;
        }

        primary = (primary ?? string.Empty).Trim();
        secondary = (secondary ?? string.Empty).Trim();
        primaryIpv6 = (primaryIpv6 ?? string.Empty).Trim();
        secondaryIpv6 = (secondaryIpv6 ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(primaryIpv6))
        {
            output = "Primary DNS cannot be empty.";
            return false;
        }

        IPAddress? primaryIp = null;
        if (!string.IsNullOrWhiteSpace(primary))
        {
            if (!IPAddress.TryParse(primary, out primaryIp) || primaryIp.AddressFamily != AddressFamily.InterNetwork)
            {
                output = "Invalid IPv4 address format for primary DNS.";
                return false;
            }
        }

        IPAddress? secondaryIp = null;
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            if (!IPAddress.TryParse(secondary, out secondaryIp) || secondaryIp.AddressFamily != AddressFamily.InterNetwork)
            {
                output = "Invalid IPv4 address format for secondary DNS.";
                return false;
            }
        }

        IPAddress? primaryIpv6Ip = null;
        if (!string.IsNullOrWhiteSpace(primaryIpv6))
        {
            if (!IPAddress.TryParse(primaryIpv6, out primaryIpv6Ip) || primaryIpv6Ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                output = "Invalid IPv6 address format for primary IPv6 DNS.";
                return false;
            }
        }

        IPAddress? secondaryIpv6Ip = null;
        if (!string.IsNullOrWhiteSpace(secondaryIpv6))
        {
            if (!IPAddress.TryParse(secondaryIpv6, out secondaryIpv6Ip) || secondaryIpv6Ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                output = "Invalid IPv6 address format for secondary IPv6 DNS.";
                return false;
            }
        }

        bool anySuccess = false;
        var interfaces = GetActiveInterfaceNames();

        foreach (var iface in interfaces)
        {
            string safeIface = iface.Replace("\"", string.Empty);
            try
            {
                // 1. Set Primary IPv4 DNS
                if (primaryIp != null)
                {
                    RunProcess("netsh.exe", $"interface ipv4 set dnsservers name=\"{safeIface}\" static {primaryIp} primary validate=no", out _);
                    anySuccess = true;

                    // 2. Add Secondary IPv4 DNS if provided
                    if (secondaryIp != null)
                    {
                        RunProcess("netsh.exe", $"interface ipv4 add dnsservers name=\"{safeIface}\" {secondaryIp} index=2 validate=no", out _);
                    }
                }

                // 3. Set IPv6 DNS (Prevents IPv6 leak that bypasses IPv4 DNS on modern ISPs)
                if (primaryIpv6Ip != null)
                {
                    RunProcess("netsh.exe", $"interface ipv6 set dnsservers name=\"{safeIface}\" static {primaryIpv6Ip} primary validate=no", out _);

                    if (secondaryIpv6Ip != null)
                    {
                        RunProcess("netsh.exe", $"interface ipv6 add dnsservers name=\"{safeIface}\" {secondaryIpv6Ip} index=2 validate=no", out _);
                    }
                }
                else
                {
                    // If no IPv6 filter DNS is configured, delete dynamic/ISP IPv6 DNS so traffic doesn't bypass the IPv4 filter
                    RunProcess("netsh.exe", $"interface ipv6 delete dnsservers name=\"{safeIface}\" all", out _);
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
            }
        }

        // 4. Lockdown browser DNS-over-HTTPS (DoH) so Chrome/Edge cannot bypass OS DNS
        EnforceBrowserDohPolicies(enablePolicyLock: true);

        // 5. Immediately flush DNS cache
        FlushDnsAsync();
        return anySuccess;
    }

    public static bool ResetDns(out string output)
    {
        output = string.Empty;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            output = "DNS configuration is only supported on Windows.";
            return false;
        }

        bool anySuccess = false;
        var interfaces = GetActiveInterfaceNames();

        foreach (var iface in interfaces)
        {
            string safeIface = iface.Replace("\"", string.Empty);
            try
            {
                // Reset IPv4 to DHCP
                RunProcess("netsh.exe", $"interface ipv4 set dnsservers name=\"{safeIface}\" source=dhcp", out _);
                // Reset IPv6 to DHCP
                RunProcess("netsh.exe", $"interface ipv6 set dnsservers name=\"{safeIface}\" source=dhcp", out _);
                anySuccess = true;
            }
            catch (Exception ex)
            {
                output = ex.Message;
            }
        }

        // Restore browser DoH settings
        EnforceBrowserDohPolicies(enablePolicyLock: false);

        FlushDnsAsync();
        return anySuccess;
    }

    public static void FlushDnsAsync()
    {
        Task.Run(() =>
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(3000);
            }
            catch { }
        });
    }

    public static bool IsDnsActive(string expectedPrimary, string expectedPrimaryIpv6 = "")
    {
        if (string.IsNullOrWhiteSpace(expectedPrimary) && string.IsNullOrWhiteSpace(expectedPrimaryIpv6)) return false;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var ipProps = ni.GetIPProperties();
                    foreach (var dns in ipProps.DnsAddresses)
                    {
                        string dnsStr = dns.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(expectedPrimary) &&
                            string.Equals(dnsStr, expectedPrimary.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        if (!string.IsNullOrWhiteSpace(expectedPrimaryIpv6) &&
                            string.Equals(dnsStr, expectedPrimaryIpv6.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static void EnforceBrowserDohPolicies(bool enablePolicyLock)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var policyPaths = new[]
        {
            @"SOFTWARE\Policies\Microsoft\Edge",
            @"SOFTWARE\Policies\Google\Chrome",
            @"SOFTWARE\Policies\BraveSoftware\Brave"
        };

        foreach (var path in policyPaths)
        {
            try
            {
                if (enablePolicyLock)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(path, true);
                    if (key != null)
                    {
                        // Disables built-in DoH and forces browser to respect Windows OS filtered DNS
                        key.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);
                        key.SetValue("BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);
                    }
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path, true);
                    if (key != null)
                    {
                        try { key.DeleteValue("DnsOverHttpsMode", false); } catch { }
                        try { key.DeleteValue("BuiltInDnsClientEnabled", false); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static int RunProcess(string fileName, string args, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return -1;
            output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}

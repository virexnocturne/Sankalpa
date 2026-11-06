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
    public static bool IsVirtualOrExcludedAdapter(string description, string name, NetworkInterfaceType type)
    {
        if (type == NetworkInterfaceType.Loopback || type == NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        string desc = description ?? string.Empty;
        string nm = name ?? string.Empty;

        // Oracle VirtualBox Host-Only adapters
        if (desc.IndexOf("VirtualBox Host-Only", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // VMware VMnet Host-Only adapters
        if (desc.IndexOf("VMware Network Adapter VMnet", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("VMnet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // Microsoft Wi-Fi Direct virtual adapter
        if (desc.IndexOf("Microsoft Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("Local Area Connection*", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // Hyper-V internal switches (WSL, Default Switch)
        if (desc.IndexOf("Hyper-V Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("vEthernet (WSL)", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("vEthernet (Default Switch)", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // NDIS Lightweight Filters / Packet Schedulers
        if (desc.IndexOf("LightWeight Filter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            desc.IndexOf("Filter Driver", StringComparison.OrdinalIgnoreCase) >= 0 ||
            desc.IndexOf("Packet Scheduler", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("Filter-", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nm.IndexOf("Scheduler-", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    public static bool IsVirtualOrExcludedAdapter(NetworkInterface ni)
    {
        if (IsVirtualOrExcludedAdapter(ni.Description, ni.Name, ni.NetworkInterfaceType))
        {
            return true;
        }

        return false;
    }

    public static bool HasDefaultGateway(NetworkInterface ni)
    {
        try
        {
            var props = ni.GetIPProperties();
            return props.GatewayAddresses.Any(g =>
                g.Address != null &&
                !IPAddress.Any.Equals(g.Address) &&
                !IPAddress.IPv6Any.Equals(g.Address) &&
                !string.IsNullOrWhiteSpace(g.Address.ToString()));
        }
        catch
        {
            return false;
        }
    }

    public static List<NetworkInterface> GetTargetNetworkInterfaces()
    {
        var list = new List<NetworkInterface>();
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var upInterfaces = all
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && !IsVirtualOrExcludedAdapter(ni))
                .ToList();

            // 1. Interfaces with active default gateways are actively routing internet traffic
            var withGateway = upInterfaces.Where(HasDefaultGateway).ToList();
            if (withGateway.Count > 0)
            {
                return withGateway;
            }

            // 2. Fallback: all non-virtual UP interfaces
            if (upInterfaces.Count > 0)
            {
                return upInterfaces;
            }
        }
        catch { }

        return list;
    }

    public static List<string> GetActiveInterfaceNames()
    {
        var list = new List<string>();
        try
        {
            var targetInterfaces = GetTargetNetworkInterfaces();
            foreach (var ni in targetInterfaces)
            {
                if (!list.Contains(ni.Name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(ni.Name);
                }
            }

            // Also include any physical UP Ethernet/Wi-Fi interfaces even if gateway negotiation is pending
            var all = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in all)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    !IsVirtualOrExcludedAdapter(ni) &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                {
                    if (!list.Contains(ni.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(ni.Name);
                    }
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

        // Also include any other non-loopback interface that currently has static preset DNS configured
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !interfaces.Contains(ni.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var props = ni.GetIPProperties();
                    if (props.DnsAddresses.Any(d =>
                        d.ToString().StartsWith("1.1.1.") ||
                        d.ToString().StartsWith("1.0.0.") ||
                        d.ToString().StartsWith("185.228.") ||
                        d.ToString().StartsWith("94.140.") ||
                        d.ToString().StartsWith("9.9.9.") ||
                        d.ToString().StartsWith("2606:4700:") ||
                        d.ToString().StartsWith("2a0d:2a00:") ||
                        d.ToString().StartsWith("2a10:50c0:") ||
                        d.ToString().StartsWith("2620:fe:")))
                    {
                        interfaces.Add(ni.Name);
                    }
                }
            }
        }
        catch { }

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

    public static bool IPAddressEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (IPAddress.TryParse(a.Trim(), out var ipA) && IPAddress.TryParse(b.Trim(), out var ipB))
        {
            return ipA.Equals(ipB);
        }
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDnsActive(string expectedPrimary, string expectedPrimaryIpv6 = "", string expectedSecondary = "", string expectedSecondaryIpv6 = "")
    {
        if (string.IsNullOrWhiteSpace(expectedPrimary) && string.IsNullOrWhiteSpace(expectedPrimaryIpv6)) return false;

        try
        {
            var targetInterfaces = GetTargetNetworkInterfaces();
            if (targetInterfaces.Count == 0)
            {
                // No active internet adapters - cannot leak DNS
                return true;
            }

            foreach (var ni in targetInterfaces)
            {
                var ipProps = ni.GetIPProperties();
                var dnsList = ipProps.DnsAddresses;

                // 1. Check IPv4 Primary DNS
                if (!string.IsNullOrWhiteSpace(expectedPrimary))
                {
                    var firstV4 = dnsList.FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                    if (firstV4 == null || !IPAddressEquals(firstV4.ToString(), expectedPrimary))
                    {
                        return false;
                    }

                    // 2. Check IPv4 Secondary DNS if provided and present
                    if (!string.IsNullOrWhiteSpace(expectedSecondary))
                    {
                        var v4List = dnsList.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
                        if (v4List.Count > 1 && !IPAddressEquals(v4List[1].ToString(), expectedSecondary))
                        {
                            return false;
                        }
                    }
                }

                // 3. Check IPv6 Primary DNS if configured and present
                if (!string.IsNullOrWhiteSpace(expectedPrimaryIpv6))
                {
                    var firstV6 = dnsList.FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetworkV6);
                    if (firstV6 != null && !IPAddressEquals(firstV6.ToString(), expectedPrimaryIpv6))
                    {
                        return false;
                    }

                    // 4. Check IPv6 Secondary DNS if configured and present
                    if (!string.IsNullOrWhiteSpace(expectedSecondaryIpv6))
                    {
                        var v6List = dnsList.Where(d => d.AddressFamily == AddressFamily.InterNetworkV6).ToList();
                        if (v6List.Count > 1 && !IPAddressEquals(v6List[1].ToString(), expectedSecondaryIpv6))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
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

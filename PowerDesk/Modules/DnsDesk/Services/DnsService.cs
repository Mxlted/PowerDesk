using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Modules.DnsDesk.Models;

namespace PowerDesk.Modules.DnsDesk.Services;

public sealed class DnsService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private readonly ILogger _log;

    public DnsService(ILogger log)
    {
        _log = log;
    }

    public IReadOnlyList<DnsAdapter> GetAdapters()
    {
        var adapters = new List<DnsAdapter>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties props;
            try { props = adapter.GetIPProperties(); }
            catch (Exception ex)
            {
                _log.Error($"DNS adapter properties: {adapter.Name}", ex);
                continue;
            }

            var ipv4Dns = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var ipv6Dns = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var supportsIpv4 = adapter.Supports(NetworkInterfaceComponent.IPv4);
            var supportsIpv6 = adapter.Supports(NetworkInterfaceComponent.IPv6);
            var ipv6Unicast = props.UnicastAddresses
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                .ToList();
            var usableIpv6 = adapter.OperationalStatus == OperationalStatus.Up &&
                ipv6Unicast.Any(IsUsableIpv6Address);
            var gateways = props.GatewayAddresses
                .Select(g => g.Address?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            adapters.Add(new DnsAdapter
            {
                Id = adapter.Id,
                Name = adapter.Name,
                Description = adapter.Description,
                InterfaceType = adapter.NetworkInterfaceType,
                Status = adapter.OperationalStatus,
                Ipv4DnsServers = ipv4Dns.Count == 0 ? "-" : string.Join(", ", ipv4Dns),
                Ipv6DnsServers = ipv6Dns.Count == 0 ? "-" : string.Join(", ", ipv6Dns),
                GatewayAddresses = gateways.Count == 0 ? "-" : string.Join(", ", gateways),
                HasIpv4 = supportsIpv4,
                HasIpv6 = supportsIpv6,
                HasUsableIpv6 = usableIpv6,
            });
        }

        return adapters
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<(bool Success, string Message)> ApplyProfileAsync(DnsAdapter adapter, DnsProfile profile)
    {
        var messages = new List<string>();
        var changed = false;

        if (profile.UseDhcp)
        {
            if (adapter.HasIpv4)
            {
                var ipv4 = await RunNetshAsync("interface", "ipv4", "set", "dnsservers", $"name={adapter.Name}", "source=dhcp");
                if (!ipv4.Success) return ipv4;
                changed = true;
            }
            if (adapter.HasIpv6)
            {
                var ipv6 = await RunNetshAsync("interface", "ipv6", "set", "dnsservers", $"name={adapter.Name}", "source=dhcp");
                if (!ipv6.Success) return ipv6;
                changed = true;
            }
            return changed
                ? (true, $"Set DNS to automatic for {adapter.Name}.")
                : (false, "Selected adapter has no IPv4 or IPv6 stack to configure.");
        }

        if (adapter.HasIpv4 && !string.IsNullOrWhiteSpace(profile.Ipv4Primary))
        {
            var first = await RunNetshAsync(
                "interface", "ipv4", "set", "dnsservers",
                $"name={adapter.Name}",
                "source=static",
                $"address={profile.Ipv4Primary}",
                "register=primary",
                "validate=no");
            if (!first.Success) return first;

            if (!string.IsNullOrWhiteSpace(profile.Ipv4Secondary))
            {
                var second = await RunNetshAsync(
                    "interface", "ipv4", "add", "dnsservers",
                    $"name={adapter.Name}",
                    $"address={profile.Ipv4Secondary}",
                    "index=2",
                    "validate=no");
                if (!second.Success) return second;
            }
            changed = true;
            messages.Add("IPv4");
        }
        else if (adapter.HasIpv4)
        {
            messages.Add("IPv4 skipped: profile has no IPv4 DNS");
        }

        if (adapter.HasUsableIpv6 && !string.IsNullOrWhiteSpace(profile.Ipv6Primary))
        {
            var first = await RunNetshAsync(
                "interface", "ipv6", "set", "dnsservers",
                $"name={adapter.Name}",
                "source=static",
                $"address={profile.Ipv6Primary}",
                "validate=no");
            if (!first.Success) return first;

            if (!string.IsNullOrWhiteSpace(profile.Ipv6Secondary))
            {
                var second = await RunNetshAsync(
                    "interface", "ipv6", "add", "dnsservers",
                    $"name={adapter.Name}",
                    $"address={profile.Ipv6Secondary}",
                    "index=2",
                    "validate=no");
                if (!second.Success) return second;
            }
            changed = true;
            messages.Add("IPv6");
        }
        else if (adapter.HasIpv6)
        {
            messages.Add(adapter.HasUsableIpv6 ? "IPv6 skipped: profile has no IPv6 DNS" : "IPv6 skipped: no routable IPv6 address");
        }

        if (!changed)
            return (false, "No matching usable IP stack was found for that DNS profile.");

        return (true, $"Applied '{profile.Name}' to {adapter.Name}: {string.Join(", ", messages)}.");
    }

    public async Task<(bool Success, string Message)> FlushDnsAsync()
    {
        var result = await RunProcessAsync("ipconfig.exe", "/flushdns");
        return result.ExitCode == 0
            ? (true, "DNS resolver cache flushed.")
            : (false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private async Task<(bool Success, string Message)> RunNetshAsync(params string[] args)
    {
        var result = await RunProcessAsync("netsh.exe", args);
        if (result.ExitCode == 0) return (true, result.Output.Trim());
        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return (false, string.IsNullOrWhiteSpace(message) ? $"netsh exited with {result.ExitCode}." : message.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return (-1, string.Empty, "Could not start process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(waitTask, Task.Delay(ProcessTimeout)) != waitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-2, string.Empty, $"{fileName} timed out.");
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static bool IsUsableIpv6Address(IPAddress address) =>
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast &&
        !address.IsIPv6SiteLocal &&
        !IPAddress.IPv6Loopback.Equals(address) &&
        !IPAddress.IPv6None.Equals(address);
}

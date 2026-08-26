using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PowerDesk.Modules.DnsDesk.Models;

namespace PowerDesk.Modules.DnsDesk.Services;

/// <summary>A single netsh invocation planned by <see cref="DnsLogic.PlanApply"/>.</summary>
internal sealed record NetshStep(string Label, string[] Args);

/// <summary>
/// The pure result of planning a DNS profile change: which netsh calls to run, which stacks
/// they touch, and human-readable notes about stacks that were intentionally skipped.
/// </summary>
internal sealed class DnsApplyPlan
{
    public List<NetshStep> Steps { get; } = new();
    public List<string> AppliedStacks { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>Non-null when nothing can be applied; explains why.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Windows-free DNS logic so it can be unit tested: IPv6 usability, address validation,
/// list formatting and netsh command planning.
/// </summary>
internal static class DnsLogic
{
    /// <summary>
    /// True for an IPv6 unicast address that could plausibly reach a public resolver:
    /// global unicast (2000::/3) or unique-local (fc00::/7). Link-local, site-local (deprecated),
    /// multicast, loopback, unspecified and IPv4-mapped addresses are rejected.
    /// </summary>
    public static bool IsUsableIpv6Address(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
        if (address.IsIPv4MappedToIPv6) return false;
        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6None.Equals(address))
            return false;
        var first = address.GetAddressBytes()[0];
        var isGlobal = (first & 0xE0) == 0x20;   // 2000::/3
        var isUniqueLocal = (first & 0xFE) == 0xFC; // fc00::/7
        return isGlobal || isUniqueLocal;
    }

    /// <summary>True for an IPv6 gateway entry that represents a real next hop.</summary>
    public static bool IsIpv6Gateway(IPAddress? address) =>
        address is not null &&
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        !IPAddress.IPv6Any.Equals(address) &&
        !IPAddress.IPv6None.Equals(address);

    /// <summary>
    /// An adapter has a usable IPv6 route when it is up, owns a routable IPv6 unicast address
    /// and has an IPv6 default gateway. Adapters that only carry a link-local fe80:: address
    /// (the common "IPv6 enabled but not provisioned" case) are not usable, so IPv6 DNS is skipped.
    /// </summary>
    public static bool HasUsableIpv6(OperationalStatus status, IEnumerable<IPAddress> unicast, IEnumerable<IPAddress> gateways)
    {
        if (status != OperationalStatus.Up) return false;
        return unicast.Any(IsUsableIpv6Address) && gateways.Any(IsIpv6Gateway);
    }

    /// <summary>Distinct, ordered, comma-joined address list for one family, or "-" when empty.</summary>
    public static string FormatAddresses(IEnumerable<IPAddress> addresses, AddressFamily family)
    {
        var list = addresses
            .Where(a => a is not null && a.AddressFamily == family)
            .Select(a => a.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? "-" : string.Join(", ", list);
    }

    /// <summary>
    /// Strict parse of a DNS server address for the given family. Rejects shorthand IPv4
    /// ("1.1"), ports, zone ids, unspecified, multicast and broadcast addresses.
    /// </summary>
    public static bool TryParseAddress(string? text, AddressFamily family, out IPAddress? address, out string error)
    {
        address = null;
        error = string.Empty;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Address is empty.";
            return false;
        }
        if (family == AddressFamily.InterNetworkV6 && value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];

        if (value.Contains('%'))
        {
            error = $"'{value}' contains a zone index, which cannot be used for a DNS server.";
            return false;
        }

        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != family)
        {
            error = family == AddressFamily.InterNetwork
                ? $"'{value}' is not a valid IPv4 address."
                : $"'{value}' is not a valid IPv6 address.";
            return false;
        }

        if (family == AddressFamily.InterNetwork)
        {
            var parts = value.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 || !p.All(char.IsAsciiDigit)))
            {
                error = $"'{value}' is not a valid dotted IPv4 address.";
                return false;
            }
            if (IPAddress.Any.Equals(parsed) || IPAddress.Broadcast.Equals(parsed))
            {
                error = $"'{value}' cannot be used as a DNS server.";
                return false;
            }
            var first = parsed.GetAddressBytes()[0];
            if (first >= 224)
            {
                error = $"'{value}' is a multicast or reserved address.";
                return false;
            }
        }
        else
        {
            if (IPAddress.IPv6Any.Equals(parsed) || IPAddress.IPv6None.Equals(parsed) || parsed.IsIPv6Multicast || parsed.IsIPv4MappedToIPv6)
            {
                error = $"'{value}' cannot be used as a DNS server.";
                return false;
            }
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Validates every address in a static profile. Returns an empty list when the profile is
    /// usable. DHCP profiles are always valid. A profile with no addresses at all is invalid.
    /// </summary>
    public static IReadOnlyList<string> ValidateProfile(DnsProfile profile)
    {
        var errors = new List<string>();
        if (profile.UseDhcp) return errors;

        Check(profile.Ipv4Primary, AddressFamily.InterNetwork, "IPv4 primary");
        Check(profile.Ipv4Secondary, AddressFamily.InterNetwork, "IPv4 secondary");
        Check(profile.Ipv6Primary, AddressFamily.InterNetworkV6, "IPv6 primary");
        Check(profile.Ipv6Secondary, AddressFamily.InterNetworkV6, "IPv6 secondary");

        if (string.IsNullOrWhiteSpace(profile.Ipv4Primary) && !string.IsNullOrWhiteSpace(profile.Ipv4Secondary))
            errors.Add("IPv4 secondary requires an IPv4 primary address.");
        if (string.IsNullOrWhiteSpace(profile.Ipv6Primary) && !string.IsNullOrWhiteSpace(profile.Ipv6Secondary))
            errors.Add("IPv6 secondary requires an IPv6 primary address.");
        if (string.IsNullOrWhiteSpace(profile.Ipv4Primary) && string.IsNullOrWhiteSpace(profile.Ipv6Primary))
            errors.Add("Enter at least one primary DNS address.");
        if (!string.IsNullOrWhiteSpace(profile.Ipv4Primary) &&
            string.Equals(profile.Ipv4Primary.Trim(), profile.Ipv4Secondary.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("IPv4 primary and secondary must differ.");
        if (!string.IsNullOrWhiteSpace(profile.Ipv6Primary) &&
            string.Equals(profile.Ipv6Primary.Trim(), profile.Ipv6Secondary.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("IPv6 primary and secondary must differ.");
        return errors;

        void Check(string value, AddressFamily family, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!TryParseAddress(value, family, out _, out var error)) errors.Add($"{label}: {error}");
        }
    }

    /// <summary>Returns a copy of the profile with every address trimmed (and IPv6 brackets removed).</summary>
    public static DnsProfile Normalize(DnsProfile profile) => new()
    {
        Name = profile.Name,
        UseDhcp = profile.UseDhcp,
        IsCustom = profile.IsCustom,
        Ipv4Primary = Clean(profile.Ipv4Primary),
        Ipv4Secondary = Clean(profile.Ipv4Secondary),
        Ipv6Primary = Clean(profile.Ipv6Primary),
        Ipv6Secondary = Clean(profile.Ipv6Secondary),
    };

    private static string Clean(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.StartsWith('[') && v.EndsWith(']')) v = v[1..^1];
        return v;
    }

    /// <summary>
    /// Plans the netsh calls needed to apply <paramref name="profile"/> to <paramref name="adapter"/>.
    /// IPv6 is only configured when the adapter has a usable IPv6 route; the reason for any skipped
    /// stack is recorded in <see cref="DnsApplyPlan.Notes"/>. The adapter name is passed as a single
    /// "name=..." argument so names with spaces, quotes or unicode survive process quoting intact.
    /// </summary>
    public static DnsApplyPlan PlanApply(DnsAdapter adapter, DnsProfile profile)
    {
        var plan = new DnsApplyPlan();
        var name = $"name={adapter.Name}";

        if (profile.UseDhcp)
        {
            if (adapter.HasIpv4)
            {
                plan.Steps.Add(new NetshStep("IPv4 DNS to automatic",
                    new[] { "interface", "ipv4", "set", "dnsservers", name, "source=dhcp" }));
                plan.AppliedStacks.Add("IPv4");
            }
            if (adapter.HasIpv6)
            {
                plan.Steps.Add(new NetshStep("IPv6 DNS to automatic",
                    new[] { "interface", "ipv6", "set", "dnsservers", name, "source=dhcp" }));
                plan.AppliedStacks.Add("IPv6");
            }
            if (plan.Steps.Count == 0)
                plan.Error = $"'{adapter.Name}' has no IPv4 or IPv6 stack to configure.";
            return plan;
        }

        if (adapter.HasIpv4 && !string.IsNullOrWhiteSpace(profile.Ipv4Primary))
        {
            plan.Steps.Add(new NetshStep("IPv4 primary DNS",
                new[] { "interface", "ipv4", "set", "dnsservers", name, "source=static", $"address={profile.Ipv4Primary}", "register=primary", "validate=no" }));
            if (!string.IsNullOrWhiteSpace(profile.Ipv4Secondary))
                plan.Steps.Add(new NetshStep("IPv4 secondary DNS",
                    new[] { "interface", "ipv4", "add", "dnsservers", name, $"address={profile.Ipv4Secondary}", "index=2", "validate=no" }));
            plan.AppliedStacks.Add("IPv4");
        }
        else if (adapter.HasIpv4)
        {
            plan.Notes.Add("IPv4 skipped: profile has no IPv4 DNS");
        }

        if (adapter.HasUsableIpv6 && !string.IsNullOrWhiteSpace(profile.Ipv6Primary))
        {
            plan.Steps.Add(new NetshStep("IPv6 primary DNS",
                new[] { "interface", "ipv6", "set", "dnsservers", name, "source=static", $"address={profile.Ipv6Primary}", "register=primary", "validate=no" }));
            if (!string.IsNullOrWhiteSpace(profile.Ipv6Secondary))
                plan.Steps.Add(new NetshStep("IPv6 secondary DNS",
                    new[] { "interface", "ipv6", "add", "dnsservers", name, $"address={profile.Ipv6Secondary}", "index=2", "validate=no" }));
            plan.AppliedStacks.Add("IPv6");
        }
        else if (adapter.HasIpv6)
        {
            plan.Notes.Add(adapter.HasUsableIpv6
                ? "IPv6 skipped: profile has no IPv6 DNS"
                : "IPv6 skipped: adapter has no usable IPv6 route");
        }

        if (plan.Steps.Count == 0)
            plan.Error = $"'{adapter.Name}' has no usable IP stack matching the '{profile.Name}' profile.";
        return plan;
    }

    /// <summary>Success message summarising what was applied and what was skipped.</summary>
    public static string DescribeSuccess(DnsAdapter adapter, DnsProfile profile, DnsApplyPlan plan)
    {
        var stacks = string.Join(" + ", plan.AppliedStacks);
        if (profile.UseDhcp)
            return $"DNS set to automatic ({stacks}) on '{adapter.Name}'.";
        var notes = plan.Notes.Count == 0 ? string.Empty : $" ({string.Join("; ", plan.Notes)})";
        return $"Applied '{profile.Name}' to '{adapter.Name}': {stacks}.{notes}";
    }

    /// <summary>Picks the adapter to keep selected after a refresh: same id if still present, else the first.</summary>
    public static DnsAdapter? SelectAfterRefresh(IReadOnlyList<DnsAdapter> adapters, string? previousId)
    {
        if (adapters.Count == 0) return null;
        if (!string.IsNullOrEmpty(previousId))
        {
            var match = adapters.FirstOrDefault(a => string.Equals(a.Id, previousId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return adapters[0];
    }
}

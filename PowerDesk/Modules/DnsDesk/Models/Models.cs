using System.Net.NetworkInformation;

namespace PowerDesk.Modules.DnsDesk.Models;

public sealed class DnsAdapter
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public NetworkInterfaceType InterfaceType { get; init; }
    public OperationalStatus Status { get; init; }
    public string Ipv4DnsServers { get; init; } = string.Empty;
    public string Ipv6DnsServers { get; init; } = string.Empty;
    public string GatewayAddresses { get; init; } = string.Empty;
    public bool HasIpv4 { get; init; }
    public bool HasIpv6 { get; init; }
    public bool HasUsableIpv6 { get; init; }

    public bool IsUp => Status == OperationalStatus.Up;
    public string StatusLabel => Status.ToString();
    public string TypeLabel => InterfaceType.ToString();
    public string Ipv6StatusLabel => HasUsableIpv6
        ? "Usable"
        : !HasIpv6 ? "Unavailable"
        : IsUp ? "No route" : "Adapter down";
}

public sealed class DnsProfile
{
    public string Name { get; init; } = string.Empty;
    public string Ipv4Primary { get; init; } = string.Empty;
    public string Ipv4Secondary { get; init; } = string.Empty;
    public string Ipv6Primary { get; init; } = string.Empty;
    public string Ipv6Secondary { get; init; } = string.Empty;
    public bool UseDhcp { get; init; }

    public string ServerLabel => UseDhcp
        ? "Automatic"
        : $"{Ipv4Label} / {Ipv6Label}";

    public string Ipv4Label => string.IsNullOrWhiteSpace(Ipv4Primary)
        ? "-"
        : string.IsNullOrWhiteSpace(Ipv4Secondary) ? Ipv4Primary : $"{Ipv4Primary}, {Ipv4Secondary}";

    public string Ipv6Label => string.IsNullOrWhiteSpace(Ipv6Primary)
        ? "-"
        : string.IsNullOrWhiteSpace(Ipv6Secondary) ? Ipv6Primary : $"{Ipv6Primary}, {Ipv6Secondary}";

    public override string ToString() => Name;
}

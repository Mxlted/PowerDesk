using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PowerDesk.Core.Logging;
using PowerDesk.Core.Permissions;
using PowerDesk.Core.Services;
using PowerDesk.Modules.DnsDesk.Models;
using PowerDesk.Modules.DnsDesk.Services;
using PowerDesk.Modules.DnsDesk.ViewModels;

namespace PowerDesk.Tests.Modules;

public sealed class DnsDeskTests
{
    private sealed class NullLogger : ILogger
    {
        public string LogFilePath => string.Empty;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    private static readonly IPAddress Global = IPAddress.Parse("2001:db8::10");
    private static readonly IPAddress UniqueLocal = IPAddress.Parse("fd12:3456::1");
    private static readonly IPAddress LinkLocal = IPAddress.Parse("fe80::1");
    private static readonly IPAddress LinkLocalGateway = IPAddress.Parse("fe80::fffe");

    private static DnsAdapter Adapter(bool ipv4 = true, bool ipv6 = true, bool usableIpv6 = true, string name = "Ethernet 2") => new()
    {
        Id = "{ADAPTER}",
        Name = name,
        Status = OperationalStatus.Up,
        HasIpv4 = ipv4,
        HasIpv6 = ipv6,
        HasUsableIpv6 = usableIpv6,
    };

    private static readonly DnsProfile Cloudflare = new()
    {
        Name = "Cloudflare",
        Ipv4Primary = "1.1.1.1",
        Ipv4Secondary = "1.0.0.1",
        Ipv6Primary = "2606:4700:4700::1111",
        Ipv6Secondary = "2606:4700:4700::1001",
    };

    [Theory]
    [InlineData("2001:db8::10", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("::ffff:192.0.2.1", false)]
    [InlineData("192.0.2.1", false)]
    public void IsUsableIpv6Address_ClassifiesScopes(string text, bool expected)
    {
        Assert.Equal(expected, DnsLogic.IsUsableIpv6Address(IPAddress.Parse(text)));
    }

    [Fact]
    public void HasUsableIpv6_RequiresUpRoutableAddressAndGateway()
    {
        Assert.True(DnsLogic.HasUsableIpv6(OperationalStatus.Up, new[] { LinkLocal, Global }, new[] { LinkLocalGateway }));
        Assert.True(DnsLogic.HasUsableIpv6(OperationalStatus.Up, new[] { UniqueLocal }, new[] { LinkLocalGateway }));
        Assert.False(DnsLogic.HasUsableIpv6(OperationalStatus.Down, new[] { Global }, new[] { LinkLocalGateway }));
        Assert.False(DnsLogic.HasUsableIpv6(OperationalStatus.Up, new[] { LinkLocal }, new[] { LinkLocalGateway }), "link-local only is not routable");
        Assert.False(DnsLogic.HasUsableIpv6(OperationalStatus.Up, new[] { Global }, Array.Empty<IPAddress>()), "no gateway means no route");
        Assert.False(DnsLogic.HasUsableIpv6(OperationalStatus.Up, new[] { Global }, new[] { IPAddress.Parse("192.0.2.1") }), "IPv4 gateway does not count");
    }

    [Fact]
    public void FormatAddresses_FiltersFamilyAndDeduplicates()
    {
        var all = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"), Global };
        Assert.Equal("8.8.8.8, 8.8.4.4", DnsLogic.FormatAddresses(all, AddressFamily.InterNetwork));
        Assert.Equal("2001:db8::10", DnsLogic.FormatAddresses(all, AddressFamily.InterNetworkV6));
        Assert.Equal("-", DnsLogic.FormatAddresses(Array.Empty<IPAddress>(), AddressFamily.InterNetwork));
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData(" 9.9.9.9 ", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("1.1", false)]
    [InlineData("8.8.8.8:53", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("300.1.1.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("", false)]
    [InlineData("not an ip", false)]
    public void TryParseAddress_Ipv4(string text, bool expected)
    {
        var ok = DnsLogic.TryParseAddress(text, AddressFamily.InterNetwork, out var address, out var error);
        Assert.Equal(expected, ok);
        if (ok) Assert.Equal(AddressFamily.InterNetwork, address!.AddressFamily);
        else Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("[2001:4860:4860::8888]", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1%12", false)]
    [InlineData("::", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:1.1.1.1", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("", false)]
    public void TryParseAddress_Ipv6(string text, bool expected)
    {
        var ok = DnsLogic.TryParseAddress(text, AddressFamily.InterNetworkV6, out var address, out var error);
        Assert.Equal(expected, ok);
        if (ok) Assert.Equal(AddressFamily.InterNetworkV6, address!.AddressFamily);
        else Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ValidateProfile_AcceptsPresetsAndDhcp()
    {
        Assert.Empty(DnsLogic.ValidateProfile(Cloudflare));
        Assert.Empty(DnsLogic.ValidateProfile(new DnsProfile { Name = "Auto", UseDhcp = true }));
        Assert.Empty(DnsLogic.ValidateProfile(new DnsProfile { Name = "v4 only", Ipv4Primary = "9.9.9.9" }));
        Assert.Empty(DnsLogic.ValidateProfile(new DnsProfile { Name = "v6 only", Ipv6Primary = "2620:fe::fe" }));
    }

    [Fact]
    public void ValidateProfile_ReportsEveryProblem()
    {
        var empty = DnsLogic.ValidateProfile(new DnsProfile { Name = "Custom", IsCustom = true });
        Assert.Contains(empty, e => e.Contains("at least one primary", StringComparison.OrdinalIgnoreCase));

        var orphanSecondary = DnsLogic.ValidateProfile(new DnsProfile { Name = "x", Ipv4Secondary = "1.0.0.1" });
        Assert.Contains(orphanSecondary, e => e.Contains("requires an IPv4 primary", StringComparison.OrdinalIgnoreCase));

        var same = DnsLogic.ValidateProfile(new DnsProfile { Name = "x", Ipv4Primary = "1.1.1.1", Ipv4Secondary = "1.1.1.1" });
        Assert.Contains(same, e => e.Contains("must differ", StringComparison.OrdinalIgnoreCase));

        var bad = DnsLogic.ValidateProfile(new DnsProfile { Name = "x", Ipv4Primary = "1.1.1.1", Ipv6Primary = "garbage" });
        Assert.Contains(bad, e => e.StartsWith("IPv6 primary:", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_TrimsAndRemovesBrackets()
    {
        var normalized = DnsLogic.Normalize(new DnsProfile
        {
            Name = "Custom",
            IsCustom = true,
            Ipv4Primary = " 1.1.1.1 ",
            Ipv6Primary = "[2606:4700:4700::1111]",
        });
        Assert.Equal("1.1.1.1", normalized.Ipv4Primary);
        Assert.Equal("2606:4700:4700::1111", normalized.Ipv6Primary);
        Assert.Equal(string.Empty, normalized.Ipv4Secondary);
        Assert.True(normalized.IsCustom);
    }

    [Fact]
    public void PlanApply_Dhcp_ResetsEveryStackThatExists()
    {
        var plan = DnsLogic.PlanApply(Adapter(), new DnsProfile { Name = "Auto", UseDhcp = true });
        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, s => Assert.Contains("source=dhcp", s.Args));
        Assert.Equal(new[] { "IPv4", "IPv6" }, plan.AppliedStacks);

        var v4Only = DnsLogic.PlanApply(Adapter(ipv6: false, usableIpv6: false), new DnsProfile { Name = "Auto", UseDhcp = true });
        Assert.Single(v4Only.Steps);
        Assert.Equal("ipv4", v4Only.Steps[0].Args[1]);

        var none = DnsLogic.PlanApply(Adapter(ipv4: false, ipv6: false, usableIpv6: false), new DnsProfile { Name = "Auto", UseDhcp = true });
        Assert.NotNull(none.Error);
        Assert.Empty(none.Steps);
    }

    [Fact]
    public void PlanApply_Static_WithUsableIpv6_ConfiguresBothStacksInOrder()
    {
        var plan = DnsLogic.PlanApply(Adapter(), Cloudflare);
        Assert.Null(plan.Error);
        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal(new[] { "interface", "ipv4", "set", "dnsservers", "name=Ethernet 2", "source=static", "address=1.1.1.1", "register=primary", "validate=no" }, plan.Steps[0].Args);
        Assert.Equal(new[] { "interface", "ipv4", "add", "dnsservers", "name=Ethernet 2", "address=1.0.0.1", "index=2", "validate=no" }, plan.Steps[1].Args);
        Assert.Equal("ipv6", plan.Steps[2].Args[1]);
        Assert.Contains("address=2606:4700:4700::1111", plan.Steps[2].Args);
        Assert.Contains("index=2", plan.Steps[3].Args);
        Assert.Equal(new[] { "IPv4", "IPv6" }, plan.AppliedStacks);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void PlanApply_AdapterNameIsASingleArgumentEvenWithSpacesQuotesAndUnicode()
    {
        const string name = "Wi-Fi \"Büro\" 2";
        var plan = DnsLogic.PlanApply(Adapter(name: name), Cloudflare);
        Assert.All(plan.Steps, s => Assert.Contains($"name={name}", s.Args));
        Assert.All(plan.Steps, s => Assert.Equal(1, s.Args.Count(a => a.StartsWith("name=", StringComparison.Ordinal))));
    }

    [Fact]
    public void PlanApply_SkipsIpv6WhenAdapterHasNoUsableRoute()
    {
        var plan = DnsLogic.PlanApply(Adapter(usableIpv6: false), Cloudflare);
        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Steps.Count);
        Assert.All(plan.Steps, s => Assert.Equal("ipv4", s.Args[1]));
        Assert.Equal(new[] { "IPv4" }, plan.AppliedStacks);
        Assert.Contains(plan.Notes, n => n.Contains("no usable IPv6 route", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlanApply_NotesWhenProfileLacksAStack()
    {
        var v4Only = new DnsProfile { Name = "v4", Ipv4Primary = "9.9.9.9" };
        var plan = DnsLogic.PlanApply(Adapter(), v4Only);
        Assert.Single(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Contains("profile has no IPv6 DNS", StringComparison.OrdinalIgnoreCase));

        var noIpv4Adapter = Adapter(ipv4: false);
        var failed = DnsLogic.PlanApply(noIpv4Adapter, v4Only);
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Steps);
    }

    [Fact]
    public void DescribeSuccess_MentionsStacksAndNotes()
    {
        var plan = DnsLogic.PlanApply(Adapter(usableIpv6: false), Cloudflare);
        var text = DnsLogic.DescribeSuccess(Adapter(usableIpv6: false), Cloudflare, plan);
        Assert.Contains("Cloudflare", text);
        Assert.Contains("Ethernet 2", text);
        Assert.Contains("IPv4", text);
        Assert.Contains("IPv6 skipped", text);

        var dhcp = new DnsProfile { Name = "Auto", UseDhcp = true };
        var dhcpText = DnsLogic.DescribeSuccess(Adapter(), dhcp, DnsLogic.PlanApply(Adapter(), dhcp));
        Assert.Contains("automatic", dhcpText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectAfterRefresh_PrefersPreviousIdThenFirst()
    {
        var a = new DnsAdapter { Id = "{A}", Name = "A" };
        var b = new DnsAdapter { Id = "{B}", Name = "B" };
        var list = new List<DnsAdapter> { a, b };
        Assert.Same(b, DnsLogic.SelectAfterRefresh(list, "{b}"));
        Assert.Same(a, DnsLogic.SelectAfterRefresh(list, "{missing}"));
        Assert.Same(a, DnsLogic.SelectAfterRefresh(list, null));
        Assert.Null(DnsLogic.SelectAfterRefresh(new List<DnsAdapter>(), "{A}"));
    }

    [Fact]
    public void Ipv6StatusLabel_ExplainsWhyIpv6IsSkipped()
    {
        Assert.Equal("Usable", new DnsAdapter { HasIpv6 = true, HasUsableIpv6 = true, Status = OperationalStatus.Up }.Ipv6StatusLabel);
        Assert.Equal("No route", new DnsAdapter { HasIpv6 = true, HasUsableIpv6 = false, Status = OperationalStatus.Up }.Ipv6StatusLabel);
        Assert.Equal("Adapter down", new DnsAdapter { HasIpv6 = true, HasUsableIpv6 = false, Status = OperationalStatus.Down }.Ipv6StatusLabel);
        Assert.Equal("Unavailable", new DnsAdapter { HasIpv6 = false, Status = OperationalStatus.Up }.Ipv6StatusLabel);
    }

    [Fact]
    public void ViewModel_CustomProfileBuildsFromTypedAddresses()
    {
        var vm = new DnsDeskViewModel(new NullLogger(), new StatusService(), new RecentActionsService(), new PermissionService());
        Assert.False(vm.IsCustomProfile);

        vm.SelectedProfile = vm.Profiles.Single(p => p.IsCustom);
        vm.CustomIpv4Primary = " 9.9.9.9 ";
        vm.CustomIpv6Primary = "[2620:fe::fe]";
        Assert.True(vm.IsCustomProfile);

        var effective = vm.BuildEffectiveProfile();
        Assert.Equal("9.9.9.9", effective.Ipv4Primary);
        Assert.Equal("2620:fe::fe", effective.Ipv6Primary);
        Assert.Empty(DnsLogic.ValidateProfile(effective));

        vm.SelectedProfile = vm.Profiles.First(p => p.Name == "Google");
        Assert.Equal("8.8.8.8", vm.BuildEffectiveProfile().Ipv4Primary);
    }

    [Fact]
    public void ViewModel_ApplyIsDisabledWithoutAnAdapter()
    {
        var vm = new DnsDeskViewModel(new NullLogger(), new StatusService(), new RecentActionsService(), new PermissionService());
        Assert.Null(vm.SelectedAdapter);
        Assert.False(vm.ApplyProfileCommand.CanExecute(null));
        Assert.True(vm.FlushDnsCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }
}

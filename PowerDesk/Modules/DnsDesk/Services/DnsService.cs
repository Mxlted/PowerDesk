using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerDesk.Core.Logging;
using PowerDesk.Modules.DnsDesk.Models;

namespace PowerDesk.Modules.DnsDesk.Services;

public sealed class DnsService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    // netsh/ipconfig write their (possibly localized) output in the OEM code page, not UTF-8.
    // Decoding with the wrong encoding turns failure text into mojibake, so resolve it once.
    private static readonly Encoding? ConsoleEncoding = ResolveConsoleEncoding();

    private readonly ILogger _log;

    public DnsService(ILogger log)
    {
        _log = log;
    }

    /// <summary>
    /// Enumerates physical/virtual adapters (loopback and tunnels excluded) with their DNS servers,
    /// gateways and IPv6 usability. Safe to call from a background thread.
    /// </summary>
    public IReadOnlyList<DnsAdapter> GetAdapters()
    {
        var adapters = new List<DnsAdapter>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex)
        {
            _log.Error("DNS adapter enumeration", ex);
            throw;
        }

        foreach (var adapter in interfaces)
        {
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (string.IsNullOrWhiteSpace(adapter.Name))
                continue;

            IPInterfaceProperties props;
            try { props = adapter.GetIPProperties(); }
            catch (Exception ex)
            {
                _log.Error($"DNS adapter properties: {adapter.Name}", ex);
                continue;
            }

            bool supportsIpv4 = false, supportsIpv6 = false;
            try { supportsIpv4 = adapter.Supports(NetworkInterfaceComponent.IPv4); } catch { }
            try { supportsIpv6 = adapter.Supports(NetworkInterfaceComponent.IPv6); } catch { }

            var dns = SafeList(() => props.DnsAddresses);
            var unicast = SafeList(() => props.UnicastAddresses).Select(a => a.Address).Where(a => a is not null).ToList();
            var gateways = SafeList(() => props.GatewayAddresses).Select(g => g.Address).Where(a => a is not null).ToList();

            adapters.Add(new DnsAdapter
            {
                Id = adapter.Id,
                Name = adapter.Name,
                Description = adapter.Description,
                InterfaceType = adapter.NetworkInterfaceType,
                Status = adapter.OperationalStatus,
                Ipv4DnsServers = DnsLogic.FormatAddresses(dns, AddressFamily.InterNetwork),
                Ipv6DnsServers = DnsLogic.FormatAddresses(dns, AddressFamily.InterNetworkV6),
                GatewayAddresses = FormatGateways(gateways),
                HasIpv4 = supportsIpv4,
                HasIpv6 = supportsIpv6,
                HasUsableIpv6 = supportsIpv6 && DnsLogic.HasUsableIpv6(adapter.OperationalStatus, unicast, gateways),
            });
        }

        return adapters
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Applies a DNS profile via netsh. Runs each planned step in order and stops at the first
    /// failure, returning the exact netsh text so the user can see what went wrong.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyProfileAsync(DnsAdapter adapter, DnsProfile profile)
    {
        var plan = DnsLogic.PlanApply(adapter, profile);
        if (plan.Error is not null) return (false, plan.Error);

        var done = new List<string>();
        foreach (var step in plan.Steps)
        {
            var result = await RunNetshAsync(step.Args);
            if (!result.Success)
            {
                var prefix = done.Count == 0 ? string.Empty : $" (already applied: {string.Join(", ", done)})";
                return (false, $"{step.Label} failed{prefix}: {result.Message}");
            }
            done.Add(step.Label);
        }

        return (true, DnsLogic.DescribeSuccess(adapter, profile, plan));
    }

    public async Task<(bool Success, string Message)> FlushDnsAsync()
    {
        var result = await RunProcessAsync(SystemTool("ipconfig.exe"), "/flushdns");
        if (result.ExitCode == 0) return (true, "DNS resolver cache flushed.");
        var message = FirstNonEmpty(result.Error, result.Output);
        return (false, string.IsNullOrWhiteSpace(message) ? $"ipconfig exited with code {result.ExitCode}." : message);
    }

    private async Task<(bool Success, string Message)> RunNetshAsync(params string[] args)
    {
        var result = await RunProcessAsync(SystemTool("netsh.exe"), args);
        if (result.ExitCode == 0) return (true, result.Output.Trim());
        var message = FirstNonEmpty(result.Error, result.Output);
        return (false, string.IsNullOrWhiteSpace(message) ? $"netsh exited with code {result.ExitCode}." : message);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex)
        {
            _log.Error($"Start {fileName}", ex);
            return (-1, string.Empty, $"Could not start {Path.GetFileName(fileName)}: {ex.Message}");
        }
        if (process is null) return (-1, string.Empty, $"Could not start {Path.GetFileName(fileName)}.");

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(ProcessTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _log.Warn($"{Path.GetFileName(fileName)} {string.Join(' ', args)} timed out after {ProcessTimeout.TotalSeconds:0}s.");
                return (-2, string.Empty, $"{Path.GetFileName(fileName)} did not finish within {ProcessTimeout.TotalSeconds:0} seconds.");
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                _log.Warn($"{Path.GetFileName(fileName)} {string.Join(' ', args)} exited {process.ExitCode}: {FirstNonEmpty(error, output)}");
            return (process.ExitCode, output, error);
        }
    }

    /// <summary>Full path under the Windows system directory so a broken PATH cannot hide the tool.</summary>
    private static string SystemTool(string exe)
    {
        try
        {
            var candidate = Path.Combine(Environment.SystemDirectory, exe);
            if (File.Exists(candidate)) return candidate;
        }
        catch { }
        return exe;
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? CollapseWhitespace(a) : CollapseWhitespace(b);

    private static string CollapseWhitespace(string text) =>
        string.Join(" ", (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string FormatGateways(IEnumerable<IPAddress> gateways)
    {
        var list = gateways
            .Where(g => !IPAddress.Any.Equals(g) && !IPAddress.IPv6Any.Equals(g))
            .Select(g => g.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? "-" : string.Join(", ", list);
    }

    private static List<T> SafeList<T>(Func<IEnumerable<T>> get)
    {
        try { return get().ToList(); }
        catch { return new List<T>(); }
    }

    private static Encoding? ResolveConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return codePage > 0 ? Encoding.GetEncoding(codePage) : null;
        }
        catch
        {
            return null;
        }
    }
}

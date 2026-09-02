using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RTSPCameraConfigurator;

public sealed record DiscoveredCamera(string Address, string Description)
{
    public override string ToString() => string.IsNullOrEmpty(Description) ? Address : $"{Address}  -  {Description}";
}

/// <summary>
/// Finds cameras by sweeping a /24 for an open HTTP port and then checking the
/// login page for the firmware's signature.
///
/// ICMP is deliberately not used: these cameras answer TCP reliably but ping is
/// often filtered, and a stale ARP cache makes a ping sweep miss devices that a
/// direct connect finds.
/// </summary>
public static class Discovery
{
    /// <summary>
    /// Shared deliberately. A sweep probes 254 hosts; one HttpClient each churned
    /// sockets and paid a fresh handshake per host. The per-request deadline comes
    /// from a linked token instead of HttpClient.Timeout.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Local /24 prefixes ("192.168.1") for every up, non-loopback IPv4 interface.</summary>
    public static List<string> LocalSubnets()
    {
        var subnets = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var prefix = Prefix24(info.Address.ToString());
                if (prefix is not null && !subnets.Contains(prefix))
                    subnets.Add(prefix);
            }
        }

        return subnets;
    }

    private static string? Prefix24(string address)
    {
        var parts = address.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : null;
    }

    /// <summary>
    /// Scans <paramref name="subnet"/> (e.g. "192.168.1") plus any configured
    /// factory-default addresses, reporting each hit as it is confirmed.
    /// </summary>
    /// <param name="onSkipped">
    /// Called for every host that answered on the probe port but was not accepted, with
    /// the reason. A camera that is missing from the list is otherwise indistinguishable
    /// from one that is not on the network at all, which makes the failure impossible to
    /// diagnose from the outside.
    /// </param>
    public static async Task<List<DiscoveredCamera>> ScanAsync(
        string subnet,
        DiscoverySpec spec,
        IProgress<double>? progress = null,
        Func<DiscoveredCamera, Task>? onFound = null,
        Action<string, string>? onSkipped = null,
        CancellationToken ct = default)
    {
        var targets = new List<string>();
        for (var host = 1; host <= 254; host++)
            targets.Add($"{subnet}.{host}");

        foreach (var extra in spec.DefaultAddresses)
            if (!targets.Contains(extra))
                targets.Add(extra);

        var found = new List<DiscoveredCamera>();
        var gate = new SemaphoreSlim(Math.Max(1, spec.MaxParallel));
        var completed = 0;
        var sync = new object();

        var tasks = targets.Select(async address =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var camera = await ProbeAsync(address, spec, onSkipped, ct);
                if (camera is not null)
                {
                    lock (sync) found.Add(camera);

                    // Awaited here so a camera reaches the list the moment it
                    // answers, rather than after the whole /24 has been swept.
                    if (onFound is not null) await onFound(camera);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* an unreachable host is the normal case, not an error */ }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report((double)done / targets.Count);
            }
        });

        await Task.WhenAll(tasks);

        found.Sort((a, b) => CompareAddresses(a.Address, b.Address));
        return found;
    }

    /// <summary>True once something is listening on the probe port at this address.</summary>
    public static Task<bool> RespondsAsync(string address, DiscoverySpec spec, CancellationToken ct = default) =>
        IsPortOpenAsync(address, spec.ProbePort, spec.ConnectTimeoutMs, ct);

    /// <summary>
    /// The budget for a second attempt at the login page. The first attempt uses the
    /// configured short one so that a subnet full of printers, switches and NASes stays
    /// cheap to sweep. Only a host that actually accepted the connection - a handful per
    /// /24 - earns the generous retry, because a camera busy serving video can easily
    /// miss a 1.2 s window, and losing it there made it vanish from the list with no
    /// clue as to why.
    /// </summary>
    private static int RetryPageTimeoutMs(DiscoverySpec spec) =>
        Math.Max(5000, spec.PageTimeoutMs * 4);

    private static async Task<DiscoveredCamera?> ProbeAsync(
        string address, DiscoverySpec spec, Action<string, string>? onSkipped, CancellationToken ct)
    {
        if (!await IsPortOpenAsync(address, spec.ProbePort, spec.ConnectTimeoutMs, ct))
            return null;

        var (page, error) = await FetchLoginPageAsync(address, spec, spec.PageTimeoutMs, ct);

        // Something is listening, so one slow reply is not enough to write it off.
        if (page is null)
            (page, error) = await FetchLoginPageAsync(address, spec, RetryPageTimeoutMs(spec), ct);

        if (page is null)
        {
            onSkipped?.Invoke(address, $"port {spec.ProbePort} open, but no login page ({error})");
            return null;
        }

        // Compare with whitespace collapsed so a firmware that formats the literal
        // slightly differently still matches.
        var flat = Normalise(page);
        var markers = spec.AllSignatures().ToList();

        if (!markers.Any(m => flat.Contains(Normalise(m), StringComparison.OrdinalIgnoreCase)))
        {
            onSkipped?.Invoke(address,
                $"port {spec.ProbePort} open, but {spec.LoginPath} carried none of " +
                $"{string.Join(" / ", markers)} ({page.Length} bytes)");
            return null;
        }

        return new DiscoveredCamera(address, "camera");
    }

    private static async Task<(string? Page, string Error)> FetchLoginPageAsync(
        string address, DiscoverySpec spec, int timeoutMs, CancellationToken ct)
    {
        var budget = Math.Max(250, timeoutMs);

        try
        {
            using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pageTimeout.CancelAfter(TimeSpan.FromMilliseconds(budget));
            // Read bytes rather than GetStringAsync: this firmware family sends a
            // quoted charset ('utf-8') that .NET refuses to interpret.
            using var res = await Http.GetAsync(
                $"http://{address}:{spec.ProbePort}{spec.LoginPath}", pageTimeout.Token);

            return (System.Text.Encoding.UTF8.GetString(
                await res.Content.ReadAsByteArrayAsync(pageTimeout.Token)), "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (null, $"no reply within {budget} ms");
        }
        catch (Exception ex)
        {
            return (null, ex.GetBaseException().Message);
        }
    }

    private static string Normalise(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static async Task<bool> IsPortOpenAsync(string address, int port, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await client.ConnectAsync(ip, port, timeout.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own connect timeout
        }
        catch
        {
            return false;
        }
    }

    private static int CompareAddresses(string a, string b)
    {
        if (IPAddress.TryParse(a, out var x) && IPAddress.TryParse(b, out var y))
        {
            var bx = x.GetAddressBytes();
            var by = y.GetAddressBytes();
            for (var i = 0; i < bx.Length; i++)
            {
                var cmp = bx[i].CompareTo(by[i]);
                if (cmp != 0) return cmp;
            }
            return 0;
        }
        return string.CompareOrdinal(a, b);
    }
}

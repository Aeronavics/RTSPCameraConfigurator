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
    public static async Task<List<DiscoveredCamera>> ScanAsync(
        string subnet,
        DiscoverySpec spec,
        IProgress<double>? progress = null,
        Action<DiscoveredCamera>? onFound = null,
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
                var camera = await ProbeAsync(address, spec, ct);
                if (camera is not null)
                {
                    lock (sync) found.Add(camera);
                    onFound?.Invoke(camera);
                }
            }
            catch (OperationCanceledException) { throw; }
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

    private static async Task<DiscoveredCamera?> ProbeAsync(string address, DiscoverySpec spec, CancellationToken ct)
    {
        if (!await IsPortOpenAsync(address, spec.ProbePort, spec.ConnectTimeoutMs, ct))
            return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        string page;
        try
        {
            // Read bytes rather than GetStringAsync: this firmware family sends a
            // quoted charset ('utf-8') that .NET refuses to interpret.
            using var res = await http.GetAsync($"http://{address}:{spec.ProbePort}{spec.LoginPath}", ct);
            page = System.Text.Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync(ct));
        }
        catch
        {
            return null;
        }

        // Compare with whitespace collapsed so a firmware that formats the literal
        // slightly differently still matches.
        if (!Normalise(page).Contains(Normalise(spec.Signature), StringComparison.OrdinalIgnoreCase))
            return null;

        return new DiscoveredCamera(address, "camera");
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

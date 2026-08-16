using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RtspCameraSetup;

/// <summary>
/// Works out whether the configured subnets are reachable from this machine, and if
/// not, temporarily gives an adapter an address on them so discovery can see them.
///
/// Everything it adds is recorded and removed again when the app closes. The record
/// is also written to disk, so an address left behind by a crash is cleaned up on the
/// next run rather than quietly accumulating on the adapter.
///
/// Adding an address needs administrator rights, so the netsh calls are made through
/// a single elevated helper - one UAC prompt covers all of them, and nothing changes
/// without the user agreeing to it.
/// </summary>
public sealed class NetworkScope
{
    private sealed record Added(string InterfaceAlias, string Address, string Mask);

    private readonly List<Added> _added = new();
    private readonly string _journalPath;

    public NetworkScope()
    {
        _journalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CameraSetup", "temporary-addresses.json");
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Subnets ("192.168.5") with no local IPv4 address on them.</summary>
    public static List<string> Unreachable(IEnumerable<string> subnets)
    {
        var locals = LocalAddresses();

        return subnets
            .Where(subnet => !locals.Any(a => Prefix24(a) == subnet))
            .ToList();
    }

    private static List<string> LocalAddresses()
    {
        var result = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var text = info.Address.ToString();
                // Ignore link-local autoconfiguration: it means "no usable address".
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                result.Add(text);
            }
        }

        return result;
    }

    private static string? Prefix24(string address)
    {
        var parts = address.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : null;
    }

    /// <summary>
    /// The adapter to borrow. Prefers the one already carrying the most real IPv4
    /// addresses - on a machine wired to several camera networks that is the one
    /// physically attached to them.
    /// </summary>
    public static string? PreferredInterface(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new
            {
                n.Name,
                Count = n.GetIPProperties().UnicastAddresses.Count(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// Finds a host number in the subnet that nothing answers on, so the borrowed
    /// address cannot collide with a camera or another machine.
    /// </summary>
    public static string? FreeAddressIn(string subnet, int firstHost, int lastHost)
    {
        using var ping = new Ping();

        for (var host = lastHost; host >= firstHost; host--)
        {
            var candidate = $"{subnet}.{host}";

            try
            {
                var reply = ping.Send(candidate, 250);
                if (reply.Status == IPStatus.Success) continue;
            }
            catch (PingException)
            {
                // Unreachable is exactly what we want.
            }

            // Ping is often filtered, so also make sure nothing answers a TCP connect.
            if (!AnswersTcp(candidate)) return candidate;
        }

        return null;
    }

    private static bool AnswersTcp(string address)
    {
        foreach (var port in new[] { 80, 554, 443 })
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.BeginConnect(IPAddress.Parse(address), port, null, null);
                if (connect.AsyncWaitHandle.WaitOne(200) && client.Connected) return true;
            }
            catch
            {
                // Nothing listening is the expected case.
            }
        }

        return false;
    }

    /// <summary>
    /// Adds an address on each requested subnet, in one elevated batch. Returns the
    /// subnets actually covered.
    /// </summary>
    public List<string> AddTemporaryAddresses(IEnumerable<string> subnets, string interfaceAlias,
        int firstHost, int lastHost, out string? error)
    {
        error = null;
        var covered = new List<string>();
        var commands = new List<string>();
        var planned = new List<Added>();

        foreach (var subnet in subnets)
        {
            var address = FreeAddressIn(subnet, firstHost, lastHost);
            if (address is null) continue;

            planned.Add(new Added(interfaceAlias, address, "255.255.255.0"));
            commands.Add($"netsh interface ipv4 add address name=\"{interfaceAlias}\" " +
                         $"address={address} mask=255.255.255.0");
            covered.Add(subnet);
        }

        if (commands.Count == 0)
        {
            error = "no free address could be found on those subnets";
            return covered;
        }

        // Record BEFORE applying: if the app dies mid-change, the next run still knows
        // what to clean up.
        _added.AddRange(planned);
        WriteJournal();

        if (!RunElevated(commands, out error))
        {
            _added.RemoveAll(planned.Contains);
            WriteJournal();
            covered.Clear();
        }

        return covered;
    }

    /// <summary>
    /// Gives back every address that was borrowed.
    ///
    /// Only entries that have actually disappeared from the adapter are dropped from
    /// the journal. Clearing it unconditionally - as this used to - strands the
    /// address on the adapter with no record of it whenever the delete fails or
    /// elevation is declined, so the next run has nothing to clean up.
    /// </summary>
    public void RemoveTemporaryAddresses()
    {
        if (_added.Count == 0) return;

        var commands = _added
            .Select(a => $"netsh interface ipv4 delete address name=\"{a.InterfaceAlias}\" address={a.Address}")
            .ToList();

        RunElevated(commands, out _);

        var present = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _added.RemoveAll(a => !present.Contains(a.Address));

        WriteJournal();
    }

    /// <summary>Addresses this run borrowed that are still on the adapter.</summary>
    public IReadOnlyList<string> Stranded()
    {
        var present = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _added.Where(a => present.Contains(a.Address)).Select(a => a.Address).ToList();
    }

    /// <summary>Removes anything a previous run left behind, e.g. after a crash.</summary>
    public int CleanUpLeftovers()
    {
        List<Added>? leftovers;

        try
        {
            if (!File.Exists(_journalPath)) return 0;
            leftovers = JsonSerializer.Deserialize<List<Added>>(File.ReadAllText(_journalPath));
        }
        catch
        {
            return 0;
        }

        if (leftovers is null || leftovers.Count == 0) return 0;

        // Only remove addresses that are actually still present.
        var present = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = leftovers.Where(a => present.Contains(a.Address)).ToList();

        if (stale.Count == 0)
        {
            _added.Clear();
            WriteJournal();
            return 0;
        }

        RunElevated(stale.Select(a =>
            $"netsh interface ipv4 delete address name=\"{a.InterfaceAlias}\" address={a.Address}").ToList(), out _);

        // Keep anything the delete did not actually shift, so a later run tries again
        // rather than losing track of it.
        var stillPresent = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _added.Clear();
        _added.AddRange(stale.Where(a => stillPresent.Contains(a.Address)));
        WriteJournal();

        return stale.Count - _added.Count;
    }

    private void WriteJournal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.WriteAllText(_journalPath, JsonSerializer.Serialize(_added));
        }
        catch
        {
            // Best effort; losing the journal only costs a manual tidy-up.
        }
    }

    /// <summary>
    /// Runs the netsh commands as administrator. One prompt covers the whole batch;
    /// when already elevated, no prompt appears at all.
    /// </summary>
    private static bool RunElevated(IReadOnlyList<string> commands, out string? error)
    {
        error = null;

        var script = new StringBuilder();
        foreach (var command in commands) script.Append(command).Append(" & ");
        script.Append("exit /b 0");

        var psi = new ProcessStartInfo("cmd.exe", $"/c {script}")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (!IsElevated) psi.Verb = "runas"; // triggers the UAC prompt

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "could not start the elevated helper";
                return false;
            }

            process.WaitForExit(20000);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "elevation was declined";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

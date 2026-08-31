using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RTSPCameraConfigurator;

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
        _journalPath = AppData.File("temporary-addresses.json");
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
            return covered;
        }

        // Trust the adapter, not the helper. netsh has been seen to report success
        // while adding nothing, and claiming a subnet is searchable when it is not
        // sends the user hunting for a camera that could never have answered.
        var missing = planned.Where(a => !HasAddress(a.Address)).ToList();

        if (missing.Count > 0)
        {
            foreach (var gone in missing)
            {
                _added.Remove(gone);
                var subnet = Prefix24(gone.Address);
                if (subnet is not null) covered.Remove(subnet);
            }

            WriteJournal();

            error ??= $"the address was accepted but did not appear on '{interfaceAlias}' " +
                      $"({string.Join(", ", missing.Select(m => m.Address))})";
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
    ///
    /// Elevation needs ShellExecute, which cannot redirect output - so the commands go
    /// into a temporary script that logs to a file and returns the first failure's
    /// exit code. The previous version chained the commands with "&amp;" and ended with
    /// "exit /b 0", then ignored the exit code entirely: every failure looked like
    /// success, so the app reported "Borrowed an address ..." while nothing had been
    /// added to the adapter.
    /// </summary>
    private static bool RunElevated(IReadOnlyList<string> commands, out string? error)
    {
        error = null;
        if (commands.Count == 0) return true;

        var stamp = Guid.NewGuid().ToString("N");
        var script = Path.Combine(Path.GetTempPath(), $"rtspcam-netsh-{stamp}.cmd");
        var log = Path.Combine(Path.GetTempPath(), $"rtspcam-netsh-{stamp}.log");

        try
        {
            var text = new StringBuilder();
            text.AppendLine("@echo off");

            foreach (var command in commands)
            {
                text.AppendLine($"{command} >> \"{log}\" 2>&1");
                // Stop at the first failure rather than pressing on and reporting the
                // last command's result.
                text.AppendLine("if errorlevel 1 exit /b 1");
            }

            text.AppendLine("exit /b 0");
            File.WriteAllText(script, text.ToString());

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!IsElevated) psi.Verb = "runas"; // triggers the UAC prompt

            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "could not start the elevated helper";
                return false;
            }

            if (!process.WaitForExit(20000))
            {
                error = "the elevated helper did not finish in time";
                return false;
            }

            if (process.ExitCode == 0) return true;

            error = ReadLog(log) ?? $"the elevated helper failed (exit code {process.ExitCode})";
            return false;
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
        finally
        {
            TryDelete(script);
            TryDelete(log);
        }
    }

    /// <summary>netsh's own words are far more useful than an exit code.</summary>
    private static string? ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            return lines.Count == 0 ? null : string.Join(" ", lines);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }

    /// <summary>
    /// Whether the adapter really carries this address now.
    ///
    /// The authoritative check: netsh can report success and still leave nothing
    /// behind, so what the app tells the user is based on the adapter's own state
    /// rather than on the helper's exit code.
    /// </summary>
    private static bool HasAddress(string address) =>
        LocalAddresses().Any(a => string.Equals(a, address, StringComparison.Ordinal));
}

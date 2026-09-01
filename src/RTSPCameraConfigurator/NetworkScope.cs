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
    /// <summary>
    /// One borrowed address. <paramref name="RestoreDhcp"/> records that the adapter
    /// was on DHCP before this address was added: "netsh add address" converts a DHCP
    /// interface to static, and deleting the address again does NOT undo that, so
    /// without this the adapter is left statically configured for good.
    /// </summary>
    private sealed record Added(string InterfaceAlias, string Address, string Mask, bool RestoreDhcp = false);

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

        // Read this BEFORE the first address is added, because adding one is what
        // flips the adapter to static.
        var wasDhcp = IsDhcpEnabled(interfaceAlias);

        foreach (var subnet in subnets)
        {
            var address = FreeAddressIn(subnet, firstHost, lastHost);
            if (address is null) continue;

            planned.Add(new Added(interfaceAlias, address, "255.255.255.0", wasDhcp));
            commands.Add($"netsh interface ipv4 add address name=\"{interfaceAlias}\" " +
                         $"address={address} mask=255.255.255.0");
            covered.Add(subnet);
        }

        if (commands.Count == 0)
        {
            error = "no free address could be found on those subnets";
            return covered;
        }

        // Adding a static address to a DHCP adapter converts it to static, and the
        // address DHCP had given it is DISCARDED - which silently takes the machine
        // off whichever subnet it was already reachable on. Pin that address
        // explicitly first, so borrowing the others costs nothing.
        if (wasDhcp && CurrentIPv4(interfaceAlias) is { } held)
        {
            var pin = $"netsh interface ipv4 set address name=\"{interfaceAlias}\" " +
                      $"static {held.Address} {held.Mask}";

            if (held.Gateway is not null) pin += $" {held.Gateway}";

            commands.Insert(0, pin);
        }

        // Record BEFORE applying: if the app dies mid-change, the next run still knows
        // what to clean up.
        _added.AddRange(planned);
        WriteJournal();

        // One elevation for the whole session. The helper applies the addresses,
        // reports back, then waits for this process to exit and reverts on its own -
        // so closing the app needs no second UAC prompt. It also means a crash still
        // gets the adapter back, which asking again on exit never could.
        if (!StartSessionHelper(commands, RestoreCommands(planned).ToList(), out error))
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
    /// <summary>
    /// Hands the borrowed addresses back.
    ///
    /// When the session helper is still alive it already holds the revert commands and
    /// runs them as soon as this process exits, so there is nothing to do here and no
    /// second UAC prompt. Only when there is no helper - a journal left by an earlier
    /// run, or a helper that died - does this elevate to clean up itself.
    /// </summary>
    public void RemoveTemporaryAddresses()
    {
        if (_added.Count == 0) return;

        if (HelperIsRunning)
        {
            // Nothing to elevate for. The addresses are still on the adapter at this
            // moment by design; the helper removes them a second from now.
            return;
        }

        var commands = _added
            .Select(a => $"netsh interface ipv4 delete address name=\"{a.InterfaceAlias}\" address={a.Address}")
            .ToList();

        commands.AddRange(RestoreCommands(_added));

        RunElevated(commands, out _);

        var present = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _added.RemoveAll(a => !present.Contains(a.Address));

        WriteJournal();
    }

    private Process? _helper;

    private bool HelperIsRunning
    {
        get
        {
            try { return _helper is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Runs one elevated helper for the life of the app: apply, report, wait for this
    /// process to end, revert.
    ///
    /// The wait is on this process's id, so the revert happens however the app ends -
    /// including a crash, where prompting for elevation would not have been possible
    /// at all. The helper cleans up its own temporary files afterwards.
    /// </summary>
    private bool StartSessionHelper(IReadOnlyList<string> apply, IReadOnlyList<string> revert, out string? error)
    {
        error = null;

        var stamp = Guid.NewGuid().ToString("N");
        var script = Path.Combine(Path.GetTempPath(), $"rtspcam-session-{stamp}.cmd");
        var status = Path.Combine(Path.GetTempPath(), $"rtspcam-session-{stamp}.status");
        var log = Path.Combine(Path.GetTempPath(), $"rtspcam-session-{stamp}.log");

        try
        {
            var text = new StringBuilder();
            text.AppendLine("@echo off");
            text.AppendLine("set RC=0");

            foreach (var command in apply)
            {
                text.AppendLine($"if \"%RC%\"==\"0\" ({command} >> \"{log}\" 2>&1) else rem");
                text.AppendLine("if errorlevel 1 set RC=1");
            }

            // Tell the app how the apply went, before settling in to wait.
            text.AppendLine($"echo %RC%> \"{status}\"");
            text.AppendLine("if not \"%RC%\"==\"0\" goto cleanup");

            // Block on the process handle rather than polling. A tasklist/find loop
            // was tried first and reverted immediately: errorlevel through a pipe is
            // not reliable here, and "timeout" fails outright when stdin is
            // redirected, which it is for a hidden helper.
            text.AppendLine($"powershell -NoProfile -NonInteractive -Command \"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue\"");

            foreach (var command in revert)
                text.AppendLine($"{command} >> \"{log}\" 2>&1");

            text.AppendLine(":cleanup");
            text.AppendLine($"del /q \"{status}\" >nul 2>&1");
            text.AppendLine($"del /q \"{log}\" >nul 2>&1");
            // Delete the script last, and detached, since it is still executing.
            text.AppendLine($"start \"\" /b cmd /c \"timeout /t 2 /nobreak >nul & del /q \"\"{script}\"\"\"");
            text.AppendLine("exit /b %RC%");

            File.WriteAllText(script, text.ToString());

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!IsElevated) psi.Verb = "runas";

            _helper = Process.Start(psi);
            if (_helper is null)
            {
                error = "could not start the elevated helper";
                return false;
            }

            // Wait for the apply phase only - the helper stays alive after it.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(status))
                {
                    var code = File.ReadAllText(status).Trim();
                    if (code.StartsWith("0", StringComparison.Ordinal)) return true;

                    error = ReadLog(log) ?? "the elevated helper could not add the address";
                    return false;
                }

                if (HelperIsRunning is false)
                {
                    error = ReadLog(log) ?? "the elevated helper stopped before it applied anything";
                    return false;
                }

                Thread.Sleep(150);
            }

            error = "the elevated helper did not finish in time";
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "elevation was declined";
            TryDelete(script);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(script);
            return false;
        }
    }

    /// <summary>
    /// Hands each adapter that was on DHCP back to DHCP, once per adapter and only
    /// after its borrowed addresses have been deleted. DNS goes back too: netsh moves
    /// that to static alongside the address.
    /// </summary>
    private static IEnumerable<string> RestoreCommands(IEnumerable<Added> entries) =>
        entries
            .Where(a => a.RestoreDhcp)
            .Select(a => a.InterfaceAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(alias => new[]
            {
                $"netsh interface ipv4 set address name=\"{alias}\" source=dhcp",
                $"netsh interface ipv4 set dnsservers name=\"{alias}\" source=dhcp"
            });

    private sealed record Held(string Address, string Mask, string? Gateway);

    /// <summary>
    /// What the adapter is using right now - needed to pin a DHCP-assigned address
    /// before converting the adapter to static, so that address is not lost.
    /// </summary>
    private static Held? CurrentIPv4(string alias)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, alias, StringComparison.OrdinalIgnoreCase));

            var properties = nic?.GetIPProperties();

            var unicast = properties?.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(u.Address));

            if (unicast is null) return null;

            var mask = unicast.IPv4Mask?.ToString();
            if (string.IsNullOrWhiteSpace(mask) || mask == "0.0.0.0") mask = "255.255.255.0";

            var gateway = properties?.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !g.Address.Equals(IPAddress.Any))
                ?.Address.ToString();

            return new Held(unicast.Address.ToString(), mask, gateway);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One adapter, described well enough to choose between them.</summary>
    public sealed record Adapter(string Alias, string Description, string Addresses, bool Dhcp, string Kind)
    {
        /// <summary>
        /// Says plainly what borrowing will do here. A DHCP adapter has to be
        /// converted to static for the duration, which is worth knowing before
        /// picking it.
        /// </summary>
        public override string ToString()
        {
            var where = string.IsNullOrWhiteSpace(Addresses) ? "no IPv4 address" : Addresses;
            return $"{Alias}  [{Kind}]  -  {where}{(Dhcp ? "  (DHCP)" : "")}";
        }
    }

    /// <summary>
    /// Wired or wireless is the distinction that matters here: a laptop on Wi-Fi with
    /// the camera on a wired link must borrow on the wired adapter, and picking by
    /// address count alone gets that wrong.
    /// </summary>
    private static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Tunnel => "Tunnel",
        _ => "Other"
    };

    /// <summary>Every adapter that is up, for the interface pickers.</summary>
    public static List<Adapter> Adapters()
    {
        var found = new List<Adapter>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();

                var addresses = properties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => u.Address.ToString())
                    .ToList();

                found.Add(new Adapter(
                    nic.Name,
                    nic.Description,
                    string.Join(", ", addresses),
                    properties.GetIPv4Properties()?.IsDhcpEnabled ?? false,
                    Describe(nic.NetworkInterfaceType)));
            }
        }
        catch
        {
            // An empty list just means the picker offers only "automatic".
        }

        return found.OrderByDescending(a => a.Addresses.Length).ToList();
    }

    /// <summary>Whether this adapter currently gets its IPv4 address from DHCP.</summary>
    private static bool IsDhcpEnabled(string alias)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, alias, StringComparison.OrdinalIgnoreCase))
                ?.GetIPProperties().GetIPv4Properties()?.IsDhcpEnabled ?? false;
        }
        catch
        {
            // Unknown: assume static, so we never hand a manually configured adapter
            // to DHCP by mistake. Leaving one address behind is the lesser fault.
            return false;
        }
    }

    /// <summary>
    /// Addresses this run borrowed that are still on the adapter and have nothing
    /// left to remove them.
    ///
    /// While the session helper is alive this is always empty: the addresses are
    /// still there at the moment the app closes, by design, and the helper takes
    /// them away a second later. Reporting them would be a warning about the normal
    /// case.
    /// </summary>
    public IReadOnlyList<string> Stranded()
    {
        if (HelperIsRunning) return Array.Empty<string>();

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

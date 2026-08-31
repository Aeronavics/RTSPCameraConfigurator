using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace RTSPCameraConfigurator;

/// <summary>
/// Keeps <see cref="Cameras"/> continuously in step with what is actually on the
/// configured subnets.
///
/// Design notes:
///  - Only subnets named in the config are swept. Local interfaces are never added
///    implicitly; the search scope must be something the operator chose.
///  - A camera is identified once. Later sweeps only confirm it still answers, so a
///    steady state costs one TCP connect per host rather than a login each time.
///  - A camera that stops answering is marked offline before it is removed, so a
///    brief blip does not make rows disappear under the user's cursor.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly Func<(string User, string Password)> _defaultCredentials;
    private readonly CredentialStore _credentials;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Bound to the UI; only ever mutated on the dispatcher thread.</summary>
    public ObservableCollection<LiveCamera> Cameras { get; } = new();

    public event Action<string>? StatusChanged;

    public DiscoveryService(
        AppConfig config,
        Dispatcher dispatcher,
        CredentialStore credentials,
        Func<(string User, string Password)> defaultCredentials)
    {
        _config = config;
        _dispatcher = dispatcher;
        _credentials = credentials;
        _defaultCredentials = defaultCredentials;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var subnets = _config.Discovery.Subnets;

        if (subnets.Count == 0)
        {
            Report("no subnets configured");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(subnets, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Report($"discovery error: {ex.Message}");
            }

            if (!_config.Discovery.Continuous) return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _config.Discovery.RefreshSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(List<string> subnets, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subnet in subnets)
        {
            ct.ThrowIfCancellationRequested();
            Report($"scanning {subnet}.0/24 ...");

            var found = await Discovery.ScanAsync(subnet, _config.Discovery, null, null, ct);
            foreach (var hit in found)
            {
                seen.Add(hit.Address);
                await EnsurePresentAsync(hit.Address, ct);
            }
        }

        await AgeOutAsync(seen, ct);

        // Counting enumerates the bound collection, so it has to happen on the UI
        // thread - the CollectionView WPF wraps around it is thread-affine.
        var online = await OnUiAsync(() =>
            Cameras.Count(c => c.State is CameraState.Online or CameraState.CredentialsRequired));

        Report(online == 1 ? "1 camera" : $"{online} cameras");
    }

    private async Task EnsurePresentAsync(string address, CancellationToken ct)
    {
        // Deliberately removed a moment ago and still answering; leave it out.
        if (IsSuppressed(address)) return;

        var existing = await OnUiAsync(() => Cameras.FirstOrDefault(c =>
            string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase)));

        if (existing is not null)
        {
            await OnUiAsync(() =>
            {
                existing.Misses = 0;
                if (existing.State == CameraState.Offline)
                    existing.State = existing.Identified ? CameraState.Online : CameraState.Identifying;
                return true;
            });

            if (existing.Identified) return;
            await IdentifyAsync(existing, ct);
            return;
        }

        var camera = new LiveCamera { Address = address };
        await OnUiAsync(() => { Cameras.Add(camera); return true; });
        await IdentifyAsync(camera, ct);
    }

    /// <summary>Logs in once to learn what the camera is. Failure is a state, not an error.</summary>
    private async Task IdentifyAsync(LiveCamera camera, CancellationToken ct)
    {
        // The fallback reads the toolbar's textboxes, which are WPF controls and so
        // thread-affine - it must be evaluated on the UI thread, not here.
        var (user, password) = _credentials.TryGet(camera.Address)
                               ?? await OnUiAsync(() => _defaultCredentials());

        try
        {
            using var client = new CameraClient(camera.Address, _config.Profiles[0].Auth);
            await client.LoginAsync(user, password, ct);

            var device = await client.GetModuleAsync("device", ct);
            var info = device.ToDictionary(
                kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

            var profile = _config.MatchProfile(info);
            var summary = "";

            try
            {
                var main = await client.GetChannelAsync(
                    profile.Video.Module, $"get_{profile.Video.MainCommandSuffix}", profile.Image.Channel, ct);

                var codec = main["enc_type"]?.ToString() switch
                {
                    "0" => "H.264",
                    "1" => "H.265",
                    _ => "codec ?"
                };
                summary = $"{codec} {main["width"]}x{main["height"]} @ {main["framerate"]}fps";
            }
            catch (CameraException) { /* encoder detail is optional */ }

            await OnUiAsync(() =>
            {
                camera.Info = info;
                camera.Model = info.GetValueOrDefault("devtype", "");
                camera.Firmware = info.GetValueOrDefault("version", "");
                camera.Serial = info.GetValueOrDefault("serial_num", "");
                camera.StreamSummary = summary;
                camera.Identified = true;
                camera.State = CameraState.Online;
                return true;
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Wrong or unknown credentials: keep the row, flag it, let the user in.
            await OnUiAsync(() =>
            {
                camera.Identified = false;
                camera.State = CameraState.CredentialsRequired;
                return true;
            });
        }
    }

    private async Task AgeOutAsync(HashSet<string> seen, CancellationToken ct)
    {
        await OnUiAsync(() =>
        {
            foreach (var camera in Cameras.ToList())
            {
                if (seen.Contains(camera.Address))
                    continue;

                camera.Misses++;

                if (camera.Misses >= Math.Max(1, _config.Discovery.MissesBeforeRemoval))
                    Cameras.Remove(camera);
                else
                    camera.State = CameraState.Offline;
            }
            return true;
        });
    }

    /// <summary>
    /// Addresses a sweep must ignore until the given time. A camera told to reset or
    /// re-address keeps answering for a moment afterwards, and without this the very
    /// next sweep puts the row straight back.
    /// </summary>
    private readonly Dictionary<string, DateTime> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drops a row immediately rather than waiting for it to miss enough sweeps.
    /// Used when a camera is known to be leaving an address - its addressing was
    /// changed, or it was factory reset - so the list stops offering a dead entry.
    /// </summary>
    public void Forget(string address, TimeSpan? suppressFor = null)
    {
        lock (_suppressed)
        {
            if (suppressFor is { } window)
                _suppressed[address] = DateTime.UtcNow + window;
            else
                _suppressed.Remove(address);
        }

        _dispatcher.InvokeAsync(() =>
        {
            var stale = Cameras.FirstOrDefault(c =>
                string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase));

            if (stale is not null) Cameras.Remove(stale);
        });
    }

    private bool IsSuppressed(string address)
    {
        lock (_suppressed)
        {
            if (!_suppressed.TryGetValue(address, out var until)) return false;
            if (DateTime.UtcNow < until) return true;

            _suppressed.Remove(address);
            return false;
        }
    }

    /// <summary>
    /// Waits for a camera to appear at an address and brings it into the list without
    /// waiting for the next sweep. Returns the row, or null if nothing answered in
    /// time - a camera that has just reconfigured its interface takes a few seconds,
    /// and one given an address on an unreachable subnet never arrives at all.
    /// </summary>
    public async Task<LiveCamera?> AdoptAsync(string address, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await Discovery.RespondsAsync(address, _config.Discovery, ct))
            {
                await EnsurePresentAsync(address, ct);

                return await OnUiAsync(() => Cameras.FirstOrDefault(c =>
                    string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase)));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }

        return null;
    }

    /// <summary>Re-identifies one camera immediately, e.g. after new credentials are entered.</summary>
    public async Task RefreshAsync(LiveCamera camera)
    {
        camera.State = CameraState.Identifying;
        camera.Identified = false;
        await IdentifyAsync(camera, CancellationToken.None);
    }

    private Task<T> OnUiAsync<T>(Func<T> action) =>
        _dispatcher.CheckAccess()
            ? Task.FromResult(action())
            : _dispatcher.InvokeAsync(action).Task;

    private void Report(string message) =>
        _dispatcher.InvokeAsync(() => StatusChanged?.Invoke(message));

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}


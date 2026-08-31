using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RtspCameraSetup;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;


    /// <summary>Kept alive for as long as it is playing - see StartPreview.</summary>

    /// <summary>Pulls frames into a WPF bitmap instead of an embedded video window.</summary>

    /// <summary>Low-latency preview path; null when the libvlc engine is in use.</summary>
    private FfmpegVideoSource? _ffmpeg;

    /// <summary>Frame geometry per stream, cached at connect so ffmpeg gets the right size.</summary>
    private (int Width, int Height) _mainSize = (1280, 720);
    private (int Width, int Height) _subSize = (640, 480);

    private CameraClient? _client;
    private CameraProfile? _profile;

    private JsonObject? _imageState;
    private JsonObject? _netState;
    private JsonObject? _videoState;

    private (int Min, int Max) _framerateRange = (1, 30);
    private (int Min, int Max) _bitrateRange = (128, 8192);
    private (int Min, int Max) _gopRange = (1, 30);
    private readonly Dictionary<string, string> _deviceInfo = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>Borrows an interface address when a configured subnet is not reachable.</summary>
    private readonly NetworkScope _networkScope = new();

    private CredentialStore _credentials = null!;
    private DiscoveryService _discovery = null!;

    /// <summary>Credentials that actually worked for the connected camera; the RTSP URL needs them.</summary>
    private string _activeUser = "";
    private string _activePassword = "";

    /// <summary>The row the user clicked. There is no address box any more.</summary>
    private LiveCamera? _selectedCamera;

    /// <summary>Starting point for any camera with no saved login.</summary>
    private (string User, string Password) DefaultCredentials =>
        (_config.Profiles[0].Auth.DefaultUsername, _config.Profiles[0].Auth.DefaultPassword);

    // Slider writes are throttled so dragging does not flood the camera.
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _statsTimer;

    /// <summary>Debounces the whole-object image write; there is no Apply button.</summary>
    private readonly DispatcherTimer _commitTimer;

    /// <summary>The same, for the OSD object.</summary>
    private readonly DispatcherTimer _osdCommitTimer;


    /// <summary>Keeps the preset list current without a Refresh button.</summary>
    private FileSystemWatcher? _presetWatcher;
    private readonly DispatcherTimer _presetRefreshTimer;
    private long _lastRendered;
    private readonly Dictionary<int, int> _pendingFastWrites = new();

    private bool _suppressUiEvents;

    /// <summary>
    /// False until the constructor finishes. A ComboBoxItem carrying
    /// IsSelected="True" raises SelectionChanged while the XAML tree is still being
    /// built, so handlers can run before the controls they touch exist.
    /// </summary>
    private bool _uiReady;

    public MainWindow()
    {
        InitializeComponent();

        _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _throttle.Tick += async (_, _) => await FlushFastWritesAsync();

        // A still picture and a frozen one look identical; show the counters.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdatePreviewStats();
        _statsTimer.Start();

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _commitTimer.Tick += async (_, _) => await CommitImageAsync();

        _osdCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _osdCommitTimer.Tick += async (_, _) => await CommitOsdAsync();


        _presetRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _presetRefreshTimer.Tick += (_, _) => { _presetRefreshTimer.Stop(); RefreshPresets(); };

        try
        {
            _config = AppConfig.Load(ConfigPath());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Configuration error", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            _config = new AppConfig();
            return;
        }

        InitialiseVideo();
        SetConnectedUi(false);
        RefreshPresets();
        StartWatchingPresets();

        (_activeUser, _activePassword) = DefaultCredentials;
        UpdateWatchScope();

        var reclaimed = _networkScope.CleanUpLeftovers();
        if (reclaimed > 0)
            SetStatus($"Removed {reclaimed} temporary address(es) left by a previous run.");

        EnsureSubnetsReachable();

        _credentials = new CredentialStore();
        _discovery = new DiscoveryService(
            _config, Dispatcher, _credentials, () => DefaultCredentials);

        CameraList.ItemsSource = _discovery.Cameras;
        _discovery.StatusChanged += _ => { };
        _discovery.Start();

        Closed += (_, _) => Teardown();
        _uiReady = true;
    }

    private static string ConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "cameras.json");

    private void InitialiseVideo()
    {
        // ffmpeg is the only engine. It is bundled beside the executable by the
        // publish, and looked up on PATH as a fallback for a plain build.
        if (FfmpegVideoSource.Resolve(_config.Preview.FfmpegPath) is { } path)
        {
            _ffmpeg = new FfmpegVideoSource(VideoImage, _config.Preview);
            return;
        }

        DisablePreview(
            $"ffmpeg not found ('{_config.Preview.FfmpegPath}'). It ships beside the " +
            "executable; install one with 'winget install Gyan.FFmpeg.Essentials' or " +
            "copy ffmpeg.exe next to CameraSetup.exe.");
    }

    /// <summary>Configuration still works without a video engine, so degrade rather than fail.</summary>
    private void DisablePreview(string reason)
    {
        SetStatus($"Video engine unavailable - preview disabled. ({reason})");
        _previewDisabled = true;
    }

    /// <summary>Set when libvlc could not load, so preview attempts stay silent.</summary>
    private bool _previewDisabled;

    /// <summary>Tabs and menu items that need a live connection.</summary>
    private void SetConnectedUi(bool connected)
    {
        ImageTab.IsEnabled = connected;
        DetectionTab.IsEnabled = connected;
        ServicesTab.IsEnabled = connected;
        UsersTab.IsEnabled = connected;
        OsdTab.IsEnabled = connected;
        StreamTab.IsEnabled = connected;
        NetworkTab.IsEnabled = connected;
        DeviceTab.IsEnabled = connected;

        ExportButton.IsEnabled = connected;
        ImportButton.IsEnabled = connected;

        UpdateProvisionTarget();
    }

    // ================================================================ menus

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClicked(object sender, RoutedEventArgs e) =>
        new AboutDialog(ConfigPath(), PresetDirectory(), _credentials.StorePath) { Owner = this }
            .ShowDialog();

    private void OnOpenPresetsFolderClicked(object sender, RoutedEventArgs e)
    {
        var directory = PresetDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {directory}:{Environment.NewLine}{ex.Message}",
                "Presets folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSubnetSettingsClicked(object sender, RoutedEventArgs e)
    {
        var discovery = _config.Discovery;
        var dialog = new SettingsDialog(discovery.Subnets, discovery.Continuous, discovery.RefreshSeconds)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            ConfigFile.UpdateDiscovery(ConfigPath(), dialog.Subnets, dialog.Continuous, dialog.RefreshSeconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save cameras.json:{Environment.NewLine}{ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Apply in memory too, then restart the sweep so the change takes effect now
        // rather than at the next launch.
        discovery.Subnets = dialog.Subnets;
        discovery.Continuous = dialog.Continuous;
        discovery.RefreshSeconds = dialog.RefreshSeconds;

        EnsureSubnetsReachable();
        RestartDiscovery();
        SetStatus($"Now watching {string.Join(", ", dialog.Subnets.Select(s => s + ".0/24"))}.");
    }

    private void OnReloadConfigClicked(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "cameras.json is read at startup. Restart the app to pick up edits made " +
            "outside it." + Environment.NewLine + Environment.NewLine +
            "Subnet changes made through Settings apply immediately and do not need a restart.",
            "Reload configuration", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestartDiscovery()
    {
        _discovery.Dispose();

        _discovery = new DiscoveryService(
            _config, Dispatcher, _credentials, () => DefaultCredentials);

        CameraList.ItemsSource = _discovery.Cameras;
        _discovery.StatusChanged += _ => { };
        _discovery.Start();

        UpdateWatchScope();
    }

    private void UpdateWatchScope()
    {
        // The watched subnets live in Settings; they are not echoed onto the window.
    }

    /// <summary>
    /// A configured subnet is only searchable if this machine has an address on it.
    /// Where it does not, offer to borrow one for the session rather than silently
    /// scanning a network that can never answer.
    /// </summary>
    private void EnsureSubnetsReachable()
    {
        var discovery = _config.Discovery;
        if (discovery.Subnets.Count == 0) return;

        var unreachable = NetworkScope.Unreachable(discovery.Subnets);
        if (unreachable.Count == 0) return;

        var list = string.Join(", ", unreachable.Select(s => $"{s}.0/24"));

        if (!discovery.AutoConfigureInterface)
        {
            SetStatus($"Not searchable from this machine - no local address on {list}.");
            return;
        }

        var adapter = NetworkScope.PreferredInterface(discovery.InterfaceAlias);
        if (adapter is null)
        {
            SetStatus($"Cannot reach {list} and no suitable adapter was found.");
            return;
        }

        var confirm = MessageBox.Show(
            $"These subnets are configured but this machine has no address on them, so " +
            $"nothing on them can be found:" + Environment.NewLine + Environment.NewLine +
            $"    {list}" + Environment.NewLine + Environment.NewLine +
            $"Add a temporary address on each to '{adapter}' for this session?" +
            Environment.NewLine + Environment.NewLine +
            "Existing addresses are left alone, and anything added is removed again when " +
            "this app closes. Windows will ask for administrator permission.",
            "Subnets not reachable", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            SetStatus($"{list} will not be searched - no local address on them.");
            return;
        }

        var covered = _networkScope.AddTemporaryAddresses(
            unreachable, adapter, discovery.TemporaryHostFirst, discovery.TemporaryHostLast, out var error);

        if (covered.Count > 0)
        {
            SetStatus($"Borrowed an address on {string.Join(", ", covered.Select(s => s + ".0/24"))} " +
                      $"via {adapter}; it will be removed when the app closes.");
        }
        else
        {
            SetStatus($"Could not add a temporary address for {list}" +
                      (error is null ? "." : $" - {error}."));
        }
    }

    // ============================================================= discovery

    /// <summary>Clicking a camera connects to it and starts its stream.</summary>
    private async void OnCameraSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        if (CameraList.SelectedItem is not LiveCamera camera) return;

        _selectedCamera = camera;
        UpdateProvisionTarget();
        await ConnectToAsync(camera);
    }

    private async void OnReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is null)
        {
            SetStatus("Select a camera first.");
            return;
        }

        await ConnectToAsync(_selectedCamera);
    }

    private async void OnRebootClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is not { } camera)
        {
            SetStatus("Select a camera first.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Reboot {camera.Address}?" + Environment.NewLine + Environment.NewLine +
            $"{camera.Model}  sn {camera.Serial}" + Environment.NewLine + Environment.NewLine +
            "Settings are kept. The camera drops off the list and reappears in about " +
            $"{_config.Profiles[0].System.RebootSeconds} seconds.",
            "Reboot camera", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        await RunSystemCommandAsync(camera, _config.MatchProfile(camera.Info).System.RebootCommand,
            $"Rebooting {camera.Address} - it should reappear in about " +
            $"{_config.Profiles[0].System.RebootSeconds} seconds.");
    }

    private async void OnFactoryResetClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is not { } camera)
        {
            SetStatus("Select a camera first.");
            return;
        }

        // Two steps, because this is irreversible and takes the address with it: if
        // the camera returns on a subnet that is not being watched, it disappears
        // from the app entirely and has to be found by other means.
        var first = MessageBox.Show(
            $"FACTORY RESET {camera.Address}?" + Environment.NewLine + Environment.NewLine +
            $"{camera.Model}  sn {camera.Serial}" + Environment.NewLine + Environment.NewLine +
            "This erases every setting on the camera, including its IP address. " +
            "It cannot be undone." + Environment.NewLine + Environment.NewLine +
            "The camera will very likely come back on a different address, and if that " +
            "address is outside the subnets being watched it will not reappear in this list.",
            "Factory reset", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (first != MessageBoxResult.OK) return;

        var second = MessageBox.Show(
            $"Last check - erase all settings on {camera.Address}?" + Environment.NewLine + Environment.NewLine +
            "Export its parameters first if you might want them back.",
            "Factory reset", MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);

        if (second != MessageBoxResult.Yes) return;

        await RunSystemCommandAsync(camera, _config.MatchProfile(camera.Info).System.FactoryResetCommand,
            $"Factory reset sent to {camera.Address} - removed from the list. It will return " +
            "with default settings, probably on a different address.",
            forgetAddress: true);
    }

    /// <summary>
    /// <paramref name="forgetAddress"/> drops the row as soon as the command is
    /// accepted. Right for a factory reset, which takes the address with it - the row
    /// left behind can never be connected to again. Wrong for a reboot, which comes
    /// back on the same address, where the row going Offline and then Online is the
    /// honest picture.
    /// </summary>
    private async Task RunSystemCommandAsync(
        LiveCamera camera, string command, string successMessage, bool forgetAddress = false)
    {
        StopPreview();

        try
        {
            var profile = _config.MatchProfile(camera.Info);
            var saved = _credentials.TryGet(camera.Address);
            var (user, password) = saved ?? DefaultCredentials;

            using var client = new CameraClient(camera.Address, profile.Auth);
            await client.LoginAsync(user, password);
            await client.SystemCommandAsync(profile.System.Module, command);

            SetStatus(successMessage);

            if (forgetAddress)
            {
                // Suppressed briefly too: the camera answers for a moment after
                // accepting the command, and an unlucky sweep would re-add the row
                // seconds after it was removed.
                _discovery.Forget(camera.Address, TimeSpan.FromSeconds(30));
                if (ReferenceEquals(_selectedCamera, camera)) _selectedCamera = null;
            }

            // The connection is about to die with the device; drop it so the UI does
            // not sit there offering settings for a camera that has gone.
            _client?.Dispose();
            _client = null;
            SetConnectedUi(false);
        }
        catch (Exception ex)
        {
            SetStatus($"{camera.Address} - command failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Command failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateProvisionTarget()
    {
        var connected = _client is not null && _selectedCamera is not null;

        // The target is whichever row is selected in the list; it is not restated.
        ProvisionSelectedButton.IsEnabled = connected;
    }

    /// <summary>
    /// Connects using saved credentials, falling back to the defaults, and prompting
    /// only when neither works. On success the preview starts by itself.
    /// </summary>
    private async Task ConnectToAsync(LiveCamera camera)
    {
        // Supersede whatever was still connecting. Clicking a second row - or a
        // discovery sweep raising SelectionChanged again while the credential modal
        // pumps messages - used to start an overlapping connect, and the two fought
        // over _client.
        _connectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        var generation = ++_connectGeneration;

        bool Superseded() => generation != _connectGeneration;

        var saved = _credentials.TryGet(camera.Address);
        var (user, password) = saved ?? DefaultCredentials;

        try
        {
            var connected = await TryConnectAsync(camera.Address, user, password, cts.Token);

            while (!connected)
            {
                if (Superseded()) return;

                var dialog = new CredentialDialog(camera.Address, user, _lastConnectError) { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    SetStatus($"{camera.Address} - sign in cancelled.");
                    return;
                }

                // ShowDialog pumps messages, so the selection may have moved on to
                // another camera while the prompt was open.
                if (Superseded()) return;

                user = dialog.Username;
                password = dialog.Password;

                connected = await TryConnectAsync(camera.Address, user, password, cts.Token);

                if (connected && dialog.ShouldSave)
                {
                    _credentials.Save(camera.Address, user, password);
                    SetStatus($"Connected to {camera.Address} - credentials saved.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer connect took over; it owns the UI now.
            return;
        }

        if (Superseded()) return;

        // Remember what actually worked - the RTSP URL is derived from it.
        _activeUser = user;
        _activePassword = password;
        UpdateProvisionTarget();

        if (camera.State == CameraState.CredentialsRequired)
            await _discovery.RefreshAsync(camera);

        StartPreview();
    }

    /// <summary>
    /// Follows a camera that has just been given a new address: drops the row it left
    /// behind, waits for it to answer on the new one, then selects it - which connects
    /// and restarts the preview through the ordinary click-to-connect path.
    ///
    /// The old row is removed rather than left to age out, because until it does it is
    /// an entry that looks selectable and cannot be connected to.
    /// </summary>
    private async Task FollowMoveAsync(string oldAddress, string newAddress)
    {
        // Suppressed only briefly: if the move did not take, the camera is still there
        // and should come back into the list on its own rather than stay hidden.
        _discovery.Forget(oldAddress, TimeSpan.FromSeconds(15));

        _client?.Dispose();
        _client = null;
        SetConnectedUi(false);

        SetStatus($"{oldAddress} is moving to {newAddress} - waiting for it to come back ...");

        var moved = await _discovery.AdoptAsync(newAddress, TimeSpan.FromSeconds(45));

        if (moved is null)
        {
            SetStatus($"{newAddress} has not answered yet. It will appear in the list when it does.");
            return;
        }

        // Selecting the row is what connects: the same path as clicking it.
        _selectedCamera = moved;
        CameraList.SelectedItem = moved;
        CameraList.ScrollIntoView(moved);

        // SelectionChanged does not fire when the row was already the selected item,
        // so connect explicitly rather than relying on it.
        if (!ReferenceEquals(CameraList.SelectedItem, moved) || _client is null)
            await ConnectToAsync(moved);
    }

    private string? _lastConnectError;

    /// <summary>
    /// Cancels the connect currently in flight. Not disposed when superseded - the
    /// operation it belongs to may still be unwinding and holding its token.
    /// </summary>
    private CancellationTokenSource? _connectCts;

    private int _connectGeneration;

    private async Task<bool> TryConnectAsync(
        string address, string user, string password, CancellationToken ct)
    {
        try
        {
            await ConnectAsync(address, user, password, throwOnFailure: true, ct);
            _lastConnectError = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ours, not the camera's - do not report it as a connection failure.
            throw;
        }
        catch (Exception ex)
        {
            _lastConnectError = ex.Message;
            return false;
        }
    }

    private void OnForgetCredentialsClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is null)
        {
            SetStatus("Select a camera first.");
            return;
        }

        var address = _selectedCamera.Address;

        if (_credentials.TryGet(address) is null)
        {
            SetStatus($"No saved credentials for {address}.");
            return;
        }

        // Confirmed because this is destructive and the button can catch a stray
        // Enter: a keystroke meant for a dialog should never silently delete a
        // stored login.
        var confirm = MessageBox.Show(
            $"Discard the saved login for {address}?" + Environment.NewLine + Environment.NewLine +
            "You will be asked for credentials the next time you connect to it.",
            "Forget credentials", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        _credentials.Forget(address);
        SetStatus($"Saved credentials for {address} discarded.");
    }

    // =============================================================== connect

    /// <summary>
    /// When <paramref name="throwOnFailure"/> is set the caller handles the error -
    /// used by the click-to-connect path, which retries behind a credential prompt
    /// rather than showing a message box for every failed attempt.
    /// </summary>
    private async Task ConnectAsync(
        string address, string username, string password, bool throwOnFailure = false,
        CancellationToken ct = default)
    {
        StopPreview();
        SetStatus($"Connecting to {address} ...");

        // Built locally and only published once the login lands. Assigning _client up
        // front meant a second connect disposed the HttpClient this one was still
        // waiting on, which the camera got blamed for as "A task was canceled".
        CameraClient? pending = null;

        try
        {
            // Log in with the first profile's auth spec, then re-select the profile
            // from the device info the camera reports.
            pending = new CameraClient(address, _config.Profiles[0].Auth);
            await pending.LoginAsync(username, password, ct);

            ct.ThrowIfCancellationRequested();

            _client?.Dispose();
            _client = pending;
            pending = null;

            await LoadDeviceInfoAsync();
            _profile = _config.MatchProfile(_deviceInfo);

            await LoadNetworkAsync();
            await LoadImageAsync();
            await LoadOsdAsync();
            await LoadDetectionAsync();
            await LoadModulesAsync();
            await LoadUsersAsync();
            await LoadVideoAsync();
            await CacheStreamSizesAsync();

            SetConnectedUi(true);
            UpdateStreamUrlText();
            SetStatus($"Connected to {address} - {_profile.Name}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer connect - leave the UI and _client alone, since
            // that connect is already driving them.
            pending?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            pending?.Dispose();
            SetConnectedUi(false);
            _client?.Dispose();
            _client = null;
            SetStatus($"Connection failed: {ex.Message}");

            if (throwOnFailure) throw;
            MessageBox.Show(ex.Message, "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadDeviceInfoAsync()
    {
        if (_client is null) return;

        var device = await _client.GetModuleAsync("device");

        _deviceInfo.Clear();
        foreach (var (key, value) in device)
            _deviceInfo[key] = value?.ToString() ?? "";

        RenderDeviceInfo(device);
    }

    private void RenderDeviceInfo(JsonObject device)
    {
        DevicePanel.Children.Clear();

        var fields = _profile?.DeviceInfoFields;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pairs = new List<(string Label, string Value)>();

        if (fields is { Count: > 0 })
        {
            foreach (var f in fields)
                if (_deviceInfo.TryGetValue(f.Key, out var v))
                    pairs.Add((f.Label, v));
        }
        else
        {
            foreach (var (key, value) in device)
                pairs.Add((key, value?.ToString() ?? ""));
        }

        for (var i = 0; i < pairs.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = pairs[i].Label,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 3, 12, 3)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var value = new TextBlock
            {
                Text = pairs[i].Value,
                Margin = new Thickness(0, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        DevicePanel.Children.Add(grid);
    }

    // =============================================================== network

    private async Task LoadNetworkAsync()
    {
        if (_client is null || _profile is null) return;

        var spec = _profile.Network;
        _netState = await _client.GetModuleAsync(spec.Module);

        _suppressUiEvents = true;
        try
        {
            IpBox.Text = Str(_netState, spec.IpKey);
            MaskBox.Text = Str(_netState, spec.MaskKey);
            GatewayBox.Text = Str(_netState, spec.GatewayKey);
            DnsBox.Text = Str(_netState, spec.DnsKey);
            MacText.Text = Str(_netState, "mac");

            // Selected by value, not by position: the camera reports the mode itself
            // (4 = "Enable 6 hour"), and matching on index would misread it.
            DhcpBox.ItemsSource = spec.DhcpModes;
            AdaptiveBox.ItemsSource = spec.AdaptiveModes;

            SelectMode(DhcpBox, spec.DhcpModes, Int(_netState, spec.DhcpKey));
            SelectMode(AdaptiveBox, spec.AdaptiveModes, Int(_netState, spec.AdaptiveKey));
            QwtBox.IsChecked = Int(_netState, spec.AllNetConnectKey) != 0;

            ApplyDhcpEnabling();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    /// <summary>
    /// Picks the entry matching the camera's value. A value the option list does not
    /// cover leaves the box blank rather than silently selecting something else -
    /// saving would then be the only way to change it.
    /// </summary>
    private static void SelectMode(ComboBox box, List<OptionSpec> options, int value) =>
        box.SelectedItem = options.FirstOrDefault(o => o.Value == value);

    private static int ModeValue(ComboBox box, int fallback) =>
        box.SelectedItem is OptionSpec option ? option.Value : fallback;

    private void OnDhcpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ApplyDhcpEnabling();
    }

    private void OnAdaptiveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ApplyDhcpEnabling();
    }

    private void OnAllNetConnectChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ApplyDhcpEnabling();
    }

    private void ApplyDhcpEnabling()
    {
        // Anything other than Disable means the camera may take an address from a
        // DHCP server, so the static fields stop being the whole story.
        var isStatic = ModeValue(DhcpBox, 0) == 0;

        IpBox.IsEnabled = isStatic;
        MaskBox.IsEnabled = isStatic;
        GatewayBox.IsEnabled = isStatic;
        DnsBox.IsEnabled = isStatic;

        SelfRecoveryWarning.Visibility =
            isStatic && ModeValue(AdaptiveBox, 0) == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnReloadNetworkClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadNetworkAsync();
            SetStatus("Network settings reloaded.");
        }
        catch (Exception ex)
        {
            SetStatus($"Reload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Address, mask, gateway, DNS and the DHCP mode are one object on the camera and
    /// have to move together: writing the address alone applies it against the old
    /// mask and gateway, and the camera then jumps to the new address before the rest
    /// can be filled in. So this tab keeps an explicit Save, unlike the others.
    /// </summary>
    private async void OnSaveNetworkClicked(object sender, RoutedEventArgs e) => await ApplyNetworkAsync();

    private void OnNetworkKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SaveNetworkButton.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private async void OnRevertNetworkClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadNetworkAsync();
            SetStatus("Network settings reloaded from the camera.");
        }
        catch (Exception ex)
        {
            SetStatus($"Reload failed: {ex.Message}");
        }
    }

    private async void OnApplyNetworkClicked(object sender, RoutedEventArgs e) => await ApplyNetworkAsync();

    private async Task ApplyNetworkAsync()
    {
        if (_client is null || _profile is null || _netState is null) return;

        var spec = _profile.Network;

        // Preserve whatever the camera currently has if the box could not be matched
        // to a known mode, rather than forcing it to Disable.
        var dhcpMode = ModeValue(DhcpBox, Int(_netState, spec.DhcpKey));
        var adaptiveMode = ModeValue(AdaptiveBox, Int(_netState, spec.AdaptiveKey));
        var useDhcp = dhcpMode != 0;

        var newIp = IpBox.Text.Trim();

        if (!useDhcp)
        {
            foreach (var (value, name) in new[]
                     {
                         (newIp, "IP address"),
                         (MaskBox.Text.Trim(), "Subnet mask"),
                         (GatewayBox.Text.Trim(), "Gateway")
                     })
            {
                if (!System.Net.IPAddress.TryParse(value, out _))
                {
                    MessageBox.Show($"{name} is not a valid IPv4 address.",
                        "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var dns = DnsBox.Text.Trim();
            if (dns.Length > 0 && !System.Net.IPAddress.TryParse(dns, out _))
            {
                MessageBox.Show("DNS is not a valid IPv4 address.",
                    "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var currentAddress = _client.Host;
        var willMove = !useDhcp && !string.Equals(newIp, currentAddress, StringComparison.Ordinal);

        // Adaptive is the setting that moves a camera without anyone touching it, so
        // say so at the point of decision rather than leaving it to be discovered.
        var adaptiveNote = adaptiveMode == 0
            ? ""
            : "\n\nIP adaptive stays on (" +
              $"{spec.AdaptiveModes.FirstOrDefault(o => o.Value == adaptiveMode)?.Label ?? adaptiveMode.ToString()}" +
              "): if the gateway stops answering, the camera will drop this address and take a DHCP lease instead.";

        var message = (useDhcp
            ? "Switch the camera to DHCP?\n\nIt will take a new address from your DHCP server, so this app will lose track of it. Re-scan to find it again."
            : willMove
                ? $"Change the camera address from {currentAddress} to {newIp}?"
                : "Apply the network settings?") + adaptiveNote;

        if (MessageBox.Show(message, "Confirm network change",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        // Echo back everything the camera gave us, overriding only what we own.
        var body = new JsonObject();
        foreach (var key in spec.PassthroughKeys)
            if (_netState.TryGetPropertyValue(key, out var v) && v is not null)
                body[key] = v.DeepClone();

        body[spec.DhcpKey] = dhcpMode;
        body[spec.AdaptiveKey] = adaptiveMode;
        body[spec.AllNetConnectKey] = QwtBox.IsChecked == true ? 1 : 0;
        body[spec.IpKey] = newIp;
        body[spec.MaskKey] = MaskBox.Text.Trim();
        body[spec.GatewayKey] = GatewayBox.Text.Trim();
        body[spec.DnsKey] = DnsBox.Text.Trim();

        StopPreview();

        try
        {
            SetStatus("Applying network settings ...");

            try
            {
                await _client.SetModuleAsync(spec.Module, body);
            }
            catch (Exception ex) when (willMove)
            {
                // Expected: the camera can drop the connection as the interface
                // reconfigures, before it manages to answer.
                SetStatus($"Camera dropped the connection while reconfiguring ({ex.Message}) - following to {newIp} ...");
            }

            if (useDhcp)
            {
                SetConnectedUi(false);
                _client.Dispose();
                _client = null;
                SetStatus("Camera switched to DHCP. Re-scan the subnet to find its new address.");
                return;
            }

            if (willMove)
            {
                await FollowMoveAsync(currentAddress, newIp);
            }
            else
            {
                await LoadNetworkAsync();
                SetStatus("Network settings applied.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Applying network settings failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Apply failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
        }
    }

    // =============================================================== users

    /// <summary>
    /// A camera account. The firmware returns the password in clear in its list
    /// response; it is kept only to satisfy delete, which requires it, and is never
    /// shown.
    /// </summary>
    private sealed record CameraUser(string Name, string Password, int Type, int Power)
    {
        public string Role => Type == 0 ? "Administrator" : "User";
    }

    private const string AccountModule = "account";

    private readonly List<CameraUser> _users = new();

    private async Task LoadUsersAsync()
    {
        if (_client is null) return;

        _users.Clear();
        var supported = true;

        try
        {
            var list = await _client.GetArrayAsync(AccountModule, "list");

            foreach (var entry in list.OfType<JsonObject>())
                _users.Add(new CameraUser(
                    entry["name"]?.ToString() ?? "",
                    entry["pwd"]?.ToString() ?? "",
                    Int(entry, "type"),
                    Int(entry, "power")));
        }
        catch (CameraException)
        {
            // Not every firmware exposes account management; hide the tab rather than
            // showing an empty list that cannot be used.
            supported = false;
        }

        UsersTab.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;

        _suppressUiEvents = true;
        try
        {
            UserList.ItemsSource = null;
            UserList.ItemsSource = _users;
            UserNameBox.Text = "";
            UserPasswordBox.Text = "";
            UserWarning.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        UpdateUserButtons();
    }

    private void OnUserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;

        if (UserList.SelectedItem is CameraUser user)
            UserNameBox.Text = user.Name;

        UpdateUserButtons();
    }

    private void UpdateUserButtons()
    {
        var selected = UserList.SelectedItem as CameraUser;

        // The firmware's own page refuses to delete admin, and losing it would leave
        // the camera unreachable.
        DeleteUserButton.IsEnabled =
            selected is not null && !string.Equals(selected.Name, "admin", StringComparison.OrdinalIgnoreCase);
    }

    private void WarnUser(string message)
    {
        UserWarning.Text = message;
        UserWarning.Visibility = Visibility.Visible;
        SetStatus(message);
    }

    /// <summary>Rejects what the firmware's own page rejects, before the round trip.</summary>
    private bool ValidUserInput(string name, string password, bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(' '))
        {
            WarnUser("A user name is required and cannot contain spaces.");
            return false;
        }

        if (requirePassword && (string.IsNullOrEmpty(password) || password.Contains(' ')))
        {
            WarnUser("A password is required and cannot contain spaces.");
            return false;
        }

        UserWarning.Visibility = Visibility.Collapsed;
        return true;
    }

    private async void OnAddUserClicked(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;

        var name = UserNameBox.Text.Trim();
        var password = UserPasswordBox.Text;

        if (!ValidUserInput(name, password, requirePassword: true)) return;

        if (_users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            WarnUser($"'{name}' already exists - use Set password to change it.");
            return;
        }

        try
        {
            await _client.SetModuleAsync(AccountModule,
                new JsonObject { ["name"] = name, ["pwd"] = password }, "add");

            SetStatus($"User '{name}' added.");
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            WarnUser($"Could not add '{name}': {ex.Message}");
        }
    }

    private async void OnChangePasswordClicked(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;

        var name = UserNameBox.Text.Trim();
        var password = UserPasswordBox.Text;

        if (!ValidUserInput(name, password, requirePassword: true)) return;

        if (!_users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            WarnUser($"'{name}' does not exist - use Add user instead.");
            return;
        }

        // Changing the account we are logged in with drops this connection, so say so
        // rather than letting the app appear to break.
        var isActive = string.Equals(name, _activeUser, StringComparison.OrdinalIgnoreCase);

        var confirm = MessageBox.Show(
            $"Set a new password for '{name}' on {_client.Host}?" +
            (isActive
                ? Environment.NewLine + Environment.NewLine +
                  "This is the account this app is connected with. The connection will drop and " +
                  "reconnect with the new password, which will be saved."
                : ""),
            "Confirm password change", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var host = _client.Host;

        try
        {
            await _client.SetModuleAsync(AccountModule,
                new JsonObject { ["name"] = name, ["pwd"] = password }, "modify");
        }
        catch (Exception ex)
        {
            WarnUser($"Could not set the password for '{name}': {ex.Message}");
            return;
        }

        SetStatus($"Password for '{name}' changed.");

        if (!isActive)
        {
            await LoadUsersAsync();
            return;
        }

        // Keep the app and the stored credentials in step with what the camera now wants.
        _activePassword = password;
        if (_credentials.TryGet(host) is not null) _credentials.Save(host, name, password);

        if (_selectedCamera is not null)
            await ConnectToAsync(_selectedCamera);
        else
            await ConnectAsync(host, name, password);
    }

    private async void OnDeleteUserClicked(object sender, RoutedEventArgs e)
    {
        if (_client is null || UserList.SelectedItem is not CameraUser user) return;

        if (string.Equals(user.Name, "admin", StringComparison.OrdinalIgnoreCase))
        {
            WarnUser("The admin account cannot be deleted.");
            return;
        }

        if (string.Equals(user.Name, _activeUser, StringComparison.OrdinalIgnoreCase))
        {
            WarnUser("That is the account this app is connected with - connect as someone else first.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete the user '{user.Name}' from {_client.Host}?",
            "Confirm delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            // Delete takes the password as well as the name - the firmware's own page
            // sends the one it read back from the list.
            await _client.SetModuleAsync(AccountModule,
                new JsonObject { ["name"] = user.Name, ["pwd"] = user.Password }, "delete");

            SetStatus($"User '{user.Name}' deleted.");
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            WarnUser($"Could not delete '{user.Name}': {ex.Message}");
        }
    }

    // =============================================== generic settings modules

    /// <summary>One rendered module: its spec, last-read state and controls.</summary>
    private sealed class ModuleSection
    {
        public SimpleModuleSpec Spec = null!;
        public JsonObject? State;
        public readonly List<(ModuleFieldSpec Field, FrameworkElement Control)> Fields = new();
        public readonly List<(CheckBox Enable, TextBox Begin, TextBox End)> Days = new();
        public TextBlock? RegionText;
        public TextBlock Warning = null!;
        public DispatcherTimer Commit = null!;
    }

    private readonly List<ModuleSection> _modules = new();

    /// <summary>
    /// Walks a dotted key such as "ntp.server". Returns the owning object and the
    /// final segment, creating nothing - a path the camera does not report yields null.
    /// </summary>
    private static (JsonObject? Owner, string Leaf) ResolvePath(JsonObject root, string key)
    {
        var parts = key.Split('.');
        JsonObject? owner = root;

        for (var i = 0; i < parts.Length - 1 && owner is not null; i++)
            owner = owner[parts[i]] as JsonObject;

        return (owner, parts[^1]);
    }

    private static JsonNode? ReadPath(JsonObject root, string key)
    {
        var (owner, leaf) = ResolvePath(root, key);
        return owner is not null && owner.TryGetPropertyValue(leaf, out var node) ? node : null;
    }

    private static void WritePath(JsonObject root, string key, JsonNode? value)
    {
        var (owner, leaf) = ResolvePath(root, key);

        // Only write where the camera already had somewhere to put it; inventing a
        // nested object the firmware never reported is how you get a rejected write.
        if (owner is not null) owner[leaf] = value;
    }

    private async Task LoadModulesAsync()
    {
        if (_client is null || _profile is null) return;

        BuildModuleTabs();
        ServicesTab.Visibility = _modules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var section in _modules)
        {
            try
            {
                section.State = section.Spec.Channel is { } channel
                    ? await _client.GetChannelAsync(section.Spec.Module, section.Spec.GetCommand, channel)
                    : await _client.GetModuleAsync(section.Spec.Module, section.Spec.GetCommand);
            }
            catch (CameraException)
            {
                // Present in config but not on this firmware - leave the tab blank
                // rather than failing the whole connect.
                continue;
            }

            LoadModule(section);
        }
    }

    private void LoadModule(ModuleSection section)
    {
        if (section.State is null) return;

        _suppressUiEvents = true;
        try
        {
            LoadFields(section.State, section.Fields);

            if (section.Days.Count > 0)
                LoadModuleSchedule(section, section.State["schedule"] as JsonArray);

            DescribeModuleRegion(section);

            section.Warning.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private static void LoadModuleSchedule(ModuleSection section, JsonArray? schedule)
    {
        for (var day = 0; day < section.Days.Count; day++)
        {
            var entry = day < (schedule?.Count ?? 0) ? schedule![day] as JsonObject : null;

            section.Days[day].Enable.IsChecked = entry is not null && Int(entry, "enable") != 0;
            section.Days[day].Begin.Text = FormatTime(entry?["begin1"] as JsonObject);
            section.Days[day].End.Text = FormatTime(entry?["end1"] as JsonObject);
        }
    }

    private static int ToInt(JsonNode? node) =>
        node is null ? 0
        : node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.True => 1,
            System.Text.Json.JsonValueKind.False => 0,
            _ => int.TryParse(node.ToString(), out var parsed) ? parsed : 0
        };

    private void BuildModuleTabs()
    {
        if (_modules.Count > 0 || _profile is null) return;

        foreach (var spec in _profile.Modules)
        {
            if (!Supports(spec.CapabilityBit)) continue;

            var section = new ModuleSection { Spec = spec };
            var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };

            if (!string.IsNullOrWhiteSpace(spec.Note))
                panel.Children.Add(new TextBlock
                {
                    Text = spec.Note,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 12)
                });

            var grid = NewFieldGrid();
            RenderFields(spec.Fields, grid, 0, section.Fields, () => OnModuleChanged(section));
            panel.Children.Add(grid);

            if (spec.HasRegion)
            {
                panel.Children.Add(GroupHeader("Region"));

                var edit = new Button
                {
                    Content = "Edit region ...",
                    MinWidth = 130,
                    Padding = new Thickness(10, 5, 10, 5),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                edit.Click += (_, _) => EditModuleRegion(section);
                panel.Children.Add(edit);

                section.RegionText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                panel.Children.Add(section.RegionText);
            }

            if (spec.HasSchedule)
            {
                panel.Children.Add(GroupHeader("Schedule"));
                panel.Children.Add(new TextBlock
                {
                    Text = "Times are the first period of each day. Any second or third period already set on the camera is preserved.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 10)
                });
                panel.Children.Add(BuildScheduleGrid(section));
            }

            section.Warning = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Chocolate,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0)
            };
            panel.Children.Add(section.Warning);

            section.Commit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            section.Commit.Tick += async (_, _) => await CommitModuleAsync(section);

            ModuleTabs.Items.Add(new TabItem
            {
                Header = spec.Label,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                }
            });

            _modules.Add(section);
        }
    }

    private Grid BuildScheduleGrid(ModuleSection section)
    {
        var grid = NewFieldGrid();
        var days = _profile!.DetectorDefaults.Days;

        for (var day = 0; day < days.Count; day++)
        {
            var enable = new CheckBox
            {
                Content = days[day],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 8)
            };
            enable.Click += (_, _) => OnModuleChanged(section);

            var begin = new TextBox { Width = 60, Padding = new Thickness(4, 3, 4, 3) };
            begin.LostFocus += (_, _) => OnModuleChanged(section);
            begin.KeyDown += OnDetectorKeyDown;

            var end = new TextBox { Width = 60, Padding = new Thickness(4, 3, 4, 3) };
            end.LostFocus += (_, _) => OnModuleChanged(section);
            end.KeyDown += OnDetectorKeyDown;

            var times = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            times.Children.Add(begin);
            times.Children.Add(new TextBlock
            {
                Text = "to",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Foreground = System.Windows.Media.Brushes.Gray
            });
            times.Children.Add(end);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(enable, day);
            Grid.SetColumn(enable, 0);
            Grid.SetRow(times, day);
            Grid.SetColumn(times, 1);
            grid.Children.Add(enable);
            grid.Children.Add(times);

            section.Days.Add((enable, begin, end));
        }

        return grid;
    }

    /// <summary>
    /// Builds the control for one field spec. Shared by the Services modules and the
    /// detectors so both render identically and a new field is config, not code.
    /// </summary>
    private FrameworkElement MakeControl(ModuleFieldSpec field, Action onChanged)
    {
        switch (field.Type)
        {
            case "toggle":
            {
                var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                box.Click += (_, _) => onChanged();
                return box;
            }

            case "choice":
            {
                var box = new ComboBox { DisplayMemberPath = "Label", MinWidth = 160 };
                box.SelectionChanged += (_, _) => onChanged();
                return box;
            }

            case "password":
            {
                var box = new PasswordBox { Padding = new Thickness(4, 3, 4, 3), Width = 260 };
                if (field.MaxLength is { } passwordLimit) box.MaxLength = passwordLimit;

                box.LostFocus += (_, _) => onChanged();
                return box;
            }

            default:
            {
                var box = new TextBox
                {
                    Padding = new Thickness(4, 3, 4, 3),
                    Width = field.Type == "number" ? 90 : 260
                };
                if (field.MaxLength is { } limit) box.MaxLength = limit;

                box.LostFocus += (_, _) => onChanged();
                box.KeyDown += OnDetectorKeyDown;
                return box;
            }
        }
    }

    /// <summary>Renders a field list into a grid, collecting the controls.</summary>
    private int RenderFields(
        IEnumerable<ModuleFieldSpec> fields,
        Grid grid,
        int row,
        List<(ModuleFieldSpec Field, FrameworkElement Control)> sink,
        Action onChanged)
    {
        foreach (var field in fields)
        {
            var control = MakeControl(field, onChanged);

            var holder = new StackPanel { Orientation = Orientation.Horizontal };
            holder.Children.Add(control);

            if (!string.IsNullOrWhiteSpace(field.Suffix))
                holder.Children.Add(new TextBlock
                {
                    Text = field.Suffix,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    Foreground = System.Windows.Media.Brushes.Gray
                });

            AddField(grid, row++, field.Label, holder);
            sink.Add((field, control));
        }

        return row;
    }

    /// <summary>Fills rendered controls from a payload. Shared by modules and detectors.</summary>
    private static void LoadFields(
        JsonObject state, IEnumerable<(ModuleFieldSpec Field, FrameworkElement Control)> fields)
    {
        foreach (var (field, control) in fields)
        {
            var node = ReadPath(state, field.Key);

            switch (control)
            {
                case CheckBox box: box.IsChecked = ToInt(node) != 0; break;
                case ComboBox combo:
                    combo.ItemsSource = field.Options;
                    combo.SelectedItem = field.Options.FirstOrDefault(o => o.Value == ToInt(node));
                    break;
                case PasswordBox password: password.Password = node?.ToString() ?? ""; break;
                case TextBox text: text.Text = node?.ToString() ?? ""; break;
            }
        }
    }

    /// <summary>
    /// Writes rendered controls back into a payload. Returns the first validation
    /// failure, or null when every field was acceptable.
    /// </summary>
    private static string? StoreFields(
        JsonObject body, IEnumerable<(ModuleFieldSpec Field, FrameworkElement Control)> fields)
    {
        foreach (var (field, control) in fields)
        {
            switch (control)
            {
                case CheckBox box:
                    WritePath(body, field.Key, box.IsChecked == true ? 1 : 0);
                    break;

                case ComboBox combo when combo.SelectedItem is OptionSpec option:
                    WritePath(body, field.Key, option.Value);
                    break;

                case PasswordBox password:
                    WritePath(body, field.Key, password.Password);
                    break;

                case TextBox text when field.Type == "number":
                {
                    if (!int.TryParse(text.Text.Trim(), out var value) ||
                        value < (field.Min ?? int.MinValue) || value > (field.Max ?? int.MaxValue))
                        return $"{field.Label} must be a whole number between {field.Min} and {field.Max} - not applied.";

                    WritePath(body, field.Key, value);
                    break;
                }

                case TextBox text:
                    WritePath(body, field.Key, text.Text);
                    break;
            }
        }

        return null;
    }

    private static void DescribeModuleRegion(ModuleSection section)
    {
        if (section.RegionText is null || section.State is null) return;

        var count = Int(section.State, "rect_num");
        section.RegionText.Text = count == 0
            ? "No region set - nothing is masked."
            : $"{count} region(s) set.";
    }

    private void EditModuleRegion(ModuleSection section)
    {
        if (section.State is null) return;

        var maximum = (section.State["rect"] as JsonArray)?.Count ?? 4;

        var editor = new RegionEditor(
            string.IsNullOrWhiteSpace(section.Spec.RegionPrompt)
                ? "Drag on the picture to draw a region."
                : section.Spec.RegionPrompt,
            section.State["rect"] as JsonArray, maximum, VideoImage.Source) { Owner = this };

        if (editor.ShowDialog() != true) return;

        ApplyRectangles(section.State, editor.Rectangles, maximum);
        DescribeModuleRegion(section);

        OnModuleChanged(section);
    }

    private void OnModuleChanged(ModuleSection section)
    {
        if (!_uiReady || _suppressUiEvents) return;

        section.Commit.Stop();
        section.Commit.Start();
    }

    private async Task CommitModuleAsync(ModuleSection section)
    {
        section.Commit.Stop();

        if (_client is null || _profile is null || section.State is null) return;

        // Start from what the camera reported, so anything not listed in cameras.json
        // survives the write untouched.
        var body = (JsonObject)section.State.DeepClone();

        if (StoreFields(body, section.Fields) is { } invalid)
        {
            WarnModule(section, invalid);
            return;
        }

        if (section.Days.Count > 0)
        {
            var badDay = section.Days.FindIndex(d =>
                !TryParseTime(d.Begin.Text, out _, out _) || !TryParseTime(d.End.Text, out _, out _));

            if (badDay >= 0)
            {
                WarnModule(section, $"{_profile.DetectorDefaults.Days[badDay]} times must be HH:MM (00:00 to 24:00) - not applied.");
                return;
            }

            body["schedule"] = BuildModuleSchedule(section);
        }

        section.Warning.Visibility = Visibility.Collapsed;

        try
        {
            if (section.Spec.Channel is { } channel)
                await _client.SetChannelAsync(section.Spec.Module, section.Spec.SetCommand, channel, body);
            else
                await _client.SetModuleAsync(section.Spec.Module, body, section.Spec.SetCommand);

            section.State = body;
            SetStatus($"{section.Spec.Label} settings applied.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applying {section.Spec.Label} settings failed: {ex.Message}");
        }
    }

    private JsonArray BuildModuleSchedule(ModuleSection section)
    {
        var existing = section.State?["schedule"] as JsonArray;
        var schedule = new JsonArray();

        for (var day = 0; day < section.Days.Count; day++)
        {
            var entry = day < (existing?.Count ?? 0) && existing![day] is JsonObject previous
                ? (JsonObject)previous.DeepClone()
                : new JsonObject();

            TryParseTime(section.Days[day].Begin.Text, out var beginHour, out var beginMinute);
            TryParseTime(section.Days[day].End.Text, out var endHour, out var endMinute);

            entry["enable"] = section.Days[day].Enable.IsChecked == true ? 1 : 0;
            entry["begin1"] = Time(beginHour, beginMinute);
            entry["end1"] = Time(endHour, endMinute);

            for (var period = 2; period <= _profile!.DetectorDefaults.SchedulePeriods; period++)
            {
                entry[$"begin{period}"] ??= Time(0, 0);
                entry[$"end{period}"] ??= Time(0, 0);
            }

            schedule.Add(entry);
        }

        return schedule;
    }

    private void WarnModule(ModuleSection section, string message)
    {
        section.Warning.Text = message;
        section.Warning.Visibility = Visibility.Visible;
        SetStatus(message);
    }

    // ============================================================= detection

    /// <summary>Every control belonging to one detector, plus its last-read state.</summary>
    private sealed class DetectorSection
    {
        public DetectionSpec Spec = null!;
        public JsonObject? State;

        public CheckBox EnableBox = null!;
        public readonly List<(ModuleFieldSpec Field, FrameworkElement Control)> Fields = new();
        public Slider? SensitivitySlider;
        public TextBlock? SensitivityValue;
        public TextBox DurationBox = null!;
        public TextBlock? RegionText;
        public TextBlock Warning = null!;

        public readonly List<(LinkageSpec Spec, CheckBox Box)> Linkages = new();
        public readonly List<(WorkModeOutputSpec Spec, ComboBox Box)> WorkModes = new();
        public readonly List<(CheckBox Enable, TextBox Begin, TextBox End)> Days = new();

        public DispatcherTimer Commit = null!;
    }

    private readonly List<DetectorSection> _detectors = new();

    /// <summary>
    /// What the currently built tabs were built for. Two cameras of the same family
    /// can still differ - system_function says which features the unit actually has -
    /// so the tabs are rebuilt whenever that word or the matched profile changes,
    /// rather than once per run.
    /// </summary>
    private (string Profile, long Capabilities)? _uiBuiltFor;

    /// <summary>Discards the generated tabs so the next load rebuilds them.</summary>
    private void ResetGeneratedUi()
    {
        DetectorTabs.Items.Clear();
        ModuleTabs.Items.Clear();

        foreach (var section in _detectors) section.Commit.Stop();
        foreach (var section in _modules) section.Commit.Stop();

        _detectors.Clear();
        _modules.Clear();
    }

    /// <summary>
    /// True when the tabs already on screen belong to this camera. Rebuilding is
    /// cheap, but doing it on every connect would throw away the user's sub-tab
    /// selection for no reason.
    /// </summary>
    private bool GeneratedUiMatchesCamera()
    {
        var wanted = (_profile?.Name ?? "", DeviceCapabilities);

        if (_uiBuiltFor == wanted) return true;

        ResetGeneratedUi();
        _uiBuiltFor = wanted;
        return false;
    }

    /// <summary>The camera's capability word; absent bits hide the controls they gate.</summary>
    private long DeviceCapabilities =>
        _deviceInfo.TryGetValue("system_function", out var raw) && long.TryParse(raw, out var value) ? value : 0;

    private bool Supports(int? capabilityBit) =>
        capabilityBit is not { } bit || ((DeviceCapabilities >> bit) & 1) == 1;

    private async Task LoadDetectionAsync()
    {
        if (_client is null || _profile is null) return;

        // Must run before either builder: it clears both when the camera changed.
        GeneratedUiMatchesCamera();

        BuildDetectorTabs();
        DetectionTab.Visibility = _detectors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var section in _detectors)
        {
            try
            {
                section.State = await _client.GetModuleAsync(section.Spec.Module);
            }
            catch (CameraException)
            {
                // The capability word said it exists but the module does not answer;
                // leave that tab as it is rather than failing the whole connect.
                continue;
            }

            LoadDetector(section);
        }
    }

    private void LoadDetector(DetectorSection section)
    {
        if (section.State is null) return;

        var spec = section.Spec;
        var defaults = _profile!.DetectorDefaults;
        var state = section.State;

        _suppressUiEvents = true;
        try
        {
            section.EnableBox.IsChecked = Int(state, "enable") != 0;

            LoadFields(state, section.Fields);

            if (section.SensitivitySlider is not null)
            {
                section.SensitivitySlider.Minimum = defaults.SensitivityRange.Min;
                section.SensitivitySlider.Maximum = defaults.SensitivityRange.Max;
                section.SensitivitySlider.Value = Math.Clamp(
                    Int(state, "sensitivity"), defaults.SensitivityRange.Min, defaults.SensitivityRange.Max);
                section.SensitivityValue!.Text = ((int)section.SensitivitySlider.Value).ToString();
            }

            section.DurationBox.Text = Int(state, "duration").ToString();

            var output = Int(state, "output");
            foreach (var (linkage, box) in section.Linkages)
                box.IsChecked = ((output >> linkage.Bit) & 1) == 1;

            foreach (var (workMode, box) in section.WorkModes)
            {
                box.ItemsSource = defaults.WorkModes;

                // The output bit is authoritative, not the stored mode. This camera
                // reports voice_work_mode 2 with the voice bit clear; the firmware's
                // own page shows Close in that case, and reading the mode directly
                // would turn the linkage on the next time anything was saved.
                var mode = ((output >> workMode.Bit) & 1) == 1
                    ? Int(state, workMode.Key)
                    : DetectionSpec.WorkModeClose;

                SelectMode(box, defaults.WorkModes, mode);
            }

            LoadSchedule(section, state["schedule"] as JsonArray);

            DescribeRegion(section);

            section.Warning.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private static void LoadSchedule(DetectorSection section, JsonArray? schedule)
    {
        for (var day = 0; day < section.Days.Count; day++)
        {
            var entry = day < (schedule?.Count ?? 0) ? schedule![day] as JsonObject : null;

            section.Days[day].Enable.IsChecked = entry is not null && Int(entry, "enable") != 0;
            section.Days[day].Begin.Text = FormatTime(entry?["begin1"] as JsonObject);
            section.Days[day].End.Text = FormatTime(entry?["end1"] as JsonObject);
        }
    }

    /// <summary>The firmware stores an end of 24:00 to mean "to midnight".</summary>
    private static string FormatTime(JsonObject? time) =>
        time is null ? "00:00" : $"{Int(time, "hour"):00}:{Int(time, "minute"):00}";

    private static bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) return false;

        // 24:00 is legal as an end time, so the hour range is 0..24 rather than 0..23.
        return hour is >= 0 and <= 24 && minute is >= 0 and <= 59 && !(hour == 24 && minute > 0);
    }

    private void BuildDetectorTabs()
    {
        if (_detectors.Count > 0 || _profile is null) return;

        var defaults = _profile.DetectorDefaults;

        foreach (var spec in _profile.Detectors)
        {
            // A detector the hardware does not have gets no tab at all.
            if (!Supports(spec.CapabilityBit)) continue;

            var section = new DetectorSection { Spec = spec };
            var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left };

            section.EnableBox = new CheckBox
            {
                Content = "Alarm enable",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            section.EnableBox.Click += (_, _) => OnDetectorChanged(section);
            panel.Children.Add(section.EnableBox);

            var fields = NewFieldGrid();
            var row = RenderFields(spec.Fields, fields, 0, section.Fields, () => OnDetectorChanged(section));

            if (spec.HasSensitivity)
            {
                var slider = new Slider
                {
                    Width = 220,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = true,
                    TickFrequency = 1
                };
                var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 30 };

                slider.ValueChanged += (_, _) =>
                {
                    value.Text = ((int)slider.Value).ToString();
                    OnDetectorChanged(section);
                };

                section.SensitivitySlider = slider;
                section.SensitivityValue = value;

                var holder = new StackPanel { Orientation = Orientation.Horizontal };
                holder.Children.Add(slider);
                holder.Children.Add(value);

                AddField(fields, row++, $"Sensitivity ({defaults.SensitivityRange.Min}-{defaults.SensitivityRange.Max})", holder);
            }

            section.DurationBox = new TextBox { Width = 70, Padding = new Thickness(4, 3, 4, 3) };
            section.DurationBox.LostFocus += (_, _) => OnDetectorChanged(section);
            section.DurationBox.KeyDown += OnDetectorKeyDown;

            var duration = new StackPanel { Orientation = Orientation.Horizontal };
            duration.Children.Add(section.DurationBox);
            duration.Children.Add(new TextBlock
            {
                Text = "seconds",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = System.Windows.Media.Brushes.Gray
            });
            AddField(fields, row, "Alarm duration", duration);

            panel.Children.Add(fields);

            panel.Children.Add(GroupHeader("Linkage"));
            foreach (var linkage in spec.Linkages)
            {
                if (!Supports(linkage.CapabilityBit)) continue;

                var box = new CheckBox { Content = linkage.Label, Margin = new Thickness(0, 0, 0, 6) };
                box.Click += (_, _) => OnDetectorChanged(section);

                panel.Children.Add(box);
                section.Linkages.Add((linkage, box));
            }

            // A detector may carry its own set, or genuinely have none: the perimeter
            // detector has no work-mode fields in its payload at all.
            var workModes = spec.SuppressWorkModes
                ? new List<WorkModeOutputSpec>()
                : spec.WorkModeOutputs.Count > 0 ? spec.WorkModeOutputs : defaults.WorkModeOutputs;

            var offered = workModes.Where(w => Supports(w.CapabilityBit)).ToList();

            if (offered.Count > 0)
            {
                panel.Children.Add(GroupHeader("Outputs"));
                var outputs = NewFieldGrid();
                var outputRow = 0;

                foreach (var workMode in offered)
                {
                    var box = new ComboBox { DisplayMemberPath = "Label" };
                    box.SelectionChanged += (_, _) => OnDetectorChanged(section);

                    AddField(outputs, outputRow++, workMode.Label, box);
                    section.WorkModes.Add((workMode, box));
                }

                panel.Children.Add(outputs);
            }

            panel.Children.Add(GroupHeader("Schedule"));
            panel.Children.Add(new TextBlock
            {
                Text = "Times are the first period of each day. Any second or third period already set on the camera is preserved.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var schedule = NewFieldGrid();
            for (var day = 0; day < defaults.Days.Count; day++)
            {
                var enable = new CheckBox
                {
                    Content = defaults.Days[day],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 8)
                };
                enable.Click += (_, _) => OnDetectorChanged(section);

                var begin = new TextBox { Width = 60, Padding = new Thickness(4, 3, 4, 3) };
                begin.LostFocus += (_, _) => OnDetectorChanged(section);
                begin.KeyDown += OnDetectorKeyDown;

                var end = new TextBox { Width = 60, Padding = new Thickness(4, 3, 4, 3) };
                end.LostFocus += (_, _) => OnDetectorChanged(section);
                end.KeyDown += OnDetectorKeyDown;

                var times = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                times.Children.Add(begin);
                times.Children.Add(new TextBlock
                {
                    Text = "to",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    Foreground = System.Windows.Media.Brushes.Gray
                });
                times.Children.Add(end);

                schedule.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(enable, day);
                Grid.SetColumn(enable, 0);
                Grid.SetRow(times, day);
                Grid.SetColumn(times, 1);
                schedule.Children.Add(enable);
                schedule.Children.Add(times);

                section.Days.Add((enable, begin, end));
            }

            panel.Children.Add(schedule);

            if (spec.HasRegion)
            {
                panel.Children.Add(GroupHeader("Region"));

                var edit = new Button
                {
                    Content = "Edit region ...",
                    MinWidth = 130,
                    Padding = new Thickness(10, 5, 10, 5),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                edit.Click += (_, _) => EditRegion(section);
                panel.Children.Add(edit);

                section.RegionText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                panel.Children.Add(section.RegionText);
            }

            section.Warning = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = System.Windows.Media.Brushes.Chocolate,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 6, 0, 0)
            };
            panel.Children.Add(section.Warning);

            section.Commit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            section.Commit.Tick += async (_, _) => await CommitDetectorAsync(section);

            DetectorTabs.Items.Add(new TabItem
            {
                Header = spec.Label,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                }
            });

            _detectors.Add(section);
        }
    }

    private static Grid NewFieldGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static void AddField(Grid grid, int row, string label, UIElement control)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 8)
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Foreground = System.Windows.Media.Brushes.SteelBlue,
        Margin = new Thickness(0, 14, 0, 8)
    };

    private static void DescribeRegion(DetectorSection section)
    {
        if (section.RegionText is null || section.State is null) return;

        var count = Int(section.State, "rect_num");
        section.RegionText.Text = count == 0
            ? "No region set - the whole image is used."
            : $"{count} region(s) set.";
    }

    /// <summary>
    /// The camera stores rectangles normalised across the frame, so the still shown
    /// for drawing does not have to match the encoder resolution.
    /// </summary>
    private void EditRegion(DetectorSection section)
    {
        if (section.State is null) return;

        var maximum = (section.State["rect"] as JsonArray)?.Count ?? 4;

        var editor = new RegionEditor(
            $"Drag on the picture to draw a {section.Spec.Label.ToLowerInvariant()} detection region. " +
            $"Up to {maximum}; none means the whole image.",
            section.State["rect"] as JsonArray, maximum, VideoImage.Source) { Owner = this };

        if (editor.ShowDialog() != true) return;

        ApplyRectangles(section.State, editor.Rectangles, maximum);
        DescribeRegion(section);

        // Straight to the camera - the region is a deliberate action, not a stray edit.
        OnDetectorChanged(section);
    }

    /// <summary>
    /// Writes the drawn rectangles into the payload, padding the array back to the
    /// length the camera reported. The firmware always sends a fixed-length rect array
    /// with unused entries zeroed, and rejects a short one.
    /// </summary>
    private static void ApplyRectangles(JsonObject state, JsonArray drawn, int slots)
    {
        var rects = new JsonArray();

        for (var i = 0; i < slots; i++)
        {
            rects.Add(i < drawn.Count && drawn[i] is JsonObject rect
                ? (JsonObject)rect.DeepClone()
                : new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = 0, ["h"] = 0 });
        }

        state["rect"] = rects;
        state["rect_num"] = Math.Min(drawn.Count, slots);
    }

    private void OnDetectorChanged(DetectorSection section)
    {
        if (!_uiReady || _suppressUiEvents) return;

        section.Commit.Stop();
        section.Commit.Start();
    }

    private void OnDetectorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is not TextBox box) return;

        box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
    }

    private async Task CommitDetectorAsync(DetectorSection section)
    {
        section.Commit.Stop();

        if (_client is null || _profile is null || section.State is null) return;

        var spec = section.Spec;
        var defaults = _profile.DetectorDefaults;

        if (!int.TryParse(section.DurationBox.Text.Trim(), out var duration) ||
            duration < defaults.DurationRange.Min || duration > defaults.DurationRange.Max)
        {
            Warn(section, $"Alarm duration must be between {defaults.DurationRange.Min} and {defaults.DurationRange.Max} seconds - not applied.");
            return;
        }

        var badDay = section.Days.FindIndex(d =>
            !TryParseTime(d.Begin.Text, out _, out _) || !TryParseTime(d.End.Text, out _, out _));

        if (badDay >= 0)
        {
            Warn(section, $"{defaults.Days[badDay]} times must be HH:MM (00:00 to 24:00) - not applied.");
            return;
        }

        section.Warning.Visibility = Visibility.Collapsed;

        // Start from what the camera reported so fields this app does not surface -
        // threshold, colour, the detection rectangles, id - survive untouched.
        var body = (JsonObject)section.State.DeepClone();

        body["enable"] = section.EnableBox.IsChecked == true ? 1 : 0;
        body["duration"] = duration;

        if (StoreFields(body, section.Fields) is { } invalid)
        {
            Warn(section, invalid);
            return;
        }

        if (section.SensitivitySlider is not null)
            body["sensitivity"] = (int)section.SensitivitySlider.Value;

        var output = 0;
        foreach (var (linkage, box) in section.Linkages)
            if (box.IsChecked == true) output |= 1 << linkage.Bit;

        // The firmware derives these bits from the mode rather than a checkbox, and
        // writes mode 2 when the control is Closed. Matching that exactly.
        foreach (var (workMode, box) in section.WorkModes)
        {
            var mode = ModeValue(box, DetectionSpec.WorkModeClose);

            if (mode != DetectionSpec.WorkModeClose)
            {
                output |= 1 << workMode.Bit;
                body[workMode.Key] = mode;
            }
            else
            {
                body[workMode.Key] = 2;
            }
        }

        body["output"] = output;
        body["schedule"] = BuildSchedule(section, defaults);

        try
        {
            await _client.SetModuleAsync(spec.Module, body);
            section.State = body;
            SetStatus($"{spec.Label} settings applied.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applying {spec.Label} settings failed: {ex.Message}");
        }
    }

    private static JsonArray BuildSchedule(DetectorSection section, DetectorDefaults defaults)
    {
        var existing = section.State?["schedule"] as JsonArray;
        var schedule = new JsonArray();

        for (var day = 0; day < section.Days.Count; day++)
        {
            // Clone the day the camera reported, so periods 2 and 3 - which this app
            // does not show - are carried through rather than blanked.
            var entry = day < (existing?.Count ?? 0) && existing![day] is JsonObject previous
                ? (JsonObject)previous.DeepClone()
                : new JsonObject();

            TryParseTime(section.Days[day].Begin.Text, out var beginHour, out var beginMinute);
            TryParseTime(section.Days[day].End.Text, out var endHour, out var endMinute);

            entry["enable"] = section.Days[day].Enable.IsChecked == true ? 1 : 0;
            entry["begin1"] = Time(beginHour, beginMinute);
            entry["end1"] = Time(endHour, endMinute);

            for (var period = 2; period <= defaults.SchedulePeriods; period++)
            {
                entry[$"begin{period}"] ??= Time(0, 0);
                entry[$"end{period}"] ??= Time(0, 0);
            }

            schedule.Add(entry);
        }

        return schedule;
    }

    private static JsonObject Time(int hour, int minute) => new()
    {
        ["hour"] = hour,
        ["minute"] = minute,
        ["second"] = 0,
        ["reserve"] = 0
    };

    private void Warn(DetectorSection section, string message)
    {
        section.Warning.Text = message;
        section.Warning.Visibility = Visibility.Visible;
        SetStatus(message);
    }

    // =================================================================== osd

    private JsonObject? _osdState;
    private readonly List<(CheckBox Enable, TextBox Text)> _osdTextRows = new();

    private async Task LoadOsdAsync()
    {
        if (_client is null || _profile is null) return;

        var spec = _profile.Osd;

        // The channel travels in param2 on the read and in param on the write - the
        // firmware's own page is asymmetric here, and GetChannelAsync already matches.
        _osdState = await _client.GetChannelAsync(spec.Module, "get", spec.Channel);

        _suppressUiEvents = true;
        try
        {
            BuildOsdTextRows(spec);

            var datetime = _osdState["datetime"] as JsonObject ?? new JsonObject();

            OsdEnableBox.IsChecked = Int(_osdState, "enable") != 0;
            OsdDateEnableBox.IsChecked = Int(datetime, "enable") != 0;
            OsdShowWeekBox.IsChecked = Int(datetime, "show_week") != 0;

            OsdDateFormatBox.ItemsSource = spec.DateFormats;
            OsdTimeFormatBox.ItemsSource = spec.TimeFormats;
            OsdTimePositionBox.ItemsSource = spec.Positions;
            OsdTextPositionBox.ItemsSource = spec.Positions;

            SelectMode(OsdDateFormatBox, spec.DateFormats, Int(datetime, "date_fmt"));
            SelectMode(OsdTimeFormatBox, spec.TimeFormats, Int(datetime, "time_fmt"));
            SelectMode(OsdTimePositionBox, spec.Positions, Int(datetime, "pos"));

            var lines = _osdState["text"] as JsonArray;
            for (var i = 0; i < _osdTextRows.Count; i++)
            {
                var line = i < (lines?.Count ?? 0) ? lines![i] as JsonObject : null;
                _osdTextRows[i].Enable.IsChecked = line is not null && Int(line, "enable") != 0;
                _osdTextRows[i].Text.Text = line?["data"]?.ToString() ?? "";
            }

            // The vendor page keeps one position for every line; seed it from the
            // first line that has one rather than assuming they agree.
            var sharedPosition = lines?.OfType<JsonObject>()
                .Select(l => Int(l, "pos")).FirstOrDefault() ?? 0;
            SelectMode(OsdTextPositionBox, spec.Positions, sharedPosition);

            OsdTextWarning.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void BuildOsdTextRows(OsdSpec spec)
    {
        if (_osdTextRows.Count == spec.TextLines) return;

        OsdTextPanel.Children.Clear();
        _osdTextRows.Clear();

        for (var i = 0; i < spec.TextLines; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = $"Line {i + 1}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var enable = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            enable.Click += OnOsdToggled;
            row.Children.Add(enable);

            var text = new TextBox { Width = 250, Padding = new Thickness(4, 3, 4, 3) };
            text.LostFocus += OnOsdTextCommitted;
            text.KeyDown += OnOsdTextKeyDown;
            row.Children.Add(text);

            Grid.SetColumn(row, 1);
            grid.Children.Add(row);

            OsdTextPanel.Children.Add(grid);
            _osdTextRows.Add((enable, text));
        }
    }

    private void OnOsdToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ScheduleOsdCommit();
    }

    private void OnOsdChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ScheduleOsdCommit();
    }

    private void OnOsdTextCommitted(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        ScheduleOsdCommit();
    }

    private void OnOsdTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is not TextBox box) return;

        // Same route as tabbing away, so there is one commit path.
        box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
    }

    private void ScheduleOsdCommit()
    {
        _osdCommitTimer.Stop();
        _osdCommitTimer.Start();
    }

    /// <summary>
    /// Counts the way the firmware does: anything outside ASCII costs two. Its own
    /// page refuses to save past this, so the same limit is enforced here rather than
    /// letting the camera silently truncate.
    /// </summary>
    private static int OsdTextWidth(string text)
    {
        var wide = text.Count(c => c > 0x7F);
        return text.Length + wide;
    }

    private async Task CommitOsdAsync()
    {
        _osdCommitTimer.Stop();

        if (_client is null || _profile is null || _osdState is null) return;

        var spec = _profile.Osd;

        var tooLong = _osdTextRows
            .Select((row, index) => (index, width: OsdTextWidth(row.Text.Text)))
            .Where(r => r.width > spec.MaxTextWidth)
            .ToList();

        if (tooLong.Count > 0)
        {
            OsdTextWarning.Text =
                $"Line {string.Join(", ", tooLong.Select(t => t.index + 1))} is longer than " +
                $"{spec.MaxTextWidth} characters - not applied.";
            OsdTextWarning.Visibility = Visibility.Visible;
            return;
        }

        OsdTextWarning.Visibility = Visibility.Collapsed;

        var position = ModeValue(OsdTextPositionBox, 0);

        var text = new JsonArray();
        foreach (var (enable, box) in _osdTextRows)
            text.Add(new JsonObject
            {
                ["enable"] = enable.IsChecked == true ? 1 : 0,
                ["data"] = box.Text,
                ["pos"] = position
            });

        // Shaped exactly like the firmware page's own save: no_save is not sent.
        var body = new JsonObject
        {
            ["enable"] = OsdEnableBox.IsChecked == true ? 1 : 0,
            ["datetime"] = new JsonObject
            {
                ["enable"] = OsdDateEnableBox.IsChecked == true ? 1 : 0,
                ["show_week"] = OsdShowWeekBox.IsChecked == true ? 1 : 0,
                ["date_fmt"] = ModeValue(OsdDateFormatBox, 0),
                ["time_fmt"] = ModeValue(OsdTimeFormatBox, 1),
                ["pos"] = ModeValue(OsdTimePositionBox, 0)
            },
            ["text"] = text
        };

        try
        {
            await _client.SetChannelAsync(spec.Module, "set", spec.Channel, body);
            _osdState = body;
            SetStatus("OSD applied.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applying OSD failed: {ex.Message}");
        }
    }

    // ========================================================= image settings

    private async Task LoadImageAsync()
    {
        if (_client is null || _profile is null) return;

        _imageState = await _client.GetModuleAsync(_profile.Image.Module);
        BuildImageUi();
    }

    /// <summary>Generates the whole Image Settings pane from cameras.json.</summary>
    private void BuildImageUi()
    {
        ImagePanel.Children.Clear();
        if (_profile is null || _imageState is null) return;

        _suppressUiEvents = true;
        try
        {
            foreach (var group in _profile.Image.Groups)
            {
                var visible = group.Settings.Where(s => _imageState.ContainsKey(s.Key)).ToList();
                if (visible.Count == 0) continue; // firmware does not expose this group

                ImagePanel.Children.Add(new TextBlock
                {
                    Text = group.Name,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14,
                    Margin = new Thickness(0, 14, 0, 6),
                    Foreground = System.Windows.Media.Brushes.SteelBlue
                });

                foreach (var setting in visible)
                    ImagePanel.Children.Add(BuildSettingRow(setting));
            }

            if (ImagePanel.Children.Count == 0)
                ImagePanel.Children.Add(new TextBlock
                {
                    Text = "No settings in cameras.json matched the fields this camera reports.",
                    Foreground = System.Windows.Media.Brushes.DimGray
                });
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private UIElement BuildSettingRow(SettingSpec setting)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

        var label = new TextBlock
        {
            Text = setting.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = setting.Key
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        switch (setting.Type.ToLowerInvariant())
        {
            case "toggle":
            {
                var box = new CheckBox
                {
                    IsChecked = ReadInt(setting.Key) != 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = setting
                };
                box.Checked += OnToggleChanged;
                box.Unchecked += OnToggleChanged;
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
                break;
            }

            case "choice":
            {
                var options = OptionsFor(setting);

                var combo = new ComboBox
                {
                    ItemsSource = options,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinWidth = 190,
                    Tag = setting
                };

                var current = ReadInt(setting.Key);
                combo.SelectedItem = options.FirstOrDefault(o => o.Value == current);

                // An unlisted value would otherwise be silently rewritten.
                if (combo.SelectedItem is null && options.Count > 0)
                {
                    combo.ToolTip = $"Camera reported an unlisted value ({current}) for {setting.Key}.";
                    label.Foreground = System.Windows.Media.Brushes.Chocolate;
                }

                combo.SelectionChanged += OnChoiceChanged;
                Grid.SetColumn(combo, 1);
                grid.Children.Add(combo);
                break;
            }

            default: // slider
            {
                var slider = new Slider
                {
                    Minimum = setting.Min,
                    Maximum = setting.Max,
                    Value = Math.Clamp(ReadInt(setting.Key), setting.Min, setting.Max),
                    IsSnapToTickEnabled = true,
                    TickFrequency = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = setting
                };

                var readout = new TextBlock
                {
                    Text = ((int)slider.Value).ToString(),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = System.Windows.Media.Brushes.DimGray
                };

                slider.ValueChanged += (s, _) =>
                {
                    readout.Text = ((int)((Slider)s).Value).ToString();
                    OnSliderChanged(slider, setting);
                };

                Grid.SetColumn(slider, 1);
                grid.Children.Add(slider);
                Grid.SetColumn(readout, 2);
                grid.Children.Add(readout);
                break;
            }
        }

        return grid;
    }

    /// <summary>
    /// Resolves a choice's options: a fixed list, or one selected by another field's
    /// current value (exposure times differ under PAL and NTSC). Options gated to a
    /// chipset are dropped when the connected camera is not that chipset.
    /// </summary>
    private List<OptionSpec> OptionsFor(SettingSpec setting)
    {
        var options = setting.Options;

        if (setting.OptionsFrom is { } key)
        {
            var selector = ReadInt(key).ToString();
            options = setting.OptionSets.TryGetValue(selector, out var set)
                ? set
                : setting.OptionSets.Values.FirstOrDefault() ?? new List<OptionSpec>();
        }

        var cpu = _deviceInfo.GetValueOrDefault("cpu_type");
        return options.Where(o => o.AvailableOn(cpu)).ToList();
    }

    /// <summary>True when some other control's options are derived from this key.</summary>
    private bool IsOptionSelector(string key) =>
        _profile is not null &&
        _profile.Image.Groups
            .SelectMany(g => g.Settings)
            .Any(s => string.Equals(s.OptionsFrom, key, StringComparison.OrdinalIgnoreCase));

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        var box = (CheckBox)sender;
        var setting = (SettingSpec)box.Tag;
        WriteInt(setting.Key, box.IsChecked == true ? 1 : 0);
        ScheduleImageCommit();
    }

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        var combo = (ComboBox)sender;
        var setting = (SettingSpec)combo.Tag;
        if (combo.SelectedItem is not OptionSpec option) return;

        WriteInt(setting.Key, option.Value);
        ScheduleImageCommit();

        // Changing TV standard changes which exposure times exist, so any control
        // whose options derive from this one has to be rebuilt.
        if (IsOptionSelector(setting.Key)) BuildImageUi();
    }

    private void OnSliderChanged(Slider slider, SettingSpec setting)
    {
        if (_suppressUiEvents) return;

        var value = (int)slider.Value;
        WriteInt(setting.Key, value);

        // A slider with a single-parameter opcode gets an immediate write so dragging
        // feels responsive; the debounced whole-object commit follows and covers
        // everything else.
        if (setting.FastCmd is { } opcode)
        {
            _pendingFastWrites[opcode] = value;
            _throttle.Stop();
            _throttle.Start();
        }

        ScheduleImageCommit();
    }

    /// <summary>
    /// Every image change is written to the camera by itself - there is no Apply
    /// button. Writes are debounced so dragging a slider results in one commit once
    /// the value settles, not one per pixel.
    /// </summary>
    private void ScheduleImageCommit()
    {
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private async Task CommitImageAsync()
    {
        _commitTimer.Stop();

        if (_client is null || _profile is null || _imageState is null) return;

        try
        {
            await FlushFastWritesAsync();
            await _client.SetImageAsync(_profile.Image.Module, _profile.Image.Channel, _imageState);
            SetStatus("Image settings applied.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applying image settings failed: {ex.Message}");
        }
    }

    private async Task FlushFastWritesAsync()
    {
        _throttle.Stop();

        if (_client is null || _profile is null || _pendingFastWrites.Count == 0) return;

        var batch = new Dictionary<int, int>(_pendingFastWrites);
        _pendingFastWrites.Clear();

        foreach (var (opcode, value) in batch)
        {
            try
            {
                await _client.SetImageParamAsync(_profile.Image.Module, _profile.Image.Channel, opcode, value);
            }
            catch (Exception ex)
            {
                SetStatus($"Live update failed: {ex.Message}");
                return;
            }
        }
    }

    private int ReadInt(string key)
    {
        if (_imageState is null) return 0;
        if (!_imageState.TryGetPropertyValue(key, out var node) || node is null) return 0;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
            System.Text.Json.JsonValueKind.True => 1,
            System.Text.Json.JsonValueKind.False => 0,
            _ => int.TryParse(node.ToString(), out var parsed) ? parsed : 0
        };
    }

    private void WriteInt(string key, int value)
    {
        if (_imageState is null) return;
        _imageState[key] = value;
    }

    // ============================================================ provisioning

    private sealed record PresetFile(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private string PresetDirectory()
    {
        var configured = _config.Presets.Directory;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private void RefreshPresets()
    {
        var directory = PresetDirectory();
        var presets = new List<PresetFile>();

        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f))
                presets.Add(new PresetFile(Path.GetFileNameWithoutExtension(file), file));
        }
        catch (Exception ex)
        {
            Log($"Could not read presets from {directory}: {ex.Message}");
        }

        // Keep the current choice if it still exists - the list refreshes by itself
        // now, and having the selection jump while someone is working would be worse
        // than the stale list this replaced.
        var selected = (PresetBox.SelectedItem as PresetFile)?.Name;

        PresetBox.ItemsSource = presets;
        PresetBox.SelectedItem = presets.FirstOrDefault(p =>
            string.Equals(p.Name, selected, StringComparison.OrdinalIgnoreCase));

        if (PresetBox.SelectedItem is null && presets.Count > 0)
            PresetBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Watches the presets folder so the dropdown stays current when files are added,
    /// edited or removed - including from File > Open presets folder.
    /// </summary>
    private void StartWatchingPresets()
    {
        try
        {
            var directory = PresetDirectory();
            Directory.CreateDirectory(directory);

            _presetWatcher = new FileSystemWatcher(directory, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            // One save can raise several events, and the file may still be locked by
            // whatever wrote it, so coalesce them behind a short delay.
            void Bump(object? _, FileSystemEventArgs __) =>
                Dispatcher.BeginInvoke(() =>
                {
                    _presetRefreshTimer.Stop();
                    _presetRefreshTimer.Start();
                });

            _presetWatcher.Created += Bump;
            _presetWatcher.Deleted += Bump;
            _presetWatcher.Changed += Bump;
            _presetWatcher.Renamed += (_, _) =>
                Dispatcher.BeginInvoke(() =>
                {
                    _presetRefreshTimer.Stop();
                    _presetRefreshTimer.Start();
                });
        }
        catch (Exception ex)
        {
            // Without the watcher the list is simply as it was at startup.
            Log($"Not watching the presets folder: {ex.Message}");
        }
    }

    /// <summary>Preset subnets from the config, plus any local interface subnets.</summary>
    private List<string> ScanSubnets()
    {
        var subnets = new List<string>(_config.Discovery.Subnets);

        foreach (var local in Discovery.LocalSubnets())
            if (!subnets.Contains(local))
                subnets.Add(local);

        return subnets;
    }

    /// <summary>
    /// Progress reporting. The provisioning log panel is gone with the tab, so this
    /// goes to the status bar - discovery deliberately reports elsewhere, so nothing
    /// competes with it.
    /// </summary>
    private void Log(string message) => SetStatus(message);

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (_client is null || _profile is null) return;

        var suggested = _deviceInfo.TryGetValue("devtype", out var model) && model.Length > 0
            ? $"{model}-{DateTime.Now:yyyyMMdd-HHmm}.json"
            : $"camera-{DateTime.Now:yyyyMMdd-HHmm}.json";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export camera parameters",
            Filter = "Parameter file (*.json)|*.json|All files (*.*)|*.*",
            FileName = suggested,
            InitialDirectory = PresetDirectory()
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Directory.CreateDirectory(PresetDirectory());
            var snapshot = await CameraSnapshot.CaptureAsync(_client, _profile, _deviceInfo);
            snapshot.Save(dialog.FileName);

            Log($"Exported {_client.Host} to {dialog.FileName}");
            SetStatus($"Parameters exported to {Path.GetFileName(dialog.FileName)}");
            RefreshPresets();
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnImportClicked(object sender, RoutedEventArgs e)
    {
        if (_client is null || _profile is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import camera parameters",
            Filter = "Parameter file (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = PresetDirectory()
        };

        if (dialog.ShowDialog() != true) return;

        CameraSnapshot snapshot;
        try
        {
            snapshot = CameraSnapshot.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Addressing is always applied. A parameter file describes a whole camera, so
        // leaving part of it out made "import" mean two different things depending on
        // a checkbox nobody could see from the dialog.
        const bool includeNetwork = true;

        var warning = snapshot.Network?["addr"] is { } capturedAddress
            ? $"\n\nThis will also set its address to {capturedAddress}, and this connection will drop." +
              "\nIf that is a different camera's address, both will end up on it."
            : "";

        var confirm = MessageBox.Show(
            $"Apply {Path.GetFileName(dialog.FileName)} to {_client.Host}?\n\n" +
            $"Captured from {snapshot.SourceModel} ({snapshot.SourceFirmware}) on {snapshot.CreatedUtc}.{warning}",
            "Confirm import", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var wasPlaying = _ffmpeg?.IsRunning == true;

        try
        {
            StopPreview();
            var steps = await snapshot.ApplyAsync(_client, _profile, includeNetwork);

            Log($"Applied {Path.GetFileName(dialog.FileName)} to {_client.Host}:");
            foreach (var step in steps) Log($"  - {step}");

            if (snapshot.Network is null)
            {
                // The camera stayed put, so the open connection is still good and the
                // panels can be refreshed from it. When addressing was applied it has
                // moved, and discovery will pick it up at its new address instead.
                await LoadImageAsync();
                await LoadVideoAsync();
                SetStatus("Parameter file applied.");

                if (wasPlaying) StartPreview();
            }
            else
            {
                var movedTo = snapshot.Network["addr"]?.ToString();
                var from = _client.Host;

                if (string.IsNullOrWhiteSpace(movedTo) || string.Equals(movedTo, from, StringComparison.Ordinal))
                {
                    await LoadImageAsync();
                    await LoadVideoAsync();
                    SetStatus("Parameter file applied.");
                    if (wasPlaying) StartPreview();
                }
                else
                {
                    await FollowMoveAsync(from, movedTo);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private async void OnProvisionSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCamera is not { } camera || _client is null)
        {
            MessageBox.Show("Click a camera in the list first, so this applies to a known target.",
                "Not connected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (PresetBox.SelectedItem is not PresetFile preset)
        {
            MessageBox.Show(
                $"No preset selected.\n\nExport a configured camera into:\n{PresetDirectory()}\n\nIt will appear in the list as soon as it is saved.",
                "No preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CameraSnapshot snapshot;
        try
        {
            snapshot = CameraSnapshot.Load(preset.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Preset unreadable", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Same rule as import: a parameter file is applied whole.
        const bool applyNetwork = true;

        // The address is the only field worth calling out: it is the one that moves the
        // camera out from under this connection. A preset without addressing omits it.
        var newAddress = snapshot.Network?["addr"]?.ToString();
        var addressLine = string.IsNullOrWhiteSpace(newAddress)
            ? ""
            : $" New IP address is {newAddress}";

        var confirm = MessageBox.Show(
            $"Overwrite all settings from preset {preset.Name}?{addressLine}",
            "Confirm provisioning", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            Log($"Cancelled - {camera.Address} unchanged.");
            return;
        }

        ProvisionSelectedButton.IsEnabled = false;

        // Drop the RTSP stream first. The Video tab has always done this before an
        // encoder write; provisioning used to write the encoder with the preview still
        // pulling frames, which is the one difference between the two paths.
        var wasPlaying = _ffmpeg?.IsRunning == true;

        // A preset carrying the address it is already on does not move anything.
        var presetAddress = snapshot.Network?["addr"]?.ToString();
        var stayedPut = string.IsNullOrWhiteSpace(presetAddress) ||
                        string.Equals(presetAddress, camera.Address, StringComparison.Ordinal);

        StopPreview();

        try
        {
            SetStatus($"Provisioning {camera.Address} ...");

            // Writes are slower than reads - an encoder change restarts the capture
            // pipeline, and a network change reconfigures the interface mid-request.
            // The default 10s read timeout is too tight for those.
            using var client = new CameraClient(
                camera.Address, _config.Profiles[0].Auth, TimeSpan.FromSeconds(30));

            await client.LoginAsync(_activeUser, _activePassword);

            var profile = _config.MatchProfile(camera.Info);
            var steps = await snapshot.ApplyAsync(client, profile, applyNetwork);

            Log($"Applied '{preset.Name}' to {camera.Address}:");
            foreach (var step in steps) Log($"  - {step}");

            // A section that failed no longer takes the rest of the file with it, so
            // say plainly which ones did rather than showing one raw exception.
            var failures = steps.Where(s => s.Contains("FAILED")).ToList();
            if (failures.Count > 0)
            {
                MessageBox.Show(
                    $"'{preset.Name}' was applied to {camera.Address}, but {failures.Count} " +
                    $"section(s) did not take:" + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, failures.Select(f => "  - " + f)) +
                    Environment.NewLine + Environment.NewLine +
                    "Everything else was applied.",
                    "Applied with problems", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Refresh the row so the list reflects what the camera now reports. Not
            // worth doing when it has moved: that row is about to be removed, and the
            // address it left would only time out.
            if (stayedPut)
            {
                try
                {
                    var main = await client.GetChannelAsync(
                        profile.Video.Module, $"get_{profile.Video.MainCommandSuffix}", profile.Image.Channel);

                    var codec = main["enc_type"]?.ToString() switch
                    {
                        "0" => "H.264",
                        "1" => "H.265",
                        _ => "codec ?"
                    };
                    // LiveCamera raises change notifications, so the list updates itself.
                    camera.StreamSummary = $"{codec} {main["width"]}x{main["height"]} @ {main["framerate"]}fps";
                    UpdateProvisionTarget();
                }
                catch (CameraException) { /* row stays as it was */ }
            }

            // The tabs still show what the camera reported before the apply.
            if (stayedPut && _client is not null)
            {
                try
                {
                    await LoadImageAsync();
                    await LoadVideoAsync();
                }
                catch (CameraException) { /* tabs refresh on the next connect */ }
            }

            SetStatus($"{camera.Address} provisioned from '{preset.Name}'.");

            // The preset gave it a new address, so follow it there: the row it left is
            // removed and the one it arrives on is selected and connected.
            if (!stayedPut)
                await FollowMoveAsync(camera.Address, presetAddress!);
        }
        catch (Exception ex)
        {
            Log($"{camera.Address} - FAILED: {ex.Message}");
            SetStatus($"Provisioning failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Provisioning failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProvisionSelectedButton.IsEnabled = true;

            // Only worth resuming where the camera still is. When it moved,
            // FollowMoveAsync has already reconnected and restarted the preview at the
            // new address.
            if (wasPlaying && stayedPut) StartPreview();
        }
    }


    // ======================================================== stream / encoder

    private sealed record ResolutionOption(int Width, int Height)
    {
        public override string ToString() => $"{Width} x {Height}";
    }

    private bool EncUsesSubStream => EncStreamBox.SelectedIndex == 1;

    private string EncCommand(string verb) =>
        $"{verb}_{(EncUsesSubStream ? _profile!.Video.SubCommandSuffix : _profile!.Video.MainCommandSuffix)}";

    private async void OnEncStreamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents || _client is null) return;

        try
        {
            await LoadVideoAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Loading encoder settings failed: {ex.Message}");
        }
    }

    private async Task LoadVideoAsync()
    {
        if (_client is null || _profile is null) return;

        var spec = _profile.Video;
        var channel = _profile.Image.Channel;

        var current = await _client.GetChannelAsync(spec.Module, EncCommand("get"), channel);
        var ability = await _client.GetChannelAsync(spec.AbilityModule, EncCommand("get"), channel);

        _suppressUiEvents = true;
        try
        {
            // Codecs: only those the capability mask advertises.
            var mask = Int(ability, "venc_set");
            var codecs = spec.Codecs.Where(c => (mask & c.Bit) != 0).ToList();
            CodecBox.ItemsSource = codecs;

            var encType = Int(current, "enc_type");
            CodecBox.SelectedItem = codecs.FirstOrDefault(c => c.Value == encType);
            CodecHint.Text = codecs.Count > 1 ? "" : "camera reports only one codec";

            // Resolutions, dropping the 0x0 padding entries the firmware includes.
            var resolutions = new List<ResolutionOption>();
            if (ability["res_list"] is JsonArray list)
            {
                var count = Math.Min(Int(ability, "res_cnt"), list.Count);
                for (var i = 0; i < count; i++)
                {
                    if (list[i] is not JsonObject entry) continue;
                    var w = Int(entry, "width");
                    var h = Int(entry, "height");
                    if (w > 0 && h > 0) resolutions.Add(new ResolutionOption(w, h));
                }
            }

            ResolutionBox.ItemsSource = resolutions;
            var cw = Int(current, "width");
            var ch = Int(current, "height");
            ResolutionBox.SelectedItem = resolutions.FirstOrDefault(r => r.Width == cw && r.Height == ch);

            RateControlBox.ItemsSource = spec.RateControlModes;
            RateControlBox.SelectedItem = spec.RateControlModes.FirstOrDefault(o => o.Value == Int(current, "rc_mode"));

            QualityBox.ItemsSource = spec.QualityLevels;
            QualityBox.SelectedItem = spec.QualityLevels.FirstOrDefault(o => o.Value == Int(current, "quality"));

            FramerateBox.Text = Int(current, "framerate").ToString();
            BitrateBox.Text = Int(current, "bitrate").ToString();
            GopBox.Text = Int(current, "gop").ToString();

            _framerateRange = (Int(ability, "min_framerate"), Int(ability, "max_framerate"));
            _bitrateRange = (Int(ability, "min_bitrate"), Int(ability, "max_bitrate"));
            _gopRange = (Int(ability, "min_gop"), Int(ability, "max_gop"));

            FramerateRange.Text = $"{_framerateRange.Min} - {_framerateRange.Max} fps";
            BitrateRange.Text = $"{_bitrateRange.Min} - {_bitrateRange.Max} kbps";
            GopRange.Text = $"{_gopRange.Min} - {_gopRange.Max} frames";

            // Keep whatever the firmware reported but does not surface (e.g. smart_enable).
            _videoState = current;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        // ffmpeg is told the exact frame size, so both streams' geometry is cached
        // whichever one the encoder tab happens to be showing.
        var size = (Int(current, "width"), Int(current, "height"));
        if (size is { Item1: > 0, Item2: > 0 })
        {
            if (EncUsesSubStream) _subSize = size;
            else _mainSize = size;
        }
    }

    /// <summary>
    /// Reads both streams' resolutions once per connection. ffmpeg is handed a fixed
    /// output size, so the preview must know the real geometry of whichever stream it
    /// is about to play.
    /// </summary>
    private async Task CacheStreamSizesAsync()
    {
        if (_client is null || _profile is null) return;

        var video = _profile.Video;
        var channel = _profile.Image.Channel;

        foreach (var (suffix, isSub) in new[] { (video.MainCommandSuffix, false), (video.SubCommandSuffix, true) })
        {
            try
            {
                var stream = await _client.GetChannelAsync(video.Module, $"get_{suffix}", channel);
                var size = (Int(stream, "width"), Int(stream, "height"));
                if (size is not { Item1: > 0, Item2: > 0 }) continue;

                if (isSub) _subSize = size;
                else _mainSize = size;
            }
            catch (CameraException)
            {
                // Keep the defaults; the preview will simply scale.
            }
        }
    }

    private async void OnReloadVideoClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadVideoAsync();
            SetStatus("Encoder settings reloaded.");
        }
        catch (Exception ex)
        {
            SetStatus($"Reload failed: {ex.Message}");
        }
    }

    /// <summary>Enter commits the field being edited, by moving focus off it.</summary>
    private void OnFieldKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is not TextBox box) return;

        // Moving focus raises LostFocus, which is where the commit happens - so both
        // routes go through exactly one code path.
        box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
    }

    private async void OnVideoChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        await ApplyVideoAsync();
    }

    private async void OnVideoFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        await ApplyVideoAsync();
    }

    private async Task ApplyVideoAsync()
    {
        if (_client is null || _profile is null || _videoState is null) return;

        // Committing on every edit means invalid input is a normal, transient state -
        // report it in the status bar and wait, rather than interrupting with a dialog.
        if (!TryReadRange(FramerateBox, _framerateRange, "Frame rate", out var framerate)) return;
        if (!TryReadRange(BitrateBox, _bitrateRange, "Bitrate", out var bitrate)) return;
        if (!TryReadRange(GopBox, _gopRange, "GOP", out var gop)) return;

        if (CodecBox.SelectedItem is not CodecSpec codec ||
            ResolutionBox.SelectedItem is not ResolutionOption resolution ||
            RateControlBox.SelectedItem is not OptionSpec rateControl ||
            QualityBox.SelectedItem is not OptionSpec quality)
            return;

        var body = (JsonObject)_videoState.DeepClone();
        body["enc_type"] = codec.Value;
        body["width"] = resolution.Width;
        body["height"] = resolution.Height;
        body["framerate"] = framerate;
        body["rc_mode"] = rateControl.Value;
        body["bitrate"] = bitrate;
        body["gop"] = gop;
        body["quality"] = quality.Value;

        var wasPlaying = _ffmpeg?.IsRunning == true;

        try
        {
            StopPreview();
            await _client.SetChannelAsync(_profile.Video.Module, EncCommand("set"), _profile.Image.Channel, body);
            SetStatus($"Encoder set to {codec.Label} {resolution} @ {framerate} fps.");

            await Task.Delay(1200); // let the encoder restart before we reconnect
            await LoadVideoAsync();

            if (wasPlaying) StartPreview();
        }
        catch (Exception ex)
        {
            SetStatus($"Applying encoder settings failed: {ex.Message}");
            MessageBox.Show(ex.Message, "Apply failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
        }
    }

    private bool TryReadRange(TextBox box, (int Min, int Max) range, string name, out int value)
    {
        if (!int.TryParse(box.Text.Trim(), out value) || value < range.Min || value > range.Max)
        {
            SetStatus($"{name} must be a whole number between {range.Min} and {range.Max} - not applied.");
            return false;
        }
        return true;
    }

    // =============================================================== preview

    private void OnStreamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressUiEvents) return;
        UpdateStreamUrlText();

        if (_ffmpeg?.IsRunning == true)
            StartPreview();
    }

    private bool UseSubStream => StreamBox.SelectedIndex == 1;

    private string? CurrentStreamUrl()
    {
        if (_client is null || _profile is null) return null;
        return CameraClient.BuildRtspUrl(_client.Host, _activeUser, _activePassword, _profile.Rtsp, UseSubStream);
    }

    private void UpdateStreamUrlText()
    {
        if (_client is null || _profile is null)
        {
            StreamUrlText.Text = "";
            return;
        }

        var path = UseSubStream ? _profile.Rtsp.SubPath : _profile.Rtsp.MainPath;
        StreamUrlText.Text = $"rtsp://{_client.Host}:{_profile.Rtsp.Port}{path}";
    }

    private void StartPreview()
    {
        if (_previewDisabled || _ffmpeg is null || _profile is null) return;

        var url = CurrentStreamUrl();
        if (url is null) return;

        StopPreview();

        var (width, height) = UseSubStream ? _subSize : _mainSize;
        var tcp = string.Equals(_profile.Rtsp.Transport, "tcp", StringComparison.OrdinalIgnoreCase);

        _ffmpeg.Start(url, width, height, tcp);
        SetStatus($"Playing {(UseSubStream ? "sub" : "main")} stream from {_client!.Host} ({width}x{height})");
    }


    /// <summary>
    /// The frame counters are no longer shown on the window. A stalled preview is
    /// still worth reporting, so it goes to the status bar - and only when it is
    /// actually stalled, rather than as a permanent readout.
    /// </summary>
    private void UpdatePreviewStats()
    {
        if (_ffmpeg is null) return;

        var drawn = _ffmpeg.FramesRendered;

        var step = drawn - _lastRendered;
        _lastRendered = drawn;

        // Only complain once frames have started and then stopped; ffmpeg's start-up
        // stderr chatter is normal and self-correcting.
        if (drawn > 0 && step == 0 && _client is not null)
            SetStatus("Preview has stopped receiving frames.");
    }

    private void StopPreview() => _ffmpeg?.Stop();

    // ================================================================= misc

    private static string Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : "";

    private static int Int(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), out var value) ? value : 0;

    private void SetStatus(string message) => StatusText.Text = message;

    private void Teardown()
    {
        _throttle.Stop();
        _presetRefreshTimer.Stop();
        _networkScope.RemoveTemporaryAddresses();

        // If a borrowed address could not be handed back - elevation declined, say -
        // it stays recorded, and the user is told rather than left with a silent
        // change to their adapter.
        if (_networkScope.Stranded() is { Count: > 0 } stranded)
        {
            MessageBox.Show(
                "These temporary addresses could not be removed and are still on the adapter:" +
                Environment.NewLine + Environment.NewLine +
                "    " + string.Join(Environment.NewLine + "    ", stranded) +
                Environment.NewLine + Environment.NewLine +
                "They will be removed the next time this app starts, or you can remove them " +
                "now from an administrator prompt with:" + Environment.NewLine +
                "    netsh interface ipv4 delete address name=\"<adapter>\" address=<address>",
                "Temporary addresses remain", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _presetWatcher?.Dispose();
        _discovery?.Dispose();

        StopPreview();
        _ffmpeg?.Dispose();
        _client?.Dispose();
    }
}













using System.Windows;

namespace RTSPCameraConfigurator;

public partial class SettingsDialog : Window
{
    private const string AutomaticEntry = "Automatic  -  the adapter carrying the most addresses";

    public List<string> Subnets { get; private set; } = new();
    public bool Continuous { get; private set; }
    public int RefreshSeconds { get; private set; }

    /// <summary>Empty means automatic - let NetworkScope pick.</summary>
    public string InterfaceAlias { get; private set; } = "";

    public SettingsDialog(IEnumerable<string> subnets, bool continuous, int refreshSeconds, string interfaceAlias)
    {
        InitializeComponent();

        SubnetsBox.Text = string.Join(Environment.NewLine, subnets);
        ContinuousCheck.IsChecked = continuous;
        IntervalBox.Text = refreshSeconds.ToString();

        // "Automatic" first, then the adapters as they are now, so the choice can be
        // made on what each one currently carries rather than on its name alone.
        AdapterBox.Items.Add(AutomaticEntry);
        foreach (var adapter in NetworkScope.Adapters()) AdapterBox.Items.Add(adapter);

        AdapterBox.SelectedItem = AdapterBox.Items
            .OfType<NetworkScope.Adapter>()
            .FirstOrDefault(a => string.Equals(a.Alias, interfaceAlias, StringComparison.OrdinalIgnoreCase));

        // A configured adapter that is not present right now must not be silently
        // dropped, so it is offered as-is rather than falling back to automatic.
        if (AdapterBox.SelectedItem is null && !string.IsNullOrWhiteSpace(interfaceAlias))
        {
            var missing = new NetworkScope.Adapter(interfaceAlias, "", "not present", false, "?");
            AdapterBox.Items.Add(missing);
            AdapterBox.SelectedItem = missing;
        }

        AdapterBox.SelectedItem ??= AutomaticEntry;

        Loaded += (_, _) => SubnetsBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();

        foreach (var line in SubnetsBox.Text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var normalised = ConfigFile.NormaliseSubnet(trimmed);
            if (normalised is null) rejected.Add(trimmed);
            else if (!accepted.Contains(normalised)) accepted.Add(normalised);
        }

        if (rejected.Count > 0)
        {
            Fail($"Not a valid subnet: {string.Join(", ", rejected)}." +
                 Environment.NewLine +
                 "Use the first three octets, e.g. 192.168.1");
            return;
        }

        if (accepted.Count == 0)
        {
            Fail("Enter at least one subnet, otherwise the app has nowhere to look.");
            return;
        }

        if (!int.TryParse(IntervalBox.Text.Trim(), out var seconds) || seconds < 5 || seconds > 3600)
        {
            Fail("The interval must be a whole number of seconds between 5 and 3600.");
            return;
        }

        Subnets = accepted;
        Continuous = ContinuousCheck.IsChecked == true;
        RefreshSeconds = seconds;
        InterfaceAlias = AdapterBox.SelectedItem is NetworkScope.Adapter chosen ? chosen.Alias : "";
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

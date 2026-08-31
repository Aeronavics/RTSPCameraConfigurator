using System.Windows;
using System.Windows.Controls;

namespace RTSPCameraConfigurator;

/// <summary>
/// Asks which adapter to borrow an address on, at the moment it is about to happen.
///
/// Choosing automatically by address count is wrong exactly where it matters most: a
/// laptop on Wi-Fi with the camera on a wired link will usually have more addresses
/// on the wireless adapter, so the wired one - the only one that can see the camera -
/// loses.
/// </summary>
public partial class AdapterDialog : Window
{
    /// <summary>What the list shows for each adapter.</summary>
    public sealed record Row(string Alias, string Kind, string Detail, bool Dhcp);

    public string SelectedAlias { get; private set; } = "";
    public bool Remember { get; private set; }

    public AdapterDialog(IEnumerable<string> unreachableSubnets,
                         IReadOnlyList<NetworkScope.Adapter> adapters,
                         string preferredAlias)
    {
        InitializeComponent();

        SubnetsText.Text = string.Join(Environment.NewLine,
            unreachableSubnets.Select(s => $"{s}.0/24"));

        foreach (var adapter in adapters)
        {
            var where = string.IsNullOrWhiteSpace(adapter.Addresses)
                ? "no IPv4 address"
                : adapter.Addresses;

            AdapterList.Items.Add(new Row(
                adapter.Alias,
                adapter.Kind,
                $"{where}{(adapter.Dhcp ? "   ·   DHCP" : "")}   ·   {adapter.Description}",
                adapter.Dhcp));
        }

        AdapterList.SelectedItem = AdapterList.Items.OfType<Row>()
            .FirstOrDefault(r => string.Equals(r.Alias, preferredAlias, StringComparison.OrdinalIgnoreCase))
            ?? AdapterList.Items.OfType<Row>().FirstOrDefault();

        // Pre-ticked when the choice came from the config, since it was deliberate.
        RememberCheck.IsChecked = !string.IsNullOrWhiteSpace(preferredAlias);

        UpdateWarning();
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e) => UpdateWarning();

    private void UpdateWarning()
    {
        var row = AdapterList.SelectedItem as Row;

        DhcpWarning.Visibility = row is { Dhcp: true } ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = row is not null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (AdapterList.SelectedItem is not Row row) return;

        SelectedAlias = row.Alias;
        Remember = RememberCheck.IsChecked == true;
        DialogResult = true;
    }
}

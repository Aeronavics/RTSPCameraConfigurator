using System.Windows;

namespace RtspCameraSetup;

public partial class SettingsDialog : Window
{
    public List<string> Subnets { get; private set; } = new();
    public bool Continuous { get; private set; }
    public int RefreshSeconds { get; private set; }

    public SettingsDialog(IEnumerable<string> subnets, bool continuous, int refreshSeconds)
    {
        InitializeComponent();

        SubnetsBox.Text = string.Join(Environment.NewLine, subnets);
        ContinuousCheck.IsChecked = continuous;
        IntervalBox.Text = refreshSeconds.ToString();

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
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

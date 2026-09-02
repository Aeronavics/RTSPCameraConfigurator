using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RTSPCameraConfigurator;

/// <summary>
/// Reference information about the app. Reached from the menu bar rather than the
/// settings tabs: it describes the application, not the camera being configured.
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog(string configPath, string presetDirectory, string credentialPath)
    {
        InitializeComponent();

        VersionText.Text = DescribeVersion();

        var row = 0;
        AddRow(ref row, "Configuration", configPath);
        AddRow(ref row, "Your settings", AppConfig.UserSettingsPath);
        AddRow(ref row, "Presets", presetDirectory);
        AddRow(ref row, "Saved credentials", credentialPath);
        AddRow(ref row, "Crash log", AppData.File("crash.log"));
        AddRow(ref row, "Discovery log", AppData.File("discovery.log"));
    }

    /// <summary>
    /// Taken from the assembly rather than a literal, so bumping the version in the
    /// csproj is enough - there is nothing here to forget to update.
    /// </summary>
    private static string DescribeVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        var version = System.Reflection.CustomAttributeExtensions
                          .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
                          ?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        // Strip the source-revision suffix the SDK appends.
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        var built = File.Exists(assembly.Location)
            ? File.GetLastWriteTime(assembly.Location).ToString("d MMM yyyy")
            : "";

        return string.IsNullOrEmpty(built) ? $"Version {version}" : $"Version {version}  ·  built {built}";
    }

    private void AddRow(ref int row, string label, string value)
    {
        PathsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 8, 4)
        };
        Grid.SetRow(name, row);
        Grid.SetColumn(name, 0);
        PathsGrid.Children.Add(name);

        // A read-only TextBox rather than a TextBlock: these are paths, and being able
        // to select and copy one is the whole point of showing it.
        var text = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        PathsGrid.Children.Add(text);

        row++;
    }

    private void OnLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is a convenience; failing to is not worth an error.
        }

        e.Handled = true;
    }
}

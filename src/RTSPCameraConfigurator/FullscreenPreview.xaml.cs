using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace RTSPCameraConfigurator;

/// <summary>
/// The preview on its own, decoded at the display's resolution rather than the small
/// pane's. Closing hands the stream back to the main window.
/// </summary>
public partial class FullscreenPreview : Window
{
    private readonly DispatcherTimer _hint = new() { Interval = TimeSpan.FromSeconds(4) };

    public System.Windows.Controls.Image Surface => VideoImage;

    public FullscreenPreview()
    {
        InitializeComponent();

        // The hint has done its job after a few seconds; leaving it on top of the
        // picture would be the only thing wrong with the view.
        _hint.Tick += (_, _) => { _hint.Stop(); HintText.Visibility = Visibility.Collapsed; };
        Loaded += (_, _) => _hint.Start();
        Closed += (_, _) => _hint.Stop();
    }

    /// <summary>
    /// The screen size in real pixels. WPF works in device-independent units, so on a
    /// scaled display those are not the same thing - and the point here is to decode
    /// at what the panel can actually show.
    /// </summary>
    public (int Width, int Height) ScreenPixels()
    {
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformToDevice;

        var scaleX = m?.M11 ?? 1.0;
        var scaleY = m?.M22 ?? 1.0;

        return ((int)Math.Round(SystemParameters.PrimaryScreenWidth * scaleX),
                (int)Math.Round(SystemParameters.PrimaryScreenHeight * scaleY));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11) Close();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Close();
}

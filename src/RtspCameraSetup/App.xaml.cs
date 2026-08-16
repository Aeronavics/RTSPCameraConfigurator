using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RtspCameraSetup;

public partial class App : Application
{
    /// <summary>Null when libvlc loaded; otherwise why preview is unavailable.</summary>
    public static string? VideoInitError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background failure (a camera going away mid-request, a socket reset)
        // should surface as a message, not kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Must happen before any VideoView is constructed. MainWindow's XAML
        // contains one, so doing this in the window constructor is too late:
        // the element throws a NullReferenceException as the tree is built.
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            VideoInitError = ex.Message;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = WriteCrashLog(e.Exception);
        var detail = log is null ? "" : $"{Environment.NewLine}{Environment.NewLine}Details written to:{Environment.NewLine}{log}";

        MessageBox.Show(Describe(e.Exception) + detail, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Writes the full exception, including stack traces, next to the app data.</summary>
    private static string? WriteCrashLog(Exception exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CameraSetup");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "crash.log");
            File.WriteAllText(path, $"{DateTime.Now:u}{Environment.NewLine}{exception}");
            return path;
        }
        catch
        {
            return null; // never let logging failure mask the original problem
        }
    }

    /// <summary>
    /// Walks the exception chain. The outermost message is often a useless wrapper
    /// ("Exception has been thrown by the target of an invocation"), so the inner
    /// causes are what actually identify the problem.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var lines = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
            lines.Add($"{current.GetType().Name}: {current.Message}");

        return string.Join(Environment.NewLine + "  -> ", lines);
    }
}

using System.IO;

namespace RTSPCameraConfigurator;

/// <summary>
/// Where per-user state lives: saved credentials, the temporary-address journal and
/// the crash log.
///
/// The folder was called "CameraSetup" before the app was renamed. Renaming it
/// outright would silently orphan saved credentials, so an existing folder is moved
/// across once and the old name is never used again.
/// </summary>
public static class AppData
{
    private const string FolderName = "RTSPCameraConfigurator";
    private const string PreviousFolderName = "CameraSetup";

    private static string? _directory;

    public static string Directory
    {
        get
        {
            if (_directory is not null) return _directory;

            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var current = Path.Combine(root, FolderName);
            var previous = Path.Combine(root, PreviousFolderName);

            try
            {
                if (!System.IO.Directory.Exists(current) && System.IO.Directory.Exists(previous))
                    System.IO.Directory.Move(previous, current);

                System.IO.Directory.CreateDirectory(current);
            }
            catch
            {
                // A failed migration must not stop the app starting; the worst case is
                // that saved credentials have to be entered again.
            }

            return _directory = current;
        }
    }

    public static string File(string name) => Path.Combine(Directory, name);
}

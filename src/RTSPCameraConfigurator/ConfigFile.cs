using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RTSPCameraConfigurator;

/// <summary>
/// Edits cameras.json in place from the Settings dialog.
///
/// Deliberately works on a JsonNode rather than round-tripping through
/// <see cref="AppConfig"/>: the file carries "$comment" keys and any profile fields a
/// future firmware needs, and serialising the typed model back would silently discard
/// everything the model does not know about. Only the keys being edited are touched.
/// </summary>
public static class ConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// The keys the Settings dialog owns. Everything else in cameras.json belongs to the
    /// build, so only these are carried across an upgrade.
    /// </summary>
    private static readonly string[] DiscoveryKeys =
        { "subnets", "continuous", "refreshSeconds", "interfaceAlias" };

    /// <summary>
    /// Writes the operator's choices to their own settings file, which is overlaid on the
    /// shipped cameras.json at startup. Writing them back into cameras.json instead is what
    /// forced the installer to leave that file alone on an upgrade, which in turn meant a
    /// new camera profile never reached an existing install.
    /// </summary>
    public static void UpdateDiscovery(
        string path,
        IEnumerable<string> subnets,
        bool continuous,
        int refreshSeconds,
        string interfaceAlias)
    {
        var root = ReadOrEmpty(path);

        if (root["discovery"] is not JsonObject discovery)
        {
            discovery = new JsonObject();
            root["discovery"] = discovery;
        }

        var list = new JsonArray();
        foreach (var subnet in subnets) list.Add(subnet);

        discovery["subnets"] = list;
        discovery["continuous"] = continuous;
        discovery["refreshSeconds"] = refreshSeconds;
        discovery["interfaceAlias"] = interfaceAlias;

        Write(path, root);
    }

    private static JsonObject ReadOrEmpty(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        return JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject
               ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a JSON object.");
    }

    private static void Write(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write via a temporary file so a failure part-way cannot leave a truncated
        // config that the app would refuse to start with next time.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Lifts the operator's discovery choices out of a cameras.json an older build wrote
    /// them into, so an upgrade does not silently reset the watched subnets. Runs once:
    /// having a settings file at all means the move has already happened.
    /// </summary>
    public static void MigrateUserSettings(string legacyConfigPath, string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath) || !File.Exists(legacyConfigPath)) return;

            if (ReadOrEmpty(legacyConfigPath)["discovery"] is not JsonObject legacy) return;

            var carried = new JsonObject();
            foreach (var key in DiscoveryKeys)
                if (legacy[key] is { } value) carried[key] = value.DeepClone();

            if (carried.Count == 0) return;

            Write(settingsPath, new JsonObject { ["discovery"] = carried });
        }
        catch
        {
            // A failed migration must not stop the app starting; the shipped defaults
            // still work, and the dialog can set them again.
        }
    }

    /// <summary>Normalises "192.168.1.10", "192.168.1." or "192.168.1" to "192.168.1".</summary>
    public static string? NormaliseSubnet(string value)
    {
        var parts = value.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return null;

        for (var i = 0; i < 3; i++)
            if (!byte.TryParse(parts[i], out _))
                return null;

        return string.Join('.', parts.Take(3));
    }
}

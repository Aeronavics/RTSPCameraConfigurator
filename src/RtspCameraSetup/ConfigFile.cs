using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtspCameraSetup;

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

    public static void UpdateDiscovery(
        string path,
        IEnumerable<string> subnets,
        bool continuous,
        int refreshSeconds)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), NodeOptions, DocumentOptions) as JsonObject
                   ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a JSON object.");

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

        // Write via a temporary file so a failure part-way cannot leave a truncated
        // config that the app would refuse to start with next time.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
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

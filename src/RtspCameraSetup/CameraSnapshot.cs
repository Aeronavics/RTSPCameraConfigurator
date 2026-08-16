using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RtspCameraSetup;

/// <summary>
/// A portable capture of one camera's settings - the "param file".
///
/// Read a camera into a snapshot, save it, then replay it onto other cameras. The
/// payloads are stored as the firmware's own JSON objects rather than a translated
/// model, so fields this app does not surface still round-trip intact.
/// </summary>
public sealed class CameraSnapshot
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("sourceAddress")] public string SourceAddress { get; set; } = "";
    [JsonPropertyName("sourceModel")] public string SourceModel { get; set; } = "";
    [JsonPropertyName("sourceFirmware")] public string SourceFirmware { get; set; } = "";
    [JsonPropertyName("profileName")] public string ProfileName { get; set; } = "";

    [JsonPropertyName("image")] public JsonObject? Image { get; set; }
    [JsonPropertyName("videoMain")] public JsonObject? VideoMain { get; set; }
    [JsonPropertyName("videoSub")] public JsonObject? VideoSub { get; set; }

    /// <summary>
    /// Addressing, when the file carries any. A file is always applied to exactly one
    /// camera, so this is applied like every other section; a preset that should not
    /// move cameras simply omits it.
    /// </summary>
    [JsonPropertyName("network")] public JsonObject? Network { get; set; }

    /// <summary>
    /// Everything else the app can configure, keyed by the spec key from cameras.json:
    /// the detectors, the OSD, and every entry in "modules".
    ///
    /// The set is derived from the configuration rather than listed here, so whatever
    /// the app can edit is automatically what a parameter file carries - a new module
    /// in cameras.json is captured and replayed with no change to this class.
    /// </summary>
    [JsonPropertyName("modules")] public JsonObject? Modules { get; set; }

    /// <summary>One capturable section: where to read it, where to write it back.</summary>
    private sealed record Part(
        string Key,
        string Module,
        string GetCommand,
        string SetCommand,
        int? Channel,
        IReadOnlyCollection<string> ReadOnlyKeys);

    /// <summary>
    /// Ordered so the riskiest writes come last: everything ordinary, then the
    /// encoder (which restarts the capture pipeline), then addressing (which moves
    /// the camera out from under the connection).
    /// </summary>
    private static List<Part> PartsOf(CameraProfile profile)
    {
        var parts = new List<Part>();

        foreach (var detector in profile.Detectors)
            parts.Add(new Part(detector.Key, detector.Module, "get", "set", null, detector.ReadOnlyKeys));

        parts.Add(new Part("osd", profile.Osd.Module, "get", "set", profile.Osd.Channel, profile.Osd.ReadOnlyKeys));

        foreach (var module in profile.Modules)
            parts.Add(new Part(module.Key, module.Module, module.GetCommand, module.SetCommand,
                               module.Channel, module.ReadOnlyKeys));

        return parts;
    }

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<CameraSnapshot> CaptureAsync(
        CameraClient client,
        CameraProfile profile,
        IReadOnlyDictionary<string, string> deviceInfo,
        CancellationToken ct = default)
    {
        var channel = profile.Image.Channel;
        var video = profile.Video;

        var snapshot = new CameraSnapshot
        {
            CreatedUtc = DateTime.UtcNow.ToString("u"),
            SourceAddress = client.Host,
            ProfileName = profile.Name,
            SourceModel = deviceInfo.TryGetValue("devtype", out var model) ? model : "",
            SourceFirmware = deviceInfo.TryGetValue("version", out var version) ? version : "",
            Image = Strip(await client.GetModuleAsync(profile.Image.Module, ct), profile.Image.ReadOnlyKeys),
            Network = Strip(await client.GetModuleAsync(profile.Network.Module, ct), profile.Network.ReadOnlyKeys)
        };

        // Encoder settings are optional: a model without these endpoints still
        // produces a valid image-only snapshot.
        try
        {
            snapshot.VideoMain = Strip(await client.GetChannelAsync(
                video.Module, $"get_{video.MainCommandSuffix}", channel, ct), video.ReadOnlyKeys);
            snapshot.VideoSub = Strip(await client.GetChannelAsync(
                video.Module, $"get_{video.SubCommandSuffix}", channel, ct), video.ReadOnlyKeys);
        }
        catch (CameraException)
        {
            // leave the video sections null
        }

        // Everything else the app knows how to configure. A module the firmware does
        // not implement is skipped rather than failing the whole capture, so one
        // config can serve models with different feature sets.
        var modules = new JsonObject();

        foreach (var part in PartsOf(profile))
        {
            try
            {
                var payload = part.Channel is { } partChannel
                    ? await client.GetChannelAsync(part.Module, part.GetCommand, partChannel, ct)
                    : await client.GetModuleAsync(part.Module, part.GetCommand, ct);

                modules[part.Key] = Strip(payload, part.ReadOnlyKeys);
            }
            catch (CameraException)
            {
                // absent on this firmware
            }
        }

        if (modules.Count > 0) snapshot.Modules = modules;

        return snapshot;
    }

    /// <summary>
    /// Replays this file onto a camera. Returns a line per step so the caller can
    /// show exactly what was applied.
    ///
    /// Each section is merged over what the camera currently reports rather than
    /// written wholesale: a field the file carries overwrites, a field it omits keeps
    /// the camera's existing value. The firmware replaces the entire object on a
    /// write, so sending a partial file directly would blank everything it did not
    /// mention - and a preset is deliberately partial, since read-only fields are
    /// stripped out of it.
    /// </summary>
    public async Task<List<string>> ApplyAsync(
        CameraClient client,
        CameraProfile profile,
        bool includeNetwork,
        CancellationToken ct = default)
    {
        var log = new List<string>();
        var channel = profile.Image.Channel;
        var video = profile.Video;

        if (Image is not null)
            log.Add(await StepAsync("image settings", async () =>
            {
                var current = await client.GetModuleAsync(profile.Image.Module, ct);
                var merged = Merge(current, Image);

                if (Same(current, merged)) return "image settings already match - not written";

                await client.SetImageAsync(profile.Image.Module, channel, merged, ct);
                return $"image settings applied ({Image.Count} of {merged.Count} fields from the preset)";
            }, ct));

        // Everything the app can configure beyond image/encoder/network. Applied
        // before the encoder so a failure here happens while the camera is still in a
        // known-good state.
        if (Modules is not null)
        {
            var parts = PartsOf(profile).ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var (key, node) in Modules)
            {
                if (node is not JsonObject wanted || wanted.Count == 0) continue;

                if (!parts.TryGetValue(key, out var part))
                {
                    log.Add($"{key} skipped - not configured for this model");
                    continue;
                }

                try
                {
                    var current = part.Channel is { } partChannel
                        ? await client.GetChannelAsync(part.Module, part.GetCommand, partChannel, ct)
                        : await client.GetModuleAsync(part.Module, part.GetCommand, ct);

                    var merged = Merge(current, wanted);

                    if (Same(current, merged))
                    {
                        log.Add($"{key} already matches - not written");
                        continue;
                    }

                    if (part.Channel is { } writeChannel)
                        await client.SetChannelAsync(part.Module, part.SetCommand, writeChannel, merged, ct);
                    else
                        await client.SetModuleAsync(part.Module, merged, part.SetCommand, ct);

                    log.Add($"{key} applied ({wanted.Count} field(s))");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // One slow or unsupported module must not abandon the rest of the
                    // file. A client timeout arrives as TaskCanceledException, not
                    // CameraException, so catching only the latter used to abort the
                    // whole apply with no indication of which module was at fault.
                    log.Add($"{key} FAILED - {Explain(ex)}");
                }
            }
        }

        // Encoder writes tear down and rebuild the capture pipeline. They are the one
        // part of an apply that can take the whole device down, so each is skipped when
        // it would change nothing, and given time to come back before the next one.
        if (VideoMain is not null)
            log.Add(await StepAsync("main stream", () => ApplyEncoderAsync(
                client, video.Module, video.MainCommandSuffix, channel, VideoMain, "main", ct), ct));

        if (VideoSub is not null)
            log.Add(await StepAsync("sub stream", () => ApplyEncoderAsync(
                client, video.Module, video.SubCommandSuffix, channel, VideoSub, "sub", ct), ct));

        if (includeNetwork && Network is not null)
            log.Add(await StepAsync("network", async () =>
            {
                var current = await client.GetModuleAsync(profile.Network.Module, ct);
                var merged = Merge(current, Network);

                if (Same(current, merged))
                    return $"network already matches - the camera stays on {merged["addr"]}";

                // A move is never acknowledged: measured on hardware, the camera applies
                // the new address and is answering there within a second, but the
                // request it arrived on simply never completes. Waiting out the full
                // client timeout would stall the apply for no information, so this one
                // write gets a short leash of its own.
                using var moveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                moveTimeout.CancelAfter(NetworkMoveGraceMs);

                try
                {
                    await client.SetModuleAsync(profile.Network.Module, merged, moveTimeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Expected. The Network tab has always treated this as success;
                    // provisioning used to report it as a failure.
                    return $"network applied - the camera is moving to {merged["addr"]}";
                }

                return $"network applied - the camera moves to {merged["addr"]}";
            }, ct));

        return log;
    }

    /// <summary>
    /// Runs one section and turns a failure into a log line naming it, instead of
    /// abandoning the rest of the file. A cancellation the caller asked for still
    /// propagates - that is a deliberate stop, not a fault.
    /// </summary>
    private static async Task<string> StepAsync(string name, Func<Task<string>> step, CancellationToken ct)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"{name} FAILED - {Explain(ex)}";
        }
    }

    /// <summary>
    /// A client-side timeout surfaces as TaskCanceledException whose message talks
    /// about HttpClient, which tells the user nothing about their camera.
    /// </summary>
    private static string Explain(Exception ex) =>
        ex is TaskCanceledException or OperationCanceledException
            ? "the camera did not answer in time"
            : ex.Message;

    /// <summary>
    /// How long the encoder needs to restart before it will answer again. Matches the
    /// settle the Video tab already uses after a resolution change.
    /// </summary>
    private const int EncoderSettleMs = 1500;

    /// <summary>
    /// How long to give an addressing write before assuming the camera has taken it
    /// and gone. It never answers one that moves it, so this only decides how long the
    /// apply stalls before following.
    /// </summary>
    private const int NetworkMoveGraceMs = 4000;

    private async Task<string> ApplyEncoderAsync(
        CameraClient client, string module, string suffix, int channel,
        JsonObject wanted, string label, CancellationToken ct)
    {
        var current = await client.GetChannelAsync(module, $"get_{suffix}", channel, ct);
        var merged = Merge(current, wanted);

        if (Same(current, merged))
            return $"{label} stream already matches ({Describe(merged)}) - not written";

        var reshapes = !Same(Shape(current), Shape(merged));

        await client.SetChannelAsync(module, $"set_{suffix}", channel, merged, ct);
        await Task.Delay(EncoderSettleMs, ct);

        return reshapes
            ? $"{label} stream applied ({Describe(current)} -> {Describe(merged)}), pipeline restarted"
            : $"{label} stream applied ({Describe(merged)})";
    }

    /// <summary>The fields whose change forces the capture pipeline to be rebuilt.</summary>
    private static JsonObject Shape(JsonObject video) => new()
    {
        ["width"] = video["width"]?.DeepClone(),
        ["height"] = video["height"]?.DeepClone(),
        ["enc_type"] = video["enc_type"]?.DeepClone()
    };

    private static bool Same(JsonObject a, JsonObject b) => a.ToJsonString() == b.ToJsonString();

    /// <summary>Preset values win; anything the preset omits keeps the camera's value.</summary>
    private static JsonObject Merge(JsonObject current, JsonObject preset)
    {
        var result = (JsonObject)current.DeepClone();

        foreach (var (key, value) in preset)
            result[key] = value?.DeepClone();

        return result;
    }

    /// <summary>
    /// Drops fields the camera will not accept a value for. Keeping them would put
    /// live telemetry - the measured IR level, for one - into a preset, where it
    /// reads like a setting and does nothing when applied.
    /// </summary>
    private static JsonObject Strip(JsonObject source, IReadOnlyCollection<string> readOnlyKeys)
    {
        if (readOnlyKeys.Count == 0) return source;

        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (readOnlyKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            result[key] = value?.DeepClone();
        }

        return result;
    }

    private static string Describe(JsonObject video)
    {
        var width = video["width"]?.ToString() ?? "?";
        var height = video["height"]?.ToString() ?? "?";
        var fps = video["framerate"]?.ToString() ?? "?";
        var codec = video["enc_type"]?.ToString() switch
        {
            "0" => "H.264",
            "1" => "H.265",
            _ => "codec ?"
        };
        return $"{codec} {width}x{height} @ {fps}fps";
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, FileOptions));
    }

    public static CameraSnapshot Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<CameraSnapshot>(File.ReadAllText(path), FileOptions)
                       ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a valid parameter file.");

        if (snapshot.Image is null && snapshot.VideoMain is null &&
            snapshot.VideoSub is null && snapshot.Modules is null)
            throw new InvalidDataException($"{Path.GetFileName(path)} contains no settings to apply.");

        return snapshot;
    }
}

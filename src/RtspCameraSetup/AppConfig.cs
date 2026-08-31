using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RtspCameraSetup;

/// <summary>
/// Object model for cameras.json. The Image Settings UI is generated from
/// <see cref="ImageSpec"/>, so adding a control is a config edit, not a code change.
/// </summary>
public sealed class AppConfig
{

    [JsonPropertyName("preview")] public PreviewSpec Preview { get; set; } = new();

    [JsonPropertyName("discovery")] public DiscoverySpec Discovery { get; set; } = new();
    [JsonPropertyName("presets")] public PresetSpec Presets { get; set; } = new();
    [JsonPropertyName("profiles")] public List<CameraProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration file not found: {path}");

        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty or not valid JSON.");

        if (cfg.Profiles.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} contains no camera profiles.");

        return cfg;
    }

    /// <summary>
    /// Picks the profile whose "match" block is satisfied by the device info the
    /// camera reported. Falls back to the first profile so an unrecognised but
    /// API-compatible camera is still usable.
    /// </summary>
    public CameraProfile MatchProfile(IReadOnlyDictionary<string, string> deviceInfo)
    {
        foreach (var profile in Profiles)
        {
            if (profile.Match.Count == 0) continue;

            var allMatch = profile.Match.All(kv =>
                deviceInfo.TryGetValue(kv.Key, out var actual) &&
                string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase));

            if (allMatch) return profile;
        }

        return Profiles[0];
    }
}

/// <summary>How the preview is decoded and drawn.</summary>
public sealed class PreviewSpec
{

    /// <summary>Executable name or full path. Resolved next to the app, then on PATH.</summary>
    [JsonPropertyName("ffmpegPath")] public string FfmpegPath { get; set; } = "ffmpeg.exe";

    /// <summary>Extra ffmpeg INPUT arguments, inserted before -i.</summary>
    [JsonPropertyName("extraInputArgs")] public List<string> ExtraInputArgs { get; set; } = new();


}

public sealed class DiscoverySpec
{
    [JsonPropertyName("probePort")] public int ProbePort { get; set; } = 80;
    [JsonPropertyName("loginPath")] public string LoginPath { get; set; } = "/view/login.html";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "realm = \"CAMERA\"";
    [JsonPropertyName("connectTimeoutMs")] public int ConnectTimeoutMs { get; set; } = 400;
    [JsonPropertyName("maxParallel")] public int MaxParallel { get; set; } = 128;
    [JsonPropertyName("defaultAddresses")] public List<string> DefaultAddresses { get; set; } = new();

    /// <summary>
    /// The /24 prefixes ("192.168.1") the app watches. This is the whole search
    /// scope - local interface subnets are never added implicitly, so a bulk change
    /// can only ever reach a network named here.
    /// </summary>
    [JsonPropertyName("subnets")] public List<string> Subnets { get; set; } = new();

    /// <summary>Keep re-scanning in the background so the camera list stays live.</summary>
    [JsonPropertyName("continuous")] public bool Continuous { get; set; } = true;

    /// <summary>Seconds to rest between sweeps. Too low just heats the network up.</summary>
    [JsonPropertyName("refreshSeconds")] public int RefreshSeconds { get; set; } = 20;

    /// <summary>Consecutive misses before a camera is dropped from the list.</summary>
    [JsonPropertyName("missesBeforeRemoval")] public int MissesBeforeRemoval { get; set; } = 3;

    /// <summary>
    /// Offer to give an adapter a temporary address on any configured subnet this
    /// machine cannot currently reach, so that subnet can actually be searched. The
    /// address is removed again when the app closes.
    /// </summary>
    [JsonPropertyName("autoConfigureInterface")] public bool AutoConfigureInterface { get; set; } = true;

    /// <summary>Adapter to borrow. Empty picks the one already carrying the most addresses.</summary>
    [JsonPropertyName("interfaceAlias")] public string InterfaceAlias { get; set; } = "";

    /// <summary>Host range searched for a free address to borrow.</summary>
    [JsonPropertyName("temporaryHostFirst")] public int TemporaryHostFirst { get; set; } = 200;
    [JsonPropertyName("temporaryHostLast")] public int TemporaryHostLast { get; set; } = 250;
}

/// <summary>
/// Presets are just parameter files in a folder - capture a camera you are happy
/// with, save it here, and it becomes a preset others can be provisioned from.
/// </summary>
public sealed class PresetSpec
{
    /// <summary>Relative to the executable, or an absolute path.</summary>
    [JsonPropertyName("directory")] public string Directory { get; set; } = "presets";
}

public sealed class CameraProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Unnamed profile";
    [JsonPropertyName("match")] public Dictionary<string, string> Match { get; set; } = new();
    [JsonPropertyName("auth")] public AuthSpec Auth { get; set; } = new();
    [JsonPropertyName("rtsp")] public RtspSpec Rtsp { get; set; } = new();
    [JsonPropertyName("network")] public NetworkSpec Network { get; set; } = new();
    [JsonPropertyName("image")] public ImageSpec Image { get; set; } = new();
    [JsonPropertyName("osd")] public OsdSpec Osd { get; set; } = new();
    [JsonPropertyName("detectorDefaults")] public DetectorDefaults DetectorDefaults { get; set; } = new();
    [JsonPropertyName("detectors")] public List<DetectionSpec> Detectors { get; set; } = new();
    [JsonPropertyName("modules")] public List<SimpleModuleSpec> Modules { get; set; } = new();
    [JsonPropertyName("video")] public VideoSpec Video { get; set; } = new();
    [JsonPropertyName("system")] public SystemSpec System { get; set; } = new();
    [JsonPropertyName("deviceInfoFields")] public List<FieldSpec> DeviceInfoFields { get; set; } = new();
}

public sealed class AuthSpec
{
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "digest-md5";

    /// <summary>Page carrying the realm/nonce literals, since no 401 challenge is sent.</summary>
    [JsonPropertyName("challengePath")] public string ChallengePath { get; set; } = "view/login.html";

    [JsonPropertyName("digestUri")] public string DigestUri { get; set; } = "/cgi-bin/web.cgi?mod=account&cmd=check";
    [JsonPropertyName("loginQuery")] public string LoginQuery { get; set; } = "mod=session&cmd=login1";
    [JsonPropertyName("defaultUsername")] public string DefaultUsername { get; set; } = "admin";
    [JsonPropertyName("defaultPassword")] public string DefaultPassword { get; set; } = "";
}

public sealed class RtspSpec
{
    [JsonPropertyName("port")] public int Port { get; set; } = 554;
    [JsonPropertyName("mainPath")] public string MainPath { get; set; } = "/0/av0";
    [JsonPropertyName("subPath")] public string SubPath { get; set; } = "/1/av0";

    /// <summary>
    /// "hmacsha1-of-md5hex" reproduces the web UI's derivation:
    /// HMAC-SHA1(key = username, message = lowercase hex MD5 of the password).
    /// "plain" sends the account password unchanged.
    /// </summary>
    [JsonPropertyName("passwordDerivation")] public string PasswordDerivation { get; set; } = "plain";

    /// <summary>"tcp" is loss-free but adds a little latency; "udp" is leaner on a clean LAN.</summary>
    [JsonPropertyName("transport")] public string Transport { get; set; } = "tcp";


    /// <summary>Floor below which this camera family stops presenting new frames.</summary>
    public const int MinimumCachingMs = 300;


}

public sealed class NetworkSpec
{
    [JsonPropertyName("module")] public string Module { get; set; } = "net";
    [JsonPropertyName("ipKey")] public string IpKey { get; set; } = "addr";
    [JsonPropertyName("maskKey")] public string MaskKey { get; set; } = "netmask";
    [JsonPropertyName("gatewayKey")] public string GatewayKey { get; set; } = "gateway";
    [JsonPropertyName("dnsKey")] public string DnsKey { get; set; } = "dns";
    [JsonPropertyName("dhcpKey")] public string DhcpKey { get; set; } = "dhcp_mode";

    /// <summary>
    /// The firmware's "IP Address adaptive" self-recovery. When it is on and the
    /// configured gateway stops answering, the camera falls back to DHCP after the
    /// chosen interval and leaves the subnet on its own.
    /// </summary>
    [JsonPropertyName("adaptiveKey")] public string AdaptiveKey { get; set; } = "qwt_ip_adaptive_mode";

    /// <summary>The firmware's "All Net Connect" checkbox, on the same network page.</summary>
    [JsonPropertyName("allNetConnectKey")] public string AllNetConnectKey { get; set; } = "qwt_enable";

    /// <summary>
    /// DHCP and IP-adaptive are both timed enums, not booleans. Treating them as
    /// on/off silently rewrote "Enable 6 hour" as "Always Enable" on every save.
    /// </summary>
    [JsonPropertyName("dhcpModes")] public List<OptionSpec> DhcpModes { get; set; } = new();

    /// <summary>The same list plus one adaptive-only entry; see cameras.json.</summary>
    [JsonPropertyName("adaptiveModes")] public List<OptionSpec> AdaptiveModes { get; set; } = new();

    /// <summary>Keys read from the camera and echoed back untouched on save.</summary>
    [JsonPropertyName("passthroughKeys")] public List<string> PassthroughKeys { get; set; } = new();

    /// <summary>Network fields the firmware reports but discards, plus pure identifiers like the MAC.</summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();
}

/// <summary>
/// One editable field on a plain settings module. "key" may be a dotted path -
/// "ntp.server" reaches into a nested object - because several modules nest.
/// </summary>
public sealed class ModuleFieldSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>toggle | text | password | number | choice</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "text";

    [JsonPropertyName("options")] public List<OptionSpec> Options { get; set; } = new();
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("maxLength")] public int? MaxLength { get; set; }

    /// <summary>Shown after the control, e.g. "seconds".</summary>
    [JsonPropertyName("suffix")] public string Suffix { get; set; } = "";

    /// <summary>Small print under the control.</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>
/// A settings module that is a flat (or shallowly nested) object, rendered from
/// cameras.json rather than hand-built XAML. Covers time, storage, email, FTP, RTSP,
/// ONVIF and the rest of the smaller pages.
/// </summary>
public sealed class SimpleModuleSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";

    [JsonPropertyName("getCommand")] public string GetCommand { get; set; } = "get";
    [JsonPropertyName("setCommand")] public string SetCommand { get; set; } = "set";

    /// <summary>
    /// Set when the module is per-channel. Those endpoints are asymmetric: the read
    /// carries the channel in param2, the write carries it in param with the payload
    /// in param2. snapshot_res tolerates a channel-less read but rejects the write.
    /// </summary>
    [JsonPropertyName("channel")] public int? Channel { get; set; }

    /// <summary>Hides the whole module when the device does not have it.</summary>
    [JsonPropertyName("capabilityBit")] public int? CapabilityBit { get; set; }

    /// <summary>Adds the shared 7-day schedule editor under the fields.</summary>
    [JsonPropertyName("hasSchedule")] public bool HasSchedule { get; set; }

    /// <summary>Adds the rectangle editor, for modules carrying rect / rect_num.</summary>
    [JsonPropertyName("hasRegion")] public bool HasRegion { get; set; }

    [JsonPropertyName("regionPrompt")] public string RegionPrompt { get; set; } = "";

    /// <summary>
    /// Fields stripped when this module is captured into a parameter file: live
    /// telemetry, and per-device identifiers that would collide if replayed onto a
    /// second camera. They are still read and written back untouched on the camera
    /// they came from.
    /// </summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();

    /// <summary>Shown at the top of the section.</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    [JsonPropertyName("fields")] public List<ModuleFieldSpec> Fields { get; set; } = new();
}

public sealed class RangeSpec
{
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
}

/// <summary>One bit of the detector's "output" linkage mask.</summary>
public class LinkageSpec
{
    [JsonPropertyName("bit")] public int Bit { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>
    /// When set, the control only appears if this bit is set in the device's
    /// system_function word. Absent means always available.
    /// </summary>
    [JsonPropertyName("capabilityBit")] public int? CapabilityBit { get; set; }
}

/// <summary>
/// A linkage driven by a work-mode enum rather than a checkbox. The firmware sets the
/// matching output bit whenever the mode is anything but Close.
/// </summary>
public sealed class WorkModeOutputSpec : LinkageSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

/// <summary>
/// Settings every detector shares, kept once rather than repeated per detector.
/// </summary>
public sealed class DetectorDefaults
{
    [JsonPropertyName("sensitivityRange")] public RangeSpec SensitivityRange { get; set; } = new() { Min = 1, Max = 100 };
    [JsonPropertyName("durationRange")] public RangeSpec DurationRange { get; set; } = new() { Min = 1, Max = 999999 };

    /// <summary>Wire order - index 0 is Sunday. See the note in cameras.json.</summary>
    [JsonPropertyName("days")] public List<string> Days { get; set; } = new();

    [JsonPropertyName("schedulePeriods")] public int SchedulePeriods { get; set; } = 3;

    [JsonPropertyName("workModes")] public List<OptionSpec> WorkModes { get; set; } = new();
    [JsonPropertyName("workModeOutputs")] public List<WorkModeOutputSpec> WorkModeOutputs { get; set; } = new();
}

/// <summary>
/// One event source - human detection, motion detection or the alarm input. They
/// share an object shape and differ only in the extra fields they carry and which
/// linkage bits their firmware page offers.
/// </summary>
public sealed class DetectionSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";

    /// <summary>Hides the whole detector when the device does not have it.</summary>
    [JsonPropertyName("capabilityBit")] public int? CapabilityBit { get; set; }

    [JsonPropertyName("hasSensitivity")] public bool HasSensitivity { get; set; }
    [JsonPropertyName("hasRegion")] public bool HasRegion { get; set; }

    /// <summary>
    /// The plain scalar settings this detector carries beyond the shared ones -
    /// show_human, show_label, the alarm input's name and type, and so on. Rendered by
    /// the same code as the Services modules, so a new detector needs no new UI.
    /// </summary>
    [JsonPropertyName("fields")] public List<ModuleFieldSpec> Fields { get; set; } = new();

    [JsonPropertyName("linkages")] public List<LinkageSpec> Linkages { get; set; } = new();

    /// <summary>
    /// Overrides the shared work-mode outputs. Empty means "use the shared set"; a
    /// detector that genuinely has none - the perimeter detector - says so with an
    /// explicit empty list in cameras.json via <see cref="SuppressWorkModes"/>.
    /// </summary>
    [JsonPropertyName("workModeOutputs")] public List<WorkModeOutputSpec> WorkModeOutputs { get; set; } = new();

    /// <summary>True when this detector has no work-mode outputs at all.</summary>
    [JsonPropertyName("suppressWorkModes")] public bool SuppressWorkModes { get; set; }

    /// <summary>Fields stripped when captured into a parameter file.</summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();

    /// <summary>The value meaning "off" in every work-mode enum.</summary>
    public const int WorkModeClose = 4;
}

/// <summary>
/// On-screen display: a date/time block plus a fixed number of text overlays.
///
/// Unlike the image module this is not a flat object, so it gets purpose-built
/// controls rather than the generated ones - but the labels and option values still
/// come from here, taken from the camera's own web UI.
/// </summary>
public sealed class OsdSpec
{
    [JsonPropertyName("module")] public string Module { get; set; } = "osd";
    [JsonPropertyName("channel")] public int Channel { get; set; }

    /// <summary>Width limit the firmware enforces, counting non-ASCII as two.</summary>
    [JsonPropertyName("maxTextWidth")] public int MaxTextWidth { get; set; } = 40;

    [JsonPropertyName("textLines")] public int TextLines { get; set; } = 5;

    /// <summary>Fields stripped when captured into a parameter file.</summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();

    [JsonPropertyName("positions")] public List<OptionSpec> Positions { get; set; } = new();
    [JsonPropertyName("timeFormats")] public List<OptionSpec> TimeFormats { get; set; } = new();
    [JsonPropertyName("dateFormats")] public List<OptionSpec> DateFormats { get; set; } = new();
}

public sealed class ImageSpec
{
    [JsonPropertyName("module")] public string Module { get; set; } = "image";
    [JsonPropertyName("channel")] public int Channel { get; set; }
    [JsonPropertyName("groups")] public List<SettingGroup> Groups { get; set; } = new();

    /// <summary>
    /// Fields the firmware reports but will not accept a value for - either
    /// read-only telemetry or simply discarded. They are stripped from captured
    /// parameter files, so a preset only ever carries settings that mean something
    /// when replayed.
    /// </summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();
}

/// <summary>
/// Encoder configuration. Ranges and the codec/resolution lists are not declared
/// here - they are read from the camera's own capability report at connect time, so
/// the UI only ever offers combinations the hardware accepts.
/// </summary>
public sealed class VideoSpec
{
    [JsonPropertyName("module")] public string Module { get; set; } = "video";

    /// <summary>Module reporting supported codecs, resolutions and value ranges.</summary>
    [JsonPropertyName("abilityModule")] public string AbilityModule { get; set; } = "stream_ability";

    [JsonPropertyName("mainCommandSuffix")] public string MainCommandSuffix { get; set; } = "main";
    [JsonPropertyName("subCommandSuffix")] public string SubCommandSuffix { get; set; } = "sub";

    /// <summary>Codecs, gated by the bits the camera sets in "venc_set".</summary>
    [JsonPropertyName("codecs")] public List<CodecSpec> Codecs { get; set; } = new();

    [JsonPropertyName("rateControlModes")] public List<OptionSpec> RateControlModes { get; set; } = new();
    [JsonPropertyName("qualityLevels")] public List<OptionSpec> QualityLevels { get; set; } = new();

    /// <summary>Encoder fields the firmware reports but discards. Stripped from parameter files.</summary>
    [JsonPropertyName("readOnlyKeys")] public List<string> ReadOnlyKeys { get; set; } = new();
}

/// <summary>
/// Device-level commands. Reboot and factory reset differ by a single word on the
/// same module, so both are named here rather than built from a string at the call
/// site - a typo would be catastrophic and silent.
/// </summary>
public sealed class SystemSpec
{
    [JsonPropertyName("module")] public string Module { get; set; } = "system";
    [JsonPropertyName("rebootCommand")] public string RebootCommand { get; set; } = "reboot";
    [JsonPropertyName("factoryResetCommand")] public string FactoryResetCommand { get; set; } = "reset";

    /// <summary>Roughly how long the camera takes to come back, for the status message.</summary>
    [JsonPropertyName("rebootSeconds")] public int RebootSeconds { get; set; } = 25;
}

public sealed class CodecSpec
{
    /// <summary>Bit tested against the camera's "venc_set" capability mask.</summary>
    [JsonPropertyName("bit")] public int Bit { get; set; }

    /// <summary>Value written to "enc_type".</summary>
    [JsonPropertyName("value")] public int Value { get; set; }

    [JsonPropertyName("label")] public string Label { get; set; } = "";
    public override string ToString() => Label;
}

public sealed class SettingGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("settings")] public List<SettingSpec> Settings { get; set; } = new();
}

public sealed class SettingSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>"slider", "toggle" or "choice".</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "slider";

    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; } = 255;

    /// <summary>Single-parameter opcode for live updates while dragging; null disables that.</summary>
    [JsonPropertyName("fastCmd")] public int? FastCmd { get; set; }

    [JsonPropertyName("options")] public List<OptionSpec> Options { get; set; } = new();

    /// <summary>
    /// Another field whose value chooses which option list applies - exposure times,
    /// for instance, are a different set under PAL and NTSC. When set, the list comes
    /// from <see cref="OptionSets"/> keyed by that field's current value, and the
    /// control is rebuilt whenever the referenced field changes.
    /// </summary>
    [JsonPropertyName("optionsFrom")] public string? OptionsFrom { get; set; }

    [JsonPropertyName("optionSets")] public Dictionary<string, List<OptionSpec>> OptionSets { get; set; } = new();
}

public sealed class OptionSpec
{
    [JsonPropertyName("value")] public int Value { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>
    /// Chipsets this option exists on. Empty means all. The firmware gates its
    /// fastest shutter step this way, so the app does the same rather than offering
    /// a value the sensor will refuse.
    /// </summary>
    [JsonPropertyName("requiresCpu")] public List<string> RequiresCpu { get; set; } = new();

    public bool AvailableOn(string? cpuType) =>
        RequiresCpu.Count == 0 ||
        (cpuType is not null && RequiresCpu.Contains(cpuType, StringComparer.OrdinalIgnoreCase));

    public override string ToString() => Label;
}

public sealed class FieldSpec
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

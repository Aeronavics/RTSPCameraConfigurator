using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RTSPCameraConfigurator;

public sealed class CameraException : Exception
{
    public CameraException(string message) : base(message) { }
}

/// <summary>
/// Talks to the camera's /cgi-bin/web.cgi JSON API.
///
/// Protocol notes (established by inspecting the shipped web UI):
///  - The digest challenge is not delivered via a 401. The realm and nonce are
///    embedded as literals in view/login.html and must be scraped from there.
///  - The digest response is always computed against a FIXED uri
///    ("...mod=account&cmd=check") no matter which endpoint is actually called.
///    This looks wrong but matches the firmware; using the real URI fails.
///  - Login returns a Session-Id response header, presented on every later call.
///  - Reads are GET with a query string; writes are POST with a JSON body of
///    {"mod":..,"cmd":"set","param":{..}} and optionally "param2".
/// </summary>
public sealed class CameraClient : IDisposable
{
    private static readonly Regex NonceRx = new("nonce\\s*=\\s*\"([0-9a-fA-F]+)\"", RegexOptions.Compiled);
    private static readonly Regex RealmRx = new("realm\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private const string CgiPath = "cgi-bin/web.cgi";

    private readonly HttpClient _http;
    private readonly AuthSpec _auth;
    private string _user = "";
    private string _pass = "";

    public string Host { get; }
    public string? SessionId { get; private set; }

    public CameraClient(string host, AuthSpec auth, TimeSpan? timeout = null)
    {
        Host = host;
        _auth = auth;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Connects with whichever of <paramref name="schemes"/> the camera actually accepts.
    ///
    /// The scheme is a property of the firmware, not of the model: two H8D units differ
    /// because one shipped with digest-and-Session-Id and the other with a per-request
    /// token. Nothing in the device record distinguishes them, and the record cannot be
    /// read until a scheme has already been chosen - so the choice is made by trying.
    /// </summary>
    public static async Task<CameraClient> OpenAsync(
        string host,
        IEnumerable<AuthSpec> schemes,
        string username,
        string password,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        Exception? first = null;

        foreach (var auth in schemes)
        {
            var client = new CameraClient(host, auth, timeout);
            try
            {
                await client.LoginAsync(username, password, ct);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                first ??= ex;

                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            }
        }

        throw first ?? new CameraException($"No configured authentication scheme was accepted by {host}.");
    }

    /// <summary>What actually travels as the password, once the profile's derivation is applied.</summary>
    private string _token = "";

    /// <summary>
    /// True when the firmware keeps no session and expects the credentials on every
    /// request instead of a Session-Id header.
    /// </summary>
    private bool UsesQueryCredentials =>
        string.Equals(_auth.Credentials, "query", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Spells one request the way this camera's firmware expects: the module and verb under
    /// the profile's parameter names, then any extra parameters, then the credentials if the
    /// firmware wants them on every call.
    /// </summary>
    private string Address(string module, string command, params (string Key, string Value)[] extra)
    {
        var parts = new List<string>
        {
            $"{_auth.ModuleParam}={Uri.EscapeDataString(module)}",
            $"{_auth.CommandParam}={Uri.EscapeDataString(command)}"
        };

        foreach (var (key, value) in extra)
            parts.Add($"{key}={Uri.EscapeDataString(value)}");

        if (UsesQueryCredentials)
        {
            parts.Add($"username={Uri.EscapeDataString(_user)}");
            parts.Add($"password={Uri.EscapeDataString(_token)}");
        }

        return string.Join("&", parts);
    }

    // ---------------------------------------------------------------- login

    public async Task LoginAsync(string username, string password, CancellationToken ct = default)
    {
        _user = username;
        _pass = password;
        _token = DeriveRtspPassword(username, password, _auth.PasswordDerivation);

        if (UsesQueryCredentials)
        {
            await CheckQueryCredentialsAsync(ct);
            return;
        }

        string html;
        try
        {
            using var pageReq = new HttpRequestMessage(HttpMethod.Get, _auth.ChallengePath.TrimStart('/'));
            using var pageRes = await _http.SendAsync(pageReq, ct);
            html = await ReadBodyAsync(pageRes, ct);
        }
        catch (Exception ex)
        {
            throw new CameraException($"Could not reach {Host}: {ex.Message}");
        }

        var nonce = NonceRx.Match(html).Groups[1].Value;
        var realm = RealmRx.Match(html).Groups[1].Value;
        if (nonce.Length == 0 || realm.Length == 0)
            throw new CameraException("Login page did not contain a digest challenge; this may not be a supported camera.");

        var header = BuildDigestHeader(username, password, realm, nonce, _auth.DigestUri);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{CgiPath}?{_auth.LoginQuery}");
        req.Headers.TryAddWithoutValidation("Authorization", header);

        using var res = await _http.SendAsync(req, ct);
        var body = await ReadBodyAsync(res, ct);

        var node = TryParseJson(body);
        if (node is null)
            throw new CameraException($"Unexpected login response: {Truncate(body)}");

        if (!string.Equals(StatusOf(node), "ok", StringComparison.OrdinalIgnoreCase))
            throw new CameraException("Login rejected - check the username and password.");

        if (res.Headers.TryGetValues("Session-Id", out var values))
            SessionId = values.FirstOrDefault();

        SessionId ??= (string?)node["data"]?["session_id"];

        if (string.IsNullOrWhiteSpace(SessionId))
            throw new CameraException("Login succeeded but the camera returned no Session-Id.");
    }

    /// <summary>
    /// Confirms a password for firmware that has no login step, by making a call that does
    /// enforce it. The device module deliberately is not used for this: on the H8D it
    /// answers in full whatever password is presented, so it would accept anything.
    /// </summary>
    private async Task CheckQueryCredentialsAsync(CancellationToken ct)
    {
        JsonNode node;
        try
        {
            node = await SendGetAsync(Address(_auth.CheckModule, _auth.CheckCommand), ct);
        }
        catch (CameraException ex)
        {
            throw new CameraException($"Could not reach {Host}: {ex.Message}");
        }

        if (!string.Equals(StatusOf(node), "ok", StringComparison.OrdinalIgnoreCase))
            throw new CameraException("Login rejected - check the username and password.");
    }

    private static string BuildDigestHeader(string user, string pass, string realm, string nonce, string uri)
    {
        var cnonce = RandomToken(16);
        var ha1 = Md5Hex($"{user}:{realm}:{pass}");
        var ha2 = Md5Hex($"GET:{uri}");
        var response = Md5Hex($"{ha1}:{nonce}:00000001:{cnonce}:auth:{ha2}");

        return $"Digest username=\"{user}\",realm=\"{realm}\",nonce=\"{nonce}\"," +
               $"uri=\"{uri}\",cnonce=\"{cnonce}\",nc=00000001,qop=\"auth\",response=\"{response}\"";
    }

    // ------------------------------------------------------------ transport

    public async Task<JsonNode> GetAsync(string query, CancellationToken ct = default)
    {
        var node = await SendGetAsync(query, ct);

        if (IsExpired(node))
        {
            await LoginAsync(_user, _pass, ct);
            node = await SendGetAsync(query, ct);
        }

        return node;
    }

    private async Task<JsonNode> SendGetAsync(string query, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{CgiPath}?{query}");
        AttachSession(req);

        using var res = await _http.SendAsync(req, ct);
        var body = await ReadBodyAsync(res, ct);

        return TryParseJson(body)
               ?? throw new CameraException($"Camera rejected '{query}': {Truncate(body)}");
    }

    public async Task PostAsync(JsonObject payload, CancellationToken ct = default)
    {
        var node = await SendPostAsync(payload, ct);

        if (IsExpired(node))
        {
            await LoginAsync(_user, _pass, ct);
            node = await SendPostAsync(payload, ct);
        }

        var status = StatusOf(node);
        if (status is not null && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new CameraException($"Camera refused the change: {Truncate(node.ToJsonString())}");
    }

    private async Task<JsonNode> SendPostAsync(JsonObject payload, CancellationToken ct)
    {
        // Firmware that only ever issues GETs takes the very same fields as query
        // parameters, with the JSON ones encoded as strings. Callers keep building one
        // shape; the difference lives here.
        if (string.Equals(_auth.Writes, "get-query", StringComparison.OrdinalIgnoreCase))
            return await SendGetAsync(AddressWrite(payload), ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, CgiPath)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AttachSession(req);

        using var res = await _http.SendAsync(req, ct);
        var body = await ReadBodyAsync(res, ct);

        return TryParseJson(body)
               ?? throw new CameraException($"Camera rejected the write: {Truncate(body)}");
    }

    /// <summary>
    /// Turns a write payload - the {mod, cmd, param, param2} shape every caller builds -
    /// into the query form. Which slot carries the channel and which the values is a
    /// per-module detail that differs between firmware families, so this preserves the
    /// slots exactly as the caller filled them rather than trying to be clever.
    /// </summary>
    private string AddressWrite(JsonObject payload)
    {
        var module = (string?)payload["mod"] ?? "";
        var command = (string?)payload["cmd"] ?? "set";

        var extra = new List<(string, string)>();

        foreach (var slot in new[] { "param", "param2" })
        {
            if (payload[slot] is not { } value) continue;

            // A JSON object or array travels as its text; a bare scalar as itself.
            extra.Add((slot, value is JsonObject or JsonArray
                ? value.ToJsonString()
                : value.ToString()));
        }

        return Address(module, command, extra.ToArray());
    }

    private void AttachSession(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(SessionId))
            req.Headers.TryAddWithoutValidation("Session-Id", SessionId);
    }

    /// <summary>
    /// Reads "status" without assuming it is a JSON string. Casting a JsonNode to
    /// string throws when the value is a number, and at least one module - p2p -
    /// answers with a numeric status, which took the whole read down.
    /// </summary>
    private static string? StatusOf(JsonNode node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("status", out var status)
            ? status?.ToString()
            : null;

    private static bool IsExpired(JsonNode node) =>
        string.Equals(StatusOf(node), "expired", StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------- typed helpers

    /// <summary>
    /// Reads a settings object. Most modules answer "get", but a few name their own
    /// command - storage uses get_record_info, account uses list.
    /// </summary>
    public Task<JsonObject> GetModuleAsync(string module, CancellationToken ct = default) =>
        GetModuleAsync(module, "get", ct);

    public async Task<JsonObject> GetModuleAsync(string module, string command, CancellationToken ct = default)
    {
        var node = await GetAsync(Address(module, command), ct);
        return node as JsonObject
               ?? throw new CameraException($"Module '{module}' returned an unexpected shape.");
    }

    /// <summary>
    /// Reads a module that answers with a list rather than an object - the account
    /// module returns a bare JSON array of users.
    /// </summary>
    public async Task<JsonArray> GetArrayAsync(
        string module, string command, CancellationToken ct = default)
    {
        var node = await GetAsync(Address(module, command), ct);
        return node as JsonArray
               ?? throw new CameraException($"'{module}/{command}' did not return a list.");
    }

    /// <summary>Writes a whole settings object back: {"mod":m,"cmd":"set","param":body}.</summary>
    public Task SetModuleAsync(string module, JsonObject body, CancellationToken ct = default) =>
        SetModuleAsync(module, body, "set", ct);

    public Task SetModuleAsync(string module, JsonObject body, string command, CancellationToken ct = default) =>
        PostAsync(new JsonObject
        {
            ["mod"] = module,
            ["cmd"] = command,
            ["param"] = body.DeepClone()
        }, ct);

    /// <summary>
    /// Reads a per-channel module. These endpoints name the stream in the command
    /// ("get_main"/"get_sub") and take the channel as a JSON-encoded query
    /// parameter rather than a plain one.
    /// </summary>
    public async Task<JsonObject> GetChannelAsync(string module, string command, int channel, CancellationToken ct = default)
    {
        var node = await GetAsync(
            Address(module, command, ("param2", $"{{\"channel\":{channel}}}")), ct);

        return node as JsonObject
               ?? throw new CameraException($"'{module}/{command}' returned an unexpected shape.");
    }

    /// <summary>Writes a per-channel module: channel in "param", payload in "param2".</summary>
    public Task SetChannelAsync(string module, string command, int channel, JsonObject body, CancellationToken ct = default) =>
        PostAsync(new JsonObject
        {
            ["mod"] = module,
            ["cmd"] = command,
            ["param"] = new JsonObject { ["channel"] = channel },
            ["param2"] = body.DeepClone()
        }, ct);

    /// <summary>Image writes carry the channel in "param" and the payload in "param2".</summary>
    public Task SetImageAsync(string module, int channel, JsonObject image, CancellationToken ct = default) =>
        PostAsync(new JsonObject
        {
            ["mod"] = module,
            ["cmd"] = "set",
            ["param"] = new JsonObject { ["channel"] = channel },
            ["param2"] = image.DeepClone()
        }, ct);

    /// <summary>
    /// Live single-parameter tweak used while dragging a slider.
    ///
    /// Note this is cmd "set_single", not "set", and it needs BOTH param (channel)
    /// and param2 (the opcode/value pair). Sending it as a one-argument "set" is
    /// rejected with "param num error".
    /// </summary>
    public Task SetImageParamAsync(string module, int channel, int opcode, int value, CancellationToken ct = default) =>
        PostAsync(new JsonObject
        {
            ["mod"] = module,
            ["cmd"] = "set_single",
            ["param"] = new JsonObject { ["channel"] = channel },
            ["param2"] = new JsonObject { ["cmd"] = opcode, ["value"] = value }
        }, ct);

    /// <summary>
    /// Issues a device command such as reboot or factory reset.
    ///
    /// The camera tears its network stack down while answering, so a timeout or a
    /// dropped connection is the normal outcome, not a failure - the firmware's own
    /// web UI ignores the result for exactly this reason. Only a JSON error body is
    /// treated as a refusal.
    /// </summary>
    public async Task SystemCommandAsync(string module, string command, CancellationToken ct = default)
    {
        try
        {
            var node = await SendGetAsync(Address(module, command), ct);

            var status = StatusOf(node);
            if (status is not null && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                throw new CameraException($"Camera refused '{command}': {Truncate(node.ToJsonString())}");
        }
        catch (HttpRequestException) { /* expected: the device is going down */ }
        catch (TaskCanceledException) { /* expected: no reply before it went down */ }
        catch (CameraException) when (SessionId is null)
        {
            // Session gone because the device restarted; nothing to report.
        }
    }

    // ------------------------------------------------------------ RTSP URL

    /// <summary>
    /// The web UI does not use the account password for RTSP. It stores
    /// HMAC-SHA1(key = username, message = md5hex(password)) and uses that as the
    /// stream password. Verified against the camera: the plain password is refused.
    /// </summary>
    public static string DeriveRtspPassword(string username, string password, string derivation)
    {
        if (!string.Equals(derivation, "hmacsha1-of-md5hex", StringComparison.OrdinalIgnoreCase))
            return password;

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(username));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(Md5Hex(password)));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string BuildRtspUrl(string host, string username, string password, RtspSpec spec, bool subStream)
    {
        var pass = DeriveRtspPassword(username, password, spec.PasswordDerivation);
        var path = subStream ? spec.SubPath : spec.MainPath;
        if (!path.StartsWith('/')) path = "/" + path;

        return $"rtsp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(pass)}@{host}:{spec.Port}{path}";
    }

    // ---------------------------------------------------------------- utils

    /// <summary>
    /// Reads a response body as UTF-8 without consulting the Content-Type charset.
    ///
    /// This firmware replies with charset='utf-8' - quoted - which .NET rejects
    /// ("'utf-8' is not a supported encoding name"), so ReadAsStringAsync throws on
    /// every API call. Decoding the bytes directly sidesteps the malformed header.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RandomToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    private static JsonNode? TryParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string Truncate(string s) =>
        s.Length <= 200 ? s.Trim() : s[..200].Trim() + "...";

    public void Dispose() => _http.Dispose();
}

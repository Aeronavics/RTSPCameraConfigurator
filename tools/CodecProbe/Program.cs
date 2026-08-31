using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

namespace RTSPCameraConfigurator.Tools;

/// <summary>
/// Reports what an RTSP path ACTUALLY delivers, by decoding it.
///
/// Why this exists: the Vatilon/Hi3516 firmware answers every RTSP path with a
/// byte-identical canned SDP that always claims H265 and carries no resolution, and
/// it silently serves the SUB stream for any path it does not recognise. A wrong
/// path therefore connects, plays, and looks entirely healthy while being the wrong
/// stream. DESCRIBE cannot tell you apart from that; the decoder can.
///
/// Use this whenever you add a camera model, to establish its real main/sub paths
/// before writing them into cameras.json. Resolution is the reliable discriminator,
/// since main and sub almost always differ.
/// </summary>
internal static class Program
{
    private static readonly string[] DefaultPaths =
    {
        "/stream0", "/stream1", "/stream2",
        "/0/av0", "/1/av0",
        "/av0_0", "/av0_1",
        "/main", "/sub",
        "/ch0_0.h264", "/ch0_1.h264",
        "/live/0", "/live/1",
        "/live/ch00_0", "/live/ch00_1",
        "/video0", "/video1",
        "/cam/realmonitor?channel=1&subtype=0",
        "/cam/realmonitor?channel=1&subtype=1",
        "/h264/ch1/main/av_stream",
        "/h264/ch1/sub/av_stream",
        "/11", "/12",
        "/profile1", "/profile2",
        "/__unrecognised_control__"
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                Usage: CodecProbe <host> [user] [password] [path ...]

                  host      camera address, e.g. 192.168.144.53
                  user      default: admin
                  password  ACCOUNT password (the RTSP password is derived from it)
                  path      one or more RTSP paths; omit to sweep a standard list

                Prints the real codec and resolution the decoder receives for each
                path. Paths that fall back to the sub stream will all report the same
                resolution - that is the tell. The list includes a deliberately
                invalid control path so you can see what "fallback" looks like.
                """);
            return 1;
        }

        var host = args[0];
        var user = args.Length > 1 ? args[1] : "admin";
        var pass = args.Length > 2 ? args[2] : "123456";
        var paths = args.Length > 3 ? args[3..] : DefaultPaths;

        var rtspPassword = DeriveRtspPassword(user, pass);

        Core.Initialize();
        using var libvlc = new LibVLC("--no-audio", "--quiet");

        Console.WriteLine($"Probing {host} as {user}");
        Console.WriteLine();
        Console.WriteLine($"{"path",-42} {"codec",-8} {"resolution",-12} state");
        Console.WriteLine(new string('-', 82));

        foreach (var path in paths)
            Console.WriteLine(await ProbeAsync(libvlc, host, user, rtspPassword, path));

        Console.WriteLine();
        Console.WriteLine("Paths sharing the control path's resolution are falling back - not real paths.");
        return 0;
    }

    private static async Task<string> ProbeAsync(
        LibVLC libvlc, string host, string user, string password, string path)
    {
        string codec = "-", resolution = "-", state;

        try
        {
            var url = $"rtsp://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{host}:554{path}";

            using var media = new Media(libvlc, new Uri(url), ":rtsp-tcp", ":network-caching=200");
            using var player = new MediaPlayer(media);
            player.Play();

            var deadline = DateTime.UtcNow.AddSeconds(7);
            while (DateTime.UtcNow < deadline && media.Tracks.Length == 0)
                await Task.Delay(200);

            // Track metadata can arrive before the decoder has real dimensions.
            await Task.Delay(1800);

            var video = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            if (video.Codec != 0)
            {
                codec = FourCc(video.Codec);
                resolution = $"{video.Data.Video.Width}x{video.Data.Video.Height}";
            }

            state = player.State.ToString();
            player.Stop();
        }
        catch (Exception ex)
        {
            state = "ERR " + ex.Message;
        }

        return $"{path,-42} {codec,-8} {resolution,-12} {state}";
    }

    /// <summary>Mirrors the web UI: HMAC-SHA1(key = username, msg = md5hex(password)).</summary>
    private static string DeriveRtspPassword(string username, string password)
    {
        var md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(username));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(md5Hex))).ToLowerInvariant();
    }

    private static string FourCc(uint codec)
    {
        var text = BitConverter.GetBytes(codec)
            .Select(b => b >= 32 && b < 127 ? (char)b : '?')
            .ToArray();
        return new string(text).Trim();
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Image = System.Windows.Controls.Image;

namespace RTSPCameraConfigurator;

/// <summary>
/// Plays an RTSP stream by piping raw frames out of an ffmpeg subprocess and drawing
/// them into a WPF bitmap.
///
/// Why not libvlc: libvlc will not present a live picture below roughly 300 ms of
/// buffering on this camera family, which puts a floor under preview latency. ffmpeg
/// with -fflags nobuffer -flags low_delay -probesize 32 has no such floor, and the
/// decode/draw path here is the same one the libvlc renderer already used.
///
/// The subprocess is deliberately simple to reason about: if it dies, the picture
/// stops and the error is reported; nothing can corrupt the app's own state.
/// </summary>
public sealed class FfmpegVideoSource : IDisposable
{
    private Image _target;
    private readonly PreviewSpec _spec;
    private readonly Dispatcher _dispatcher;

    private readonly object _sync = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    private WriteableBitmap? _bitmap;
    private byte[]? _uiBuffer;
    private int _width;
    private int _height;

    private volatile bool _pendingFrame;
    private bool _disposed;

    /// <summary>True while the decoder process is up and frames are being pumped.</summary>
    public bool IsRunning => _process is { HasExited: false };

    public long FramesRendered { get; private set; }
    public uint LastFrameHash { get; private set; }
    public string? LastError { get; private set; }

    public FfmpegVideoSource(Image target, PreviewSpec spec)
    {
        _target = target;
        _spec = spec;
        _dispatcher = target.Dispatcher;
    }

    /// <summary>
    /// Draws into a different Image from now on. Used by the fullscreen view, which
    /// wants the same decoder pointed somewhere else rather than a second one.
    /// </summary>
    public void Retarget(Image target)
    {
        _dispatcher.Invoke(() => _target.Source = null);
        _target = target;
    }

    /// <summary>Resolves the executable, so a missing ffmpeg is reported rather than thrown at play time.</summary>
    public static string? Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) configured = "ffmpeg.exe";

        if (Path.IsPathRooted(configured))
            return File.Exists(configured) ? configured : null;

        var beside = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(beside)) return beside;

        // Fall back to PATH.
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var directory in paths)
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), configured);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        return null;
    }

    public void Start(string url, int width, int height, bool preferTcp)
    {
        Stop();

        var executable = Resolve(_spec.FfmpegPath);
        if (executable is null)
        {
            LastError = $"ffmpeg not found ('{_spec.FfmpegPath}')";
            return;
        }

        lock (_sync)
        {
            _width = width;
            _height = height;
            _uiBuffer = new byte[width * height * 4];
        }

        _dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _target.Source = _bitmap;
        });

        var arguments = BuildArguments(url, width, height, preferTcp);

        var psi = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            LastError = $"could not start ffmpeg: {ex.Message}";
            return;
        }

        if (_process is null)
        {
            LastError = "could not start ffmpeg";
            return;
        }

        LastError = null;

        // ffmpeg writes progress and warnings to stderr; keep only the last line so a
        // failure has a usable explanation without buffering megabytes.
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data.Trim();
        };
        _process.BeginErrorReadLine();

        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_process, _cts.Token));
    }

    private string BuildArguments(string url, int width, int height, bool preferTcp)
    {
        var builder = new StringBuilder("-hide_banner -loglevel error ");

        // Order matters: these are INPUT options and must precede -i.
        builder.Append("-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 ");
        builder.Append(preferTcp ? "-rtsp_transport tcp " : "-rtsp_transport udp ");

        foreach (var extra in _spec.ExtraInputArgs)
            builder.Append(extra).Append(' ');

        builder.Append($"-i \"{url}\" ");

        // -fps_mode passthrough is NOT optional here.
        //
        // The rawvideo muxer defaults to constant frame rate, and the aggressive probe
        // settings above deliberately stop ffmpeg from ever learning the real input
        // rate - so it invents one and DUPLICATES frames to fill it. Measured against
        // this camera's 20 fps main stream: 42 fps written without this flag, and the
        // faster the reader drains the pipe the worse it gets (131 fps observed in the
        // running app). The preview then falls permanently behind decoding frames that
        // do not exist, which looks exactly like a stopped stream.
        //
        // Passthrough forwards the source timestamps untouched: one frame out per
        // frame in.
        builder.Append("-fps_mode passthrough ");
        builder.Append($"-an -f rawvideo -pix_fmt bgra -s {width}x{height} -");

        return builder.ToString();
    }

    private async Task PumpAsync(Process process, CancellationToken ct)
    {
        var frameBytes = _width * _height * 4;
        var buffer = new byte[frameBytes];
        var stream = process.StandardOutput.BaseStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < frameBytes)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, frameBytes - read), ct);
                    if (n <= 0) return; // ffmpeg exited
                    read += n;
                }

                Present(buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>
    /// Frames arrive faster than WPF will render them, so a frame already queued for
    /// the UI thread is left to cover this one rather than queueing more work.
    /// </summary>
    private void Present(byte[] frame)
    {
        if (_disposed || _pendingFrame) return;

        lock (_sync)
        {
            if (_uiBuffer is null || _uiBuffer.Length != frame.Length) return;
            Buffer.BlockCopy(frame, 0, _uiBuffer, 0, frame.Length);
        }

        _pendingFrame = true;

        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _pendingFrame = false;
            if (_disposed) return;

            try
            {
                lock (_sync)
                {
                    if (_bitmap is null || _uiBuffer is null) return;

                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, _width, _height),
                        _uiBuffer,
                        _width * 4,
                        0);

                    LastFrameHash = Hash(_uiBuffer);
                }

                FramesRendered++;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }));
    }

    private static uint Hash(byte[] frame)
    {
        var hash = 2166136261u;
        for (var offset = 0; offset < frame.Length; offset += 997)
        {
            hash ^= frame[offset];
            hash *= 16777619u;
        }
        return hash;
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
        _pump = null;
    }

    public void Clear()
    {
        _dispatcher.Invoke(() => _target.Source = null);
        _bitmap = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

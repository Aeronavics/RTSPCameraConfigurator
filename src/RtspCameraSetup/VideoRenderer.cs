using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

// WinForms interop is referenced for libvlc's packaging, so Image and MediaPlayer are
// ambiguous without these aliases.
using Image = System.Windows.Controls.Image;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace RtspCameraSetup;

/// <summary>
/// Renders video into a WPF <see cref="WriteableBitmap"/> using libvlc's raw video
/// callbacks, instead of letting libvlc draw into an embedded child window.
///
/// Why: LibVLCSharp.WPF's VideoView hands libvlc an HWND hosted inside WPF. On this
/// machine that produced a single frame and then a static picture - libvlc reported
/// pictures as "displayed" and kept decoding, but the window was never repainted.
/// Pulling the frames into a bitmap ourselves removes the embedded window entirely,
/// so the picture is ordinary WPF content: it scales, layers and repaints normally.
///
/// The cost is a per-frame copy into the bitmap's back buffer, which is negligible
/// for a preview at these resolutions.
/// </summary>
public sealed class VideoRenderer : IDisposable
{
    private readonly Image _target;
    private readonly Dispatcher _dispatcher;

    // libvlc keeps these for the lifetime of playback; holding the delegates in
    // fields stops the GC collecting them out from under native code.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private readonly object _sync = new();

    private IntPtr _buffer;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private uint _lines;

    private WriteableBitmap? _bitmap;
    private volatile bool _pendingFrame;
    private bool _disposed;

    /// <summary>Frames actually written into the bitmap. Distinguishes "libvlc says it displayed" from "we drew it".</summary>
    public long FramesRendered { get; private set; }

    /// <summary>Set if writing to the bitmap ever failed, so the cause is visible.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Cheap checksum of the most recent frame's pixels. If this keeps changing, the
    /// picture really is moving - which a screen capture cannot reliably tell us.
    /// </summary>
    public uint LastFrameHash { get; private set; }

    public VideoRenderer(Image target)
    {
        _target = target;
        _dispatcher = target.Dispatcher;

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
    }

    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
    }

    /// <summary>Lock calls, i.e. how often libvlc asked for a buffer to decode into.</summary>
    public long LockCount { get; private set; }

    /// <summary>Negotiates the pixel format. RV32 maps directly onto WPF's Bgr32.</summary>
    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        WriteFourCc(chroma, "RV32");

        pitches = width * 4;
        lines = height;

        lock (_sync)
        {
            _width = width;
            _height = height;
            _pitch = pitches;
            _lines = lines;

            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)(pitches * lines));
        }

        var w = (int)width;
        var h = (int)height;

        _dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            _target.Source = _bitmap;
        });

        return 1; // one plane
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_sync)
        {
            if (_buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        LockCount++;
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Nothing to release: we hand libvlc the same buffer every time.
    }

    /// <summary>
    /// Called on a libvlc thread once a frame is ready. The copy has to happen on the
    /// UI thread, and frames arrive faster than WPF will render, so a frame already
    /// queued is left to cover this one rather than piling up work on the dispatcher.
    /// </summary>
    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed || _pendingFrame) return;

        _pendingFrame = true;

        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _pendingFrame = false;
            if (_disposed) return;

            try
            {
                lock (_sync)
                {
                    if (_bitmap is null || _buffer == IntPtr.Zero) return;

                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, (int)_width, (int)_height),
                        _buffer,
                        (int)(_pitch * _lines),
                        (int)_pitch);
                }

                FramesRendered++;
                LastFrameHash = HashSample();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }));
    }

    /// <summary>FNV-1a over a sparse sample of the frame - cheap enough to run per frame.</summary>
    private uint HashSample()
    {
        if (_buffer == IntPtr.Zero) return 0;

        var total = (int)(_pitch * _lines);
        var hash = 2166136261u;

        for (var offset = 0; offset < total; offset += 997)
        {
            hash ^= Marshal.ReadByte(_buffer, offset);
            hash *= 16777619u;
        }

        return hash;
    }

    private static void WriteFourCc(IntPtr destination, string fourCc)
    {
        for (var i = 0; i < 4; i++)
            Marshal.WriteByte(destination, i, (byte)fourCc[i]);
    }

    public void Clear()
    {
        _dispatcher.Invoke(() => _target.Source = null);
        _bitmap = null;
    }

    public void Dispose()
    {
        _disposed = true;

        lock (_sync)
        {
            if (_buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}

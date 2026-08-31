using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RTSPCameraConfigurator;

public enum CameraState
{
    Identifying,
    Online,
    CredentialsRequired,
    Offline
}

/// <summary>
/// A camera as the background discovery service currently understands it. Bound
/// directly to the list, so every field raises change notifications.
/// </summary>
public sealed class LiveCamera : INotifyPropertyChanged
{
    public required string Address { get; init; }

    private string _model = "";
    private string _firmware = "";
    private string _serial = "";
    private string _streamSummary = "";
    private CameraState _state = CameraState.Identifying;

    public string Model { get => _model; set => Set(ref _model, value); }
    public string Firmware { get => _firmware; set => Set(ref _firmware, value); }
    public string Serial { get => _serial; set => Set(ref _serial, value); }
    public string StreamSummary { get => _streamSummary; set => Set(ref _streamSummary, value); }

    public CameraState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Display));
        }
    }

    public string StatusText => State switch
    {
        CameraState.Identifying => "identifying...",
        CameraState.Online => Model.Length > 0 ? Model : "online",
        CameraState.CredentialsRequired => "sign in needed",
        CameraState.Offline => "offline",
        _ => ""
    };

    /// <summary>Single-line label, so the list reads well at a glance.</summary>
    public string Display => $"{Address}  -  {StatusText}";

    /// <summary>Accessibility and automation read this; without it they see the type name.</summary>
    public override string ToString() => Display;

    /// <summary>Device info from the last successful identification, for profile matching.</summary>
    public Dictionary<string, string> Info { get; set; } = new();

    /// <summary>Consecutive sweeps in which this camera did not answer.</summary>
    public int Misses { get; set; }

    /// <summary>True once identified, so repeat sweeps do not re-interrogate it.</summary>
    public bool Identified { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Display));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

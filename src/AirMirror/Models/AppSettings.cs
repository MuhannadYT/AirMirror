namespace AirMirror.Models;

public sealed class AppSettings
{
    public string AirPlayName { get; set; } = "AirMirror";
    public ResolutionMode ResolutionMode { get; set; } = ResolutionMode.Auto;
    public int CustomWidth { get; set; } = 1920;
    public int CustomHeight { get; set; } = 1080;
    public int CustomRefreshRate { get; set; } = 60;
    public StartMode StartMode { get; set; } = StartMode.Fullscreen;
    public bool AutoFullscreenVideo { get; set; } = true;
    public AudioOutputMode AudioOutput { get; set; } = AudioOutputMode.Pc;
    public HdrMode HdrSupport { get; set; } = HdrMode.Auto;
    public string? UxPlayPath { get; set; }
    public bool StartReceiverOnLaunch { get; set; } = true;
    /// <summary>
    /// When true, AirMirror takes over HLS playback in its own VLC-based PlaybackWindow
    /// (UxPlay launches with -hls-external). When false, UxPlay plays the HLS stream
    /// natively inside its d3d12 window with the AirMirror overlay HUD.
    /// </summary>
    public bool UseInAppVideoPlayer { get; set; } = true;

    /// <summary>
    /// When true (default), AirMirror advertises HLS support to AirPlay clients (the
    /// YouTube/streaming “AirPlay Video” button on iOS). When false, the receiver runs
    /// without any -hls flag — only mirror/audio AirPlay is offered.
    /// </summary>
    public bool EnableHlsVideo { get; set; } = false;

    /// <summary>
    /// When true (default), closing the main window hides it to the tray instead of exiting.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// When true (default), AirMirror auto-launches when Windows starts (registered under HKCU Run).
    /// </summary>
    public bool LaunchOnSystemStartup { get; set; } = true;
}

public enum ResolutionMode
{
    Auto,
    Custom
}

public enum StartMode
{
    Fullscreen,
    Windowed
}

public enum AudioOutputMode
{
    Pc,
    Iphone
}

public enum HdrMode
{
    Auto,
    On,
    Off
}

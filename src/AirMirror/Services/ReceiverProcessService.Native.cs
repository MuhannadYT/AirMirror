using AirMirror.Models;

namespace AirMirror.Services;

/// <summary>
/// Native-HLS-mode stubs. AirMirror's default settings use the in-app LibVLC player
/// (<see cref="AppSettings.UseInAppVideoPlayer"/> = true), which means UxPlay is launched
/// with <c>-hls-external</c> and these native-mode events / commands are never used.
/// They exist only to satisfy compile-time references from <c>App.xaml.cs</c>.
/// </summary>
public sealed partial class ReceiverProcessService
{
    public event EventHandler<HlsPlayEventArgs>? HlsNativePlayStarted
    {
        add { _ = value; }
        remove { _ = value; }
    }

    public event EventHandler? HlsNativeStopped
    {
        add { _ = value; }
        remove { _ = value; }
    }

    public event EventHandler<HlsPositionEventArgs>? HlsNativePositionUpdated
    {
        add { _ = value; }
        remove { _ = value; }
    }

    public event EventHandler<HlsAudioTracksEventArgs>? HlsNativeAudioTracksUpdated
    {
        add { _ = value; }
        remove { _ = value; }
    }

    public void SendNativePause() { }
    public void SendNativeResume() { }
    public void SendNativeSeek(float positionSeconds) { _ = positionSeconds; }
    public void SendNativeAudioTrack(int index) { _ = index; }
}

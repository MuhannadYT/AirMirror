using System.Windows;

namespace AirMirror;

/// <summary>
/// Minimal stub used only by native-HLS playback paths (when the user opts out of the
/// in-app LibVLC player). With AirMirror's default settings (UseInAppVideoPlayer=true),
/// this overlay is never instantiated. Provides just enough surface area for App.xaml.cs
/// to compile and run.
/// </summary>
public sealed class HlsOverlayWindow : Window
{
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler<float>? SeekRequested;
    public event EventHandler<int>? AudioTrackSelected;

    public HlsOverlayWindow()
    {
        Width = 480;
        Height = 96;
        WindowStyle = WindowStyle.ToolWindow;
        Title = "AirPlay Video";
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Black;
    }

    public void ShowOverlayFor(string title)
    {
        Title = title;
        if (!IsVisible) Show();
    }

    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }

    public void ApplyPosition(double durationSeconds, double positionSeconds, float rate)
    {
        // Stub: native-mode position telemetry is intentionally unused.
        _ = durationSeconds;
        _ = positionSeconds;
        _ = rate;
    }

    public void ApplyAudioTracks(IReadOnlyList<string> labels, int currentIndex)
    {
        _ = labels;
        _ = currentIndex;
    }

    private void RaiseEventsToSilenceWarnings()
    {
        // Reference handlers so they're not flagged as unused (they're part of public API).
        PauseRequested?.Invoke(this, EventArgs.Empty);
        ResumeRequested?.Invoke(this, EventArgs.Empty);
        SeekRequested?.Invoke(this, 0f);
        AudioTrackSelected?.Invoke(this, 0);
    }
}

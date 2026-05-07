using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using WpfButton = System.Windows.Controls.Button;

namespace AirMirror;

public partial class PlaybackWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _player;
    private readonly DispatcherTimer _uiTimer;
    private bool _isScrubbing;
    private bool _isMuted;
    private int _lastVolume = 100;
    private bool _suppressTrackEvents;
    private bool _closed;
    private bool _hasStarted;
    private string _pendingUrl;
    private float _pendingStartSeconds;
    private string _pendingTitle;
    private double? _airPlayDurationMs;
    private DateTime _lastStateSentUtc = DateTime.MinValue;
    private bool _activeSession;
    private DateTime _lastAirPlayHeartbeatUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _remoteRateTimer;
    private float? _pendingRemoteRate;
    private float _reportedRate = 1.0f;
    private DateTime _reportedRateOverrideUntilUtc = DateTime.MinValue;
    private readonly DispatcherTimer _hideChromeTimer;
    private bool _chromeVisible = true;
    private static readonly TimeSpan ChromeHideDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ChromeFadeDuration = TimeSpan.FromMilliseconds(220);
    private readonly DispatcherTimer _cursorPollTimer;
    private System.Drawing.Point _lastCursorPos;
    private bool _wasLeftMouseDown;
    private bool _videoClickStartedInside;
    private static readonly Geometry PlayIcon = Geometry.Parse("M8,5 L32,20 L8,35 Z");
    private static readonly Geometry PauseIcon = Geometry.Parse("M9,6 H16 V34 H9 Z M24,6 H31 V34 H24 Z");
    private static readonly Geometry VolumeIcon = Geometry.Parse("M7,16 L14,16 L25,8 L25,32 L14,24 L7,24 Z M30,14 C34,18 34,22 30,26");
    private static readonly Geometry MutedIcon = Geometry.Parse("M7,16 L14,16 L25,8 L25,32 L14,24 L7,24 Z M30,14 L36,26 M36,14 L30,26");
    // Material-style gear (filled, with center hole via even-odd fill rule).
    private static readonly Geometry SettingsIcon = Geometry.Parse("F0 M19.43 12.98 C 19.47 12.66 19.5 12.34 19.5 12 C 19.5 11.66 19.47 11.34 19.43 11.02 L 21.54 9.37 C 21.73 9.22 21.78 8.95 21.66 8.73 L 19.66 5.27 C 19.54 5.05 19.27 4.97 19.05 5.05 L 16.56 6.05 C 16.04 5.65 15.48 5.32 14.87 5.07 L 14.49 2.42 C 14.45 2.18 14.25 2 14.01 2 L 9.99 2 C 9.75 2 9.56 2.18 9.52 2.42 L 9.14 5.07 C 8.53 5.32 7.97 5.66 7.45 6.05 L 4.96 5.05 C 4.73 4.96 4.47 5.05 4.35 5.27 L 2.35 8.73 C 2.23 8.95 2.28 9.22 2.47 9.37 L 4.58 11.02 C 4.54 11.34 4.5 11.67 4.5 12 C 4.5 12.33 4.53 12.66 4.57 12.98 L 2.46 14.63 C 2.27 14.78 2.22 15.05 2.34 15.27 L 4.34 18.73 C 4.46 18.95 4.73 19.03 4.95 18.95 L 7.44 17.95 C 7.96 18.35 8.52 18.68 9.13 18.93 L 9.51 21.58 C 9.56 21.82 9.75 22 9.99 22 L 14.01 22 C 14.25 22 14.45 21.82 14.48 21.58 L 14.86 18.93 C 15.47 18.68 16.03 18.34 16.55 17.95 L 19.04 18.95 C 19.27 19.04 19.53 18.95 19.65 18.73 L 21.65 15.27 C 21.77 15.05 21.72 14.78 21.53 14.63 L 19.43 12.98 Z M 12 15.5 C 10.07 15.5 8.5 13.93 8.5 12 C 8.5 10.07 10.07 8.5 12 8.5 C 13.93 8.5 15.5 10.07 15.5 12 C 15.5 13.93 13.93 15.5 12 15.5 Z");
    private static readonly Geometry FullscreenIcon = Geometry.Parse("M7,15 L7,7 L15,7 M25,7 L33,7 L33,15 M33,25 L33,33 L25,33 M15,33 L7,33 L7,25");
    private DateTime _settingsPopupClosedUtc = DateTime.MinValue;

    public bool IsClosed => _closed;
    public event EventHandler<PlaybackStateEventArgs>? PlaybackStateChanged;
    public event EventHandler? AirPlayClientStale;
    public event EventHandler<string>? PlaybackDiagnostic;

    public sealed record TrackEntry(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    public PlaybackWindow(LibVLC libVlc, string url, float startSeconds, string title)
    {
        InitializeComponent();
        _libVlc = libVlc;
        _player = new VlcMediaPlayer(_libVlc);
        VideoView.MediaPlayer = _player;
        _pendingUrl = url;
        _pendingStartSeconds = startSeconds;
        _pendingTitle = title;
        Title = title;
        TitleText.Text = title;
        SetPlayPauseIcon(isPaused: true);
        SetMuteIcon(isMuted: false);
        SetFilledIcon(SettingsButton, SettingsIcon, 22, 22);
        SetStrokeIcon(FullscreenButton, FullscreenIcon, 21, 21);
        SettingsPopup.Closed += (_, _) => _settingsPopupClosedUtc = DateTime.UtcNow;

        _player.LengthChanged += (_, _) => Dispatcher.BeginInvoke(RefreshLength);
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            SetReportedRate(1.0f);
            SetPlayPauseIcon(isPaused: false);
            RefreshTracks();
            SendPlaybackState(force: true);
        });
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            SetReportedRate(0.0f);
            SetPlayPauseIcon(isPaused: true);
            SendPlaybackState(force: true);
        });
        _player.Stopped += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            SetReportedRate(0.0f);
            SetPlayPauseIcon(isPaused: true);
            SendPlaybackState(force: true);
        });
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            SetReportedRate(0.0f);
            SetPlayPauseIcon(isPaused: true);
            SendPlaybackState(force: true);
        });
        _player.ESAdded += (_, _) => Dispatcher.BeginInvoke(RefreshTracks);
        _player.ESDeleted += (_, _) => Dispatcher.BeginInvoke(RefreshTracks);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiTimer.Tick += OnTick;
        _uiTimer.Start();

        _remoteRateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _remoteRateTimer.Tick += (_, _) => ApplyPendingRemoteRate();

        _hideChromeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ChromeHideDelay
        };
        _hideChromeTimer.Tick += (_, _) => HideChrome();

        // VLC's VideoView is a native HwndHost; WPF MouseMove does NOT fire when the cursor
        // is over it. Poll the global cursor position so we can detect mouse movement over
        // the video surface and show the chrome.
        _cursorPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _cursorPollTimer.Tick += OnCursorPollTick;

        Loaded += (_, _) =>
        {
            StartPendingPlayback();
            UpdateChromePopupGeometry();
            TopChromePopup.IsOpen = true;
            BottomChromePopup.IsOpen = true;
            ScheduleHideChrome();
            _lastCursorPos = System.Windows.Forms.Cursor.Position;
            _cursorPollTimer.Start();
        };
    }

    private void OnCursorPollTick(object? sender, EventArgs e)
    {
        if (_closed || !IsActive)
        {
            return;
        }
        var pos = System.Windows.Forms.Cursor.Position;
        if (pos == _lastCursorPos)
        {
            PollVideoClick(pos);
            return;
        }
        _lastCursorPos = pos;
        PollVideoClick(pos);
        if (!IsCursorInsideWindow(pos)) return;
        ShowChrome();
        ScheduleHideChrome();
    }

    private void SetPlayPauseIcon(bool isPaused)
    {
        SetFilledIcon(PlayPauseButton, isPaused ? PlayIcon : PauseIcon, isPaused ? 22 : 20, isPaused ? 22 : 20);
    }

    private void SetMuteIcon(bool isMuted)
    {
        SetStrokeIcon(MuteButton, isMuted ? MutedIcon : VolumeIcon, 23, 23);
    }

    private static void SetFilledIcon(WpfButton button, Geometry geometry, double width, double height)
    {
        button.Content = new Path
        {
            Data = geometry,
            Fill = System.Windows.Media.Brushes.White,
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };
    }

    private static void SetStrokeIcon(WpfButton button, Geometry geometry, double width, double height)
    {
        button.Content = new Path
        {
            Data = geometry,
            Fill = System.Windows.Media.Brushes.Transparent,
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };
    }

    private void PollVideoClick(System.Drawing.Point pos)
    {
        var leftDown = (System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) == System.Windows.Forms.MouseButtons.Left;
        if (leftDown && !_wasLeftMouseDown)
        {
            _videoClickStartedInside = IsVideoSurfaceClickTarget(pos);
        }
        else if (!leftDown && _wasLeftMouseDown)
        {
            if (_videoClickStartedInside && IsVideoSurfaceClickTarget(pos))
            {
                ShowChrome();
                ScheduleHideChrome();
                TogglePlayPause();
            }
            _videoClickStartedInside = false;
        }
        _wasLeftMouseDown = leftDown;
    }

    private bool IsVideoSurfaceClickTarget(System.Drawing.Point pos)
    {
        // Only treat clicks that occur over the actual VideoView surface as video-area clicks.
        // This prevents OS title-bar drags (mouse-down on caption, then release) from being
        // interpreted as a click on the video and toggling play/pause.
        return IsCursorOverVideoView(pos) && !IsMouseOverChrome() && !SettingsPopup.IsOpen;
    }

    private bool IsCursorOverVideoView(System.Drawing.Point pos)
    {
        try
        {
            if (!IsLoaded || VideoView == null) return false;
            if (VideoView.ActualWidth <= 0 || VideoView.ActualHeight <= 0) return false;
            var tl = VideoView.PointToScreen(new System.Windows.Point(0, 0));
            var br = VideoView.PointToScreen(new System.Windows.Point(VideoView.ActualWidth, VideoView.ActualHeight));
            return pos.X >= tl.X && pos.X < br.X && pos.Y >= tl.Y && pos.Y < br.Y;
        }
        catch
        {
            return false;
        }
    }

    private bool IsCursorInsideWindow(System.Drawing.Point pos)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return false;
            if (!GetWindowRect(helper.Handle, out var r)) return false;
            return pos.X >= r.Left && pos.X < r.Right && pos.Y >= r.Top && pos.Y < r.Bottom;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private void UpdateChromePopupGeometry()
    {
        if (RootGrid == null || !RootGrid.IsLoaded)
        {
            return;
        }

        var w = RootGrid.ActualWidth;
        var h = RootGrid.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        const double bottomChromeHeight = 102.0;
        const double topChromeHeight = 64.0;

        try
        {
            // Compute the on-screen pixel rectangle of RootGrid.
            var rootTl = RootGrid.PointToScreen(new System.Windows.Point(0, 0));
            var rootBr = RootGrid.PointToScreen(new System.Windows.Point(w, h));
            int gridLeft = (int)Math.Round(rootTl.X);
            int gridTop = (int)Math.Round(rootTl.Y);
            int gridWidthPx = Math.Max(1, (int)Math.Round(rootBr.X - rootTl.X));
            int gridHeightPx = Math.Max(1, (int)Math.Round(rootBr.Y - rootTl.Y));
            int topHeightPx = (int)Math.Round(topChromeHeight * (gridHeightPx / Math.Max(1.0, h)));
            int bottomHeightPx = (int)Math.Round(bottomChromeHeight * (gridHeightPx / Math.Max(1.0, h)));

            TopChrome.Width = w;
            BottomChrome.Width = w;

            // Open the popups (must be open before they have an HWND), with arbitrary offsets;
            // we'll override the actual position via SetWindowPos below.
            if (!TopChromePopup.IsOpen) TopChromePopup.IsOpen = true;
            if (!BottomChromePopup.IsOpen) BottomChromePopup.IsOpen = true;

            ForcePopupScreenRect(TopChromePopup, gridLeft, gridTop, gridWidthPx, topHeightPx);
            ForcePopupScreenRect(BottomChromePopup, gridLeft, gridTop + gridHeightPx - bottomHeightPx, gridWidthPx, bottomHeightPx);
        }
        catch
        {
            TopChrome.Width = w;
            BottomChrome.Width = w;
            TopChromePopup.HorizontalOffset = 0;
            TopChromePopup.VerticalOffset = 0;
            BottomChromePopup.HorizontalOffset = 0;
            BottomChromePopup.VerticalOffset = Math.Max(0, h - bottomChromeHeight);
            ReanchorPopup(TopChromePopup);
            ReanchorPopup(BottomChromePopup);
        }
    }

    private static System.Reflection.PropertyInfo? _popupChildProperty;

    private static void ForcePopupScreenRect(System.Windows.Controls.Primitives.Popup popup, int x, int y, int w, int h)
    {
        // Reach into the popup's internal child window HWND and SetWindowPos it directly.
        // WPF's Popup auto-clamps to the monitor; calling SetWindowPos afterwards bypasses
        // that and lets the chrome sit exactly on the video, even when the host window is
        // partially off-screen at a monitor edge.
        try
        {
            _popupChildProperty ??= typeof(System.Windows.Controls.Primitives.Popup).GetProperty(
                "Child",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (popup.Child is not System.Windows.UIElement childElement) return;
            var src = (System.Windows.Interop.HwndSource?)System.Windows.PresentationSource.FromVisual(childElement);
            if (src == null) return;
            var hwnd = src.Handle;
            if (hwnd == IntPtr.Zero) return;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_NOOWNERZORDER = 0x0200;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly System.Reflection.MethodInfo? PopupUpdatePositionMethod =
        typeof(System.Windows.Controls.Primitives.Popup).GetMethod(
            "UpdatePosition",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static void ReanchorPopup(System.Windows.Controls.Primitives.Popup popup)
    {
        // WPF Popup positions are sticky once opened. The internal UpdatePosition() is the
        // only reliable way to force WPF to re-place the popup HWND relative to its placement
        // target after the target has moved or resized. (Toggling HorizontalOffset by a tiny
        // delta is unreliable across window moves between monitors.)
        if (!popup.IsOpen) return;
        try
        {
            PopupUpdatePositionMethod?.Invoke(popup, null);
        }
        catch
        {
            var ho = popup.HorizontalOffset;
            popup.HorizontalOffset = ho + 0.001;
            popup.HorizontalOffset = ho;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateChromePopupGeometry();
    }

    private void OnWindowGeometryChanged(object? sender, EventArgs e)
    {
        UpdateChromePopupGeometry();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Hide popups while minimized so they don't float on the desktop.
        if (WindowState == WindowState.Minimized)
        {
            TopChromePopup.IsOpen = false;
            BottomChromePopup.IsOpen = false;
        }
        else
        {
            TopChromePopup.IsOpen = true;
            BottomChromePopup.IsOpen = true;
            UpdateChromePopupGeometry();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Don't show overlays when the player isn't focused (otherwise popups float on top of
        // other windows since they're separate HWNDs).
        TopChromePopup.IsOpen = false;
        BottomChromePopup.IsOpen = false;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            TopChromePopup.IsOpen = true;
            BottomChromePopup.IsOpen = true;
            UpdateChromePopupGeometry();
        }
    }

    private void OnChromeMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowChrome();
        _hideChromeTimer.Stop();
    }

    private void OnChromeMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ScheduleHideChrome();
    }

    private void OnChromeMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowChrome();
        _hideChromeTimer.Stop();
    }

    private void StartPendingPlayback()
    {
        if (_hasStarted)
        {
            return;
        }

        Title = _pendingTitle;
        TitleText.Text = _pendingTitle;
        StartPlayback(_pendingUrl, _pendingStartSeconds);
    }

    private void StartPlayback(string url, float startSeconds)
    {
        var media = new Media(_libVlc, url, FromType.FromLocation);
        media.AddOption(":network-caching=1500");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        if (startSeconds > 0)
        {
            media.AddOption($":start-time={startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        _player.Play(media);
        media.Dispose();
        _hasStarted = true;
        _activeSession = true;
        SetReportedRate(1.0f, TimeSpan.FromSeconds(2));
        _lastAirPlayHeartbeatUtc = DateTime.UtcNow;
    }

    public void Replace(string url, float startSeconds, string title)
    {
        _pendingUrl = url;
        _pendingStartSeconds = startSeconds;
        _pendingTitle = title;
        _airPlayDurationMs = null;
        Title = title;
        TitleText.Text = title;
        if (!_hasStarted)
        {
            return;
        }

        try
        {
            _player.Stop();
        }
        catch
        {
        }
        StartPlayback(url, startSeconds);
    }

    public void SetAirPlayDuration(double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return;
        }

        _airPlayDurationMs = durationSeconds * 1000.0;
        RefreshLength();
        SendPlaybackState(force: true);
    }

    public void MarkAirPlayClientAlive()
    {
        _lastAirPlayHeartbeatUtc = DateTime.UtcNow;
    }

    public void ApplyRemoteRate(float rate)
    {
        PlaybackDiagnostic?.Invoke(this, $"Player queued remote rate {rate:0.000}");
        _pendingRemoteRate = rate;
        _remoteRateTimer.Stop();
        _remoteRateTimer.Start();
    }

    private void ApplyPendingRemoteRate()
    {
        _remoteRateTimer.Stop();
        if (_pendingRemoteRate is not { } rate)
        {
            return;
        }
        _pendingRemoteRate = null;

        if (rate == 0.0f)
        {
            SetReportedRate(0.0f, TimeSpan.FromSeconds(2));
            _player.SetPause(true);
            SetPlayPauseIcon(isPaused: true);
            PlaybackDiagnostic?.Invoke(this, "Player applied remote pause");
            SendPlaybackState(force: true);
        }
        else if (rate == 1.0f)
        {
            SetReportedRate(1.0f, TimeSpan.FromSeconds(2));
            if (_player.State == VLCState.Paused)
            {
                _player.SetPause(false);
            }
            else if (!_player.IsPlaying)
            {
                _player.Play();
            }
            SetPlayPauseIcon(isPaused: false);
            PlaybackDiagnostic?.Invoke(this, "Player applied remote play");
            SendPlaybackState(force: true);
        }
    }

    public void SeekToSeconds(float seconds)
    {
        var displayMs = Math.Max(0, seconds * 1000.0);
        _player.Time = DisplayToPlayerMs(displayMs);
        UpdateDisplayedPosition();
        SendPlaybackState(force: true);
    }

    public void StopPlayback()
    {
        _activeSession = false;
        try
        {
            SetReportedRate(0.0f);
            _player.Stop();
        }
        catch
        {
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        StopIfAirPlayClientWentAway();
        if (!_isScrubbing)
        {
            UpdateDisplayedPosition();
        }
        SendPlaybackState(force: false);
    }

    private void StopIfAirPlayClientWentAway()
    {
        if (!_activeSession || !_player.IsPlaying)
        {
            return;
        }

        var positionMs = PlayerToDisplayMs(_player.Time);
        var timeout = positionMs > TimeSpan.FromMinutes(14).TotalMilliseconds
            ? TimeSpan.FromSeconds(45)
            : TimeSpan.FromSeconds(4);
        if (DateTime.UtcNow - _lastAirPlayHeartbeatUtc <= timeout)
        {
            return;
        }

        StopPlayback();
        Hide();
        AirPlayClientStale?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshLength()
    {
        var duration = DisplayDurationMs;
        if (duration > 0)
        {
            ScrubBar.Maximum = duration;
            DurationRun.Text = FormatTime(duration);
        }
    }

    private void UpdateDisplayedPosition()
    {
        var duration = DisplayDurationMs;
        var position = PlayerToDisplayMs(_player.Time);
        if (duration > 0)
        {
            ScrubBar.Maximum = duration;
            ScrubBar.Value = Math.Clamp(position, 0, duration);
            DurationRun.Text = FormatTime(duration);
        }
        CurrentTimeRun.Text = FormatTime(position);
    }

    private double DisplayDurationMs
    {
        get
        {
            if (_airPlayDurationMs is > 0)
            {
                return _airPlayDurationMs.Value;
            }
            return Math.Max(0, _player.Length);
        }
    }

    private double PlayerToDisplayMs(long playerMs)
    {
        var playerDuration = _player.Length;
        var displayDuration = DisplayDurationMs;
        if (_airPlayDurationMs is > 0 && playerDuration > 0 && displayDuration > 0)
        {
            return Math.Clamp(playerMs * (displayDuration / playerDuration), 0, displayDuration);
        }
        return Math.Max(0, playerMs);
    }

    private long DisplayToPlayerMs(double displayMs)
    {
        var playerDuration = _player.Length;
        var displayDuration = DisplayDurationMs;
        if (_airPlayDurationMs is > 0 && playerDuration > 0 && displayDuration > 0)
        {
            return (long)Math.Clamp(displayMs * (playerDuration / displayDuration), 0, playerDuration);
        }
        return (long)Math.Max(0, displayMs);
    }

    private void SendPlaybackState(bool force)
    {
        if (!_activeSession)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastStateSentUtc < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        var duration = DisplayDurationMs;
        if (duration <= 0)
        {
            return;
        }

        var position = Math.Clamp(PlayerToDisplayMs(_player.Time), 0, duration);
        var rate = CurrentReportedRate;
        _lastStateSentUtc = now;
        PlaybackStateChanged?.Invoke(this, new PlaybackStateEventArgs(duration / 1000.0, position / 1000.0, rate));
    }

    private void SetReportedRate(float rate, TimeSpan? overrideFor = null)
    {
        _reportedRate = rate;
        _reportedRateOverrideUntilUtc = overrideFor is { } duration
            ? DateTime.UtcNow + duration
            : DateTime.MinValue;
    }

    private float CurrentReportedRate => DateTime.UtcNow <= _reportedRateOverrideUntilUtc
        ? _reportedRate
        : (_player.IsPlaying ? 1.0f : 0.0f);

    private void RefreshTracks()
    {
        _suppressTrackEvents = true;
        try
        {
            AudioTrackCombo.Items.Clear();
            foreach (var track in _player.AudioTrackDescription)
            {
                AudioTrackCombo.Items.Add(new TrackEntry(track.Id, track.Name ?? $"Track {track.Id}"));
            }
            SelectTrackById(AudioTrackCombo, _player.AudioTrack);

            SubtitleCombo.Items.Clear();
            SubtitleCombo.Items.Add(new TrackEntry(-1, "Off"));
            foreach (var track in _player.SpuDescription)
            {
                if (track.Id == -1) continue;
                SubtitleCombo.Items.Add(new TrackEntry(track.Id, track.Name ?? $"Sub {track.Id}"));
            }
            SelectTrackById(SubtitleCombo, _player.Spu);
        }
        finally
        {
            _suppressTrackEvents = false;
        }
    }

    private static void SelectTrackById(System.Windows.Controls.ComboBox combo, int id)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is TrackEntry entry && entry.Id == id)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static string FormatTime(double ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private void OnScrubMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
    }

    private void OnScrubMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = false;
        if (DisplayDurationMs > 0)
        {
            _player.Time = DisplayToPlayerMs(ScrubBar.Value);
            UpdateDisplayedPosition();
            SendPlaybackState(force: true);
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private DateTime _lastToggleUtc = DateTime.MinValue;
    private int _toggleInFlight;

    private void TogglePlayPause()
    {
        // Debounce: ignore rapid repeats (e.g. button click + global cursor-poll click both
        // firing for the same physical click) which can deadlock LibVLC.
        var now = DateTime.UtcNow;
        if ((now - _lastToggleUtc).TotalMilliseconds < 250) return;
        if (System.Threading.Interlocked.Exchange(ref _toggleInFlight, 1) == 1) return;
        _lastToggleUtc = now;

        bool wantPause = _player.IsPlaying;
        if (wantPause)
        {
            SetReportedRate(0.0f, TimeSpan.FromSeconds(2));
            SetPlayPauseIcon(isPaused: true);
        }
        else
        {
            SetReportedRate(1.0f, TimeSpan.FromSeconds(2));
            SetPlayPauseIcon(isPaused: false);
        }

        // Run the actual LibVLC call off the UI thread. SetPause / Play can block while
        // VLC transitions between states; doing it inline freezes the WPF dispatcher and
        // makes the app appear unresponsive (the user has to force-close).
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (wantPause)
                {
                    _player.SetPause(true);
                }
                else if (_player.State == VLCState.Paused)
                {
                    _player.SetPause(false);
                }
                else
                {
                    _player.Play();
                }
            }
            catch
            {
                // Swallow — VLC may throw if the player is being torn down.
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _toggleInFlight, 0);
                try
                {
                    Dispatcher.BeginInvoke(() => SendPlaybackState(force: true));
                }
                catch { }
            }
        });
    }

    private void SeekRelative(double seconds)
    {
        var displayTarget = Math.Clamp(PlayerToDisplayMs(_player.Time) + seconds * 1000.0, 0, Math.Max(DisplayDurationMs, 0));
        _player.Time = DisplayToPlayerMs(displayTarget);
        UpdateDisplayedPosition();
        SendPlaybackState(force: true);
    }

    private void OnRewindClick(object sender, RoutedEventArgs e)
    {
        SeekRelative(-10);
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        SeekRelative(10);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        if (_isMuted)
        {
            _lastVolume = _player.Volume;
            _player.Volume = 0;
            VolumeSlider.Value = 0;
            SetMuteIcon(isMuted: true);
        }
        else
        {
            _player.Volume = _lastVolume;
            VolumeSlider.Value = _lastVolume;
            SetMuteIcon(isMuted: false);
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        var v = (int)Math.Clamp(e.NewValue, 0, 100);
        _player.Volume = v;
        if (v == 0)
        {
            SetMuteIcon(isMuted: true);
            _isMuted = true;
        }
        else
        {
            SetMuteIcon(isMuted: false);
            _isMuted = false;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // WPF Popup with StaysOpen=False auto-closes on outside click BEFORE the button
        // Click event fires. Without this guard, clicking the settings button while the
        // popup is open would dismiss it and immediately reopen it. Suppress the reopen
        // if the popup just closed within the last ~300ms.
        if (!SettingsPopup.IsOpen &&
            (DateTime.UtcNow - _settingsPopupClosedUtc).TotalMilliseconds < 300)
        {
            return;
        }
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
        if (SettingsPopup.IsOpen)
        {
            ShowChrome();
            _hideChromeTimer.Stop();
        }
        else
        {
            ScheduleHideChrome();
        }
    }

    private void OnAudioTrackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTrackEvents) return;
        if (AudioTrackCombo.SelectedItem is TrackEntry entry)
        {
            _player.SetAudioTrack(entry.Id);
        }
    }

    private void OnSubtitleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTrackEvents) return;
        if (SubtitleCombo.SelectedItem is TrackEntry entry)
        {
            _player.SetSpu(entry.Id);
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ToggleFullscreen()
    {
        if (IsCurrentlyFullscreen())
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private bool IsCurrentlyFullscreen()
    {
        return WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized;
    }

    public void EnterFullscreen()
    {
        if (IsCurrentlyFullscreen()) return;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    public void ExitFullscreen()
    {
        if (!IsCurrentlyFullscreen()) return;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        ShowChrome();
        ScheduleHideChrome();
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                OnPlayPauseClick(sender, e);
                e.Handled = true;
                break;
            case Key.J:
            case Key.Left:
                SeekRelative(-10);
                e.Handled = true;
                break;
            case Key.L:
            case Key.Right:
                SeekRelative(10);
                e.Handled = true;
                break;
            case Key.M:
                OnMuteClick(sender, e);
                e.Handled = true;
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (SettingsPopup.IsOpen)
                {
                    SettingsPopup.IsOpen = false;
                    e.Handled = true;
                }
                else if (WindowStyle == WindowStyle.None)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _activeSession = false;
        _uiTimer.Stop();
        _remoteRateTimer.Stop();
        _hideChromeTimer.Stop();
        _cursorPollTimer.Stop();
        try { TopChromePopup.IsOpen = false; } catch { }
        try { BottomChromePopup.IsOpen = false; } catch { }
        try { SettingsPopup.IsOpen = false; } catch { }
        try
        {
            _player.Stop();
        }
        catch
        {
        }
        VideoView.MediaPlayer = null;
        _player.Dispose();
    }

    private void OnRootMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowChrome();
        ScheduleHideChrome();
    }

    private void OnRootMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ScheduleHideChrome(immediate: true);
    }

    private void ShowChrome()
    {
        if (_chromeVisible) return;
        _chromeVisible = true;
        Cursor = null;
        AnimateChromeOpacity(1.0);
    }

    private void HideChrome()
    {
        _hideChromeTimer.Stop();
        if (!_chromeVisible) return;
        if (SettingsPopup.IsOpen) return;
        if (IsMouseOverChrome()) return;
        _chromeVisible = false;
        Cursor = System.Windows.Input.Cursors.None;
        AnimateChromeOpacity(0.0);
    }

    private void ScheduleHideChrome(bool immediate = false)
    {
        _hideChromeTimer.Stop();
        if (immediate)
        {
            HideChrome();
            return;
        }
        _hideChromeTimer.Start();
    }

    private bool IsMouseOverChrome()
    {
        return BottomChrome.IsMouseOver || TopChrome.IsMouseOver
               || BottomChromePopup.IsMouseOver || TopChromePopup.IsMouseOver;
    }

    private void AnimateChromeOpacity(double target)
    {
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = ChromeFadeDuration,
            FillBehavior = FillBehavior.HoldEnd
        };
        BottomChrome.BeginAnimation(OpacityProperty, anim);
        TopChrome.BeginAnimation(OpacityProperty, anim);
    }
}

public sealed record PlaybackStateEventArgs(double DurationSeconds, double PositionSeconds, float Rate);

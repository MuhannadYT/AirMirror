using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using AirMirror.Services;
using LibVLCSharp.Shared;

namespace AirMirror;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private ReceiverProcessService? _receiver;
    private SettingsStore? _settingsStore;
    private LibVLC? _libVlc;
    private PlaybackWindow? _playback;
    private HlsOverlayWindow? _overlay;

    public ReceiverProcessService Receiver => _receiver ?? throw new InvalidOperationException("Receiver not initialized.");

    private void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Core.Initialize();
            _libVlc = new LibVLC("--no-video-title-show", "--network-caching=1500");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to initialize LibVLC: {ex.Message}\n\nVideo playback controls will be unavailable.",
                "AirMirror",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _settingsStore = new SettingsStore();
        var settings = _settingsStore.Load();
        StartupRegistration.Apply(settings.LaunchOnSystemStartup);
        _receiver = new ReceiverProcessService(settings);
        _receiver.StateChanged += (_, _) => UpdateTrayMenu();
        _receiver.HlsPlayRequested += OnHlsPlayRequested;
        _receiver.HlsStopRequested += OnHlsStopRequested;
        _receiver.HlsRateChanged += OnHlsRateChanged;
        _receiver.HlsScrubRequested += OnHlsScrubRequested;
        _receiver.HlsDurationAvailable += OnHlsDurationAvailable;
        _receiver.HlsClientAlive += OnHlsClientAlive;
        _receiver.HlsNativePlayStarted += OnHlsNativePlayStarted;
        _receiver.HlsNativeStopped += OnHlsNativeStopped;
        _receiver.HlsNativePositionUpdated += OnHlsNativePositionUpdated;
        _receiver.HlsNativeAudioTracksUpdated += OnHlsNativeAudioTracksUpdated;

        CreateTrayIcon();

        _mainWindow = new MainWindow(_settingsStore, _receiver);
        _mainWindow.Show();

        if (settings.StartReceiverOnLaunch)
        {
            _ = _receiver.StartAsync();
        }
    }

    private void OnHlsPlayRequested(object? sender, HlsPlayEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_libVlc is null)
            {
                return;
            }

            var title = $"AirPlay Video — {e.Url}";
            if (_playback is { IsClosed: false })
            {
                _playback.Replace(e.Url, e.StartSeconds, title);
                if (!_playback.IsVisible)
                {
                    _playback.Show();
                }
                _playback.BringToFront();
                return;
            }

            _playback = new PlaybackWindow(_libVlc, e.Url, e.StartSeconds, title);
            _playback.PlaybackStateChanged += OnPlaybackStateChanged;
            _playback.AirPlayClientStale += OnAirPlayClientStale;
            _playback.PlaybackDiagnostic += OnPlaybackDiagnostic;
            _playback.Closed += (_, _) =>
            {
                _playback = null;
                // User closed the video window — tell uxplay to drop the AirPlay HLS session
                // so the iPhone returns to its picker instead of streaming into the void.
                try { _receiver?.SendHlsStopCommand(); } catch { }
            };
            _playback.Show();
            _playback.BringToFront();
            if (_settingsStore?.Load().AutoFullscreenVideo == true)
            {
                _playback.EnterFullscreen();
            }
        });
    }

    private void OnHlsStopRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_playback is { IsClosed: false })
            {
                _playback.StopPlayback();
                _playback.Hide();
            }
        });
    }

    private void OnHlsRateChanged(object? sender, HlsRateEventArgs e)
    {
        Dispatcher.BeginInvoke(() => _playback?.ApplyRemoteRate(e.Rate));
    }

    private void OnHlsScrubRequested(object? sender, HlsScrubEventArgs e)
    {
        Dispatcher.BeginInvoke(() => _playback?.SeekToSeconds(e.PositionSeconds));
    }

    private void OnHlsDurationAvailable(object? sender, HlsDurationEventArgs e)
    {
        Dispatcher.BeginInvoke(() => _playback?.SetAirPlayDuration(e.DurationSeconds));
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateEventArgs e)
    {
        _receiver?.SendHlsPlaybackState(e.DurationSeconds, e.PositionSeconds, e.Rate);
    }

    private void OnPlaybackDiagnostic(object? sender, string message)
    {
        _receiver?.LogDiagnostic(message);
    }

    private void OnHlsClientAlive(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _playback?.MarkAirPlayClientAlive());
    }

    private void OnAirPlayClientStale(object? sender, EventArgs e)
    {
        _receiver?.SendHlsStopCommand();
    }

    private void OnHlsNativePlayStarted(object? sender, HlsPlayEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_overlay is null)
            {
                _overlay = new HlsOverlayWindow();
                _overlay.PauseRequested += (_, _) => _receiver?.SendNativePause();
                _overlay.ResumeRequested += (_, _) => _receiver?.SendNativeResume();
                _overlay.SeekRequested += (_, position) => _receiver?.SendNativeSeek(position);
                _overlay.AudioTrackSelected += (_, idx) => _receiver?.SendNativeAudioTrack(idx);
                _overlay.Closed += (_, _) => _overlay = null;
            }
            _overlay.ShowOverlayFor("AirPlay Video");
        });
    }

    private void OnHlsNativeStopped(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _overlay?.HideOverlay());
    }

    private void OnHlsNativePositionUpdated(object? sender, HlsPositionEventArgs e)
    {
        Dispatcher.BeginInvoke(() => _overlay?.ApplyPosition(e.DurationSeconds, e.PositionSeconds, e.Rate));
    }

    private void OnHlsNativeAudioTracksUpdated(object? sender, HlsAudioTracksEventArgs e)
    {
        Dispatcher.BeginInvoke(() => _overlay?.ApplyAudioTracks(e.Labels, e.CurrentIndex));
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "AirMirror Receiver",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon?.ContextMenuStrip is null || _receiver is null)
        {
            return;
        }

        var menu = _trayIcon.ContextMenuStrip;
        menu.Items.Clear();
        menu.Items.Add("Open AirMirror", null, (_, _) => ShowMainWindow());
        menu.Items.Add(_receiver.IsRunning ? "Restart Receiver" : "Start Receiver", null, async (_, _) => await _receiver.RestartAsync());
        menu.Items.Add("Stop Receiver", null, async (_, _) => await _receiver.StopAsync()).Enabled = _receiver.IsRunning;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitApplicationAsync());
        _trayIcon.Text = _receiver.IsRunning ? "AirMirror Receiver - running" : "AirMirror Receiver - stopped";
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_receiver is not null)
        {
            await _receiver.StopAsync();
        }

        _trayIcon?.Dispose();
        Shutdown();
    }

    private async void OnExit(object sender, ExitEventArgs e)
    {
        if (_receiver is not null)
        {
            await _receiver.StopAsync();
        }

        try
        {
            _overlay?.Close();
        }
        catch { }

        try
        {
            _playback?.Close();
        }
        catch
        {
        }

        _libVlc?.Dispose();
        _trayIcon?.Dispose();
    }
}

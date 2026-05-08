using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirMirror.Models;
using AirMirror.Services;
using Microsoft.Win32;

namespace AirMirror;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly ReceiverProcessService _receiver;
    private AppSettings _settings;
    private MonitorInfo _primaryMonitor = MonitorService.GetPrimaryMonitor();

    public MainWindow(SettingsStore settingsStore, ReceiverProcessService receiver)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _receiver = receiver;
        _settings = _settingsStore.Load();

        _receiver.StateChanged += OnReceiverStateChanged;
        _receiver.LogReceived += OnLogReceived;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoControls();
        RefreshDisplayState();
        RefreshCommandPreview();

        // Show the user which version they're on inside the Updates panel.
        var ver = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";
        var plus = ver.IndexOf('+');
        if (plus >= 0) ver = ver[..plus];
        CurrentVersionText.Text = $"Current version: {ver}";
    }

    private void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            (System.Windows.Application.Current as App)?.CheckForUpdatesManually(this);
        }
        finally
        {
            // Re-enable shortly after so the user can re-trigger if they want; the actual
            // network call is async on a Task.Run thread inside CheckForUpdatesManually.
            Dispatcher.BeginInvoke(new Action(() => CheckForUpdatesButton.IsEnabled = true),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void LoadSettingsIntoControls()
    {
        _primaryMonitor = MonitorService.GetPrimaryMonitor();
        DetectedResolutionText.Text = $"{_primaryMonitor.ResolutionText} ({_primaryMonitor.DisplayName})";

        ResolutionModeBox.SelectedIndex = _settings.ResolutionMode == ResolutionMode.Auto ? 0 : 1;
        WidthBox.Text = _settings.CustomWidth.ToString();
        HeightBox.Text = _settings.CustomHeight.ToString();
        RefreshBox.Text = _settings.CustomRefreshRate.ToString();

        FullscreenRadio.IsChecked = _settings.StartMode == StartMode.Fullscreen;
        WindowedRadio.IsChecked = _settings.StartMode == StartMode.Windowed;
        AutoFullscreenVideoCheckBox.IsChecked = _settings.AutoFullscreenVideo;
        EnableHlsVideoCheckBox.IsChecked = _settings.EnableHlsVideo;
        AirPlayNameBox.Text = _settings.AirPlayName;
        AudioPcRadio.IsChecked = _settings.AudioOutput == AudioOutputMode.Pc;
        AudioIphoneRadio.IsChecked = _settings.AudioOutput == AudioOutputMode.Iphone;
        HdrAutoRadio.IsChecked = _settings.HdrSupport == HdrMode.Auto;
        HdrOnRadio.IsChecked = _settings.HdrSupport == HdrMode.On;
        HdrOffRadio.IsChecked = _settings.HdrSupport == HdrMode.Off;
        UxPlayPathBox.Text = _settings.UxPlayPath ?? "";
        LaunchCheckBox.IsChecked = _settings.StartReceiverOnLaunch;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;
        LaunchOnSystemStartupCheckBox.IsChecked = _settings.LaunchOnSystemStartup;
        UpdateUxPlayPlaceholder();

        UpdateCustomResolutionEnabled();
    }

    private AppSettings ReadSettingsFromControls()
    {
        var current = _settingsStore.Load();
        current.ResolutionMode = ResolutionModeBox.SelectedIndex == 1 ? ResolutionMode.Custom : ResolutionMode.Auto;
        current.CustomWidth = ReadInt(WidthBox.Text, 1920);
        current.CustomHeight = ReadInt(HeightBox.Text, 1080);
        current.CustomRefreshRate = ReadInt(RefreshBox.Text, 60);
        current.StartMode = WindowedRadio.IsChecked == true ? StartMode.Windowed : StartMode.Fullscreen;
        current.AutoFullscreenVideo = AutoFullscreenVideoCheckBox.IsChecked == true;
        current.EnableHlsVideo = EnableHlsVideoCheckBox.IsChecked == true;
        current.AirPlayName = string.IsNullOrWhiteSpace(AirPlayNameBox.Text) ? "AirMirror" : AirPlayNameBox.Text.Trim();
        current.AudioOutput = AudioIphoneRadio.IsChecked == true ? AudioOutputMode.Iphone : AudioOutputMode.Pc;
        current.HdrSupport = HdrOnRadio.IsChecked == true ? HdrMode.On : HdrOffRadio.IsChecked == true ? HdrMode.Off : HdrMode.Auto;
        current.UxPlayPath = string.IsNullOrWhiteSpace(UxPlayPathBox.Text) ? null : UxPlayPathBox.Text.Trim();
        current.StartReceiverOnLaunch = LaunchCheckBox.IsChecked == true;
        current.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        current.LaunchOnSystemStartup = LaunchOnSystemStartupCheckBox.IsChecked == true;
        return current;
    }

    private void SaveSettings()
    {
        _settings = ReadSettingsFromControls();
        _settingsStore.Save(_settings);
        StartupRegistration.Apply(_settings.LaunchOnSystemStartup);
        _receiver.UpdateSettings(_settings);
        RefreshDisplayState();
        RefreshCommandPreview();
    }

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_receiver.IsRunning)
        {
            await _receiver.StopAsync();
        }
        else
        {
            await _receiver.StartAsync();
        }

        RefreshDisplayState();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        AppendLog("Settings saved.");
    }

    private async void OnSaveRestartClick(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        await _receiver.RestartAsync();
        RefreshDisplayState();
    }

    private void OnBrowseUxPlayClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select uxplay.exe",
            Filter = "UxPlay executable|uxplay.exe|Executable files|*.exe|All files|*.*",
            InitialDirectory = Directory.Exists(@"C:\msys64\ucrt64\bin") ? @"C:\msys64\ucrt64\bin" : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            UxPlayPathBox.Text = dialog.FileName;
            RefreshCommandPreview();
        }
    }

    private void OnRestoreUxPlayDefaultClick(object sender, RoutedEventArgs e)
    {
        UxPlayPathBox.Text = "";
        RefreshCommandPreview();
    }

    private void OnUxPlayPathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateUxPlayPlaceholder();
    }

    private void UpdateUxPlayPlaceholder()
    {
        if (UxPlayPathPlaceholder != null && UxPlayPathBox != null)
        {
            UxPlayPathPlaceholder.Visibility = string.IsNullOrEmpty(UxPlayPathBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnResolutionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCustomResolutionEnabled();
        RefreshCommandPreview();
    }

    private void OnEnableHlsToggled(object sender, RoutedEventArgs e)
    {
        RefreshCommandPreview();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OnOpenUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OnReceiverStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RefreshDisplayState);
    }

    private void OnLogReceived(object? sender, string message)
    {
        Dispatcher.Invoke(() => AppendLog(message));
    }

    private void RefreshDisplayState()
    {
        StatusText.Text = _receiver.StatusText;
        StartStopButton.Content = _receiver.IsRunning ? "Stop" : "Start";
        StatusBadge.Background = _receiver.IsRunning
            ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush")
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 247, 251));

        StatusText.Foreground = _receiver.IsRunning
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");

        StatusSubtext.Text = _receiver.IsRunning ? "AirPlay running" : "AirPlay stopped";

        RefreshCommandPreview();
    }

    private void RefreshCommandPreview()
    {
        var settings = ReadSettingsFromControls();
        var args = ReceiverProcessService.BuildArguments(settings, _primaryMonitor);
        var path = string.IsNullOrWhiteSpace(settings.UxPlayPath)
            ? _receiver.ResolvedUxPlayPath ?? "uxplay.exe"
            : settings.UxPlayPath;
        CommandBox.Text = $"{path} {string.Join(" ", args.Select(QuoteForDisplay))}";
    }

    private void UpdateCustomResolutionEnabled()
    {
        var isCustom = ResolutionModeBox.SelectedIndex == 1;
        CustomResolutionPanel.IsEnabled = isCustom;
        CustomResolutionPanel.Opacity = isCustom ? 1 : 0.52;
    }

    private void AppendLog(string message)
    {
        if (LogBox.Text.Length > 12000)
        {
            LogBox.Text = LogBox.Text[^9000..];
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static int ReadInt(string text, int fallback)
    {
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
    }
}

using System.Diagnostics;
using System.IO;
using AirMirror.Models;

namespace AirMirror.Services;

public sealed partial class ReceiverProcessService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _settings;
    private Process? _process;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? LogReceived;
    public event EventHandler<HlsPlayEventArgs>? HlsPlayRequested;
    public event EventHandler? HlsStopRequested;
    public event EventHandler<HlsRateEventArgs>? HlsRateChanged;
    public event EventHandler<HlsScrubEventArgs>? HlsScrubRequested;
    public event EventHandler<HlsDurationEventArgs>? HlsDurationAvailable;
    public event EventHandler? HlsClientAlive;

    public bool IsRunning => _process is { HasExited: false };
    public string? ResolvedUxPlayPath { get; private set; }
    public string LastCommandLine { get; private set; } = "";
    public string StatusText { get; private set; } = "Stopped";

    public ReceiverProcessService(AppSettings settings)
    {
        _settings = settings;
        ResolvedUxPlayPath = ResolveUxPlayPath(settings);
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        ResolvedUxPlayPath = ResolveUxPlayPath(settings);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            ResolvedUxPlayPath = ResolveUxPlayPath(_settings);
            if (ResolvedUxPlayPath is null)
            {
                StatusText = "UxPlay not found";
                Log("UxPlay executable was not found. Build it into third_party\\UxPlay\\build or set the path in settings.");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var primary = MonitorService.GetPrimaryMonitor();
            var arguments = BuildArguments(_settings, primary);
            LastCommandLine = $"{ResolvedUxPlayPath} {string.Join(" ", arguments.Select(QuoteForDisplay))}";

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvedUxPlayPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ResolvedUxPlayPath) ?? AppContext.BaseDirectory
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            AddReceiverRuntimePath(startInfo, ResolvedUxPlayPath);

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            _process.OutputDataReceived += (_, e) => HandleLine(e.Data);
            _process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
            _process.Exited += (_, _) =>
            {
                StatusText = "Stopped";
                Log("Receiver process stopped.");
                StateChanged?.Invoke(this, EventArgs.Empty);
            };

            _process.Start();
            _process.StandardInput.AutoFlush = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            StatusText = "Broadcasting";
            Log("Receiver started.");
            Log(LastCommandLine);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            Log($"Failed to start receiver: {ex.Message}");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is null)
            {
                StatusText = "Stopped";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }
            }

            _process.Dispose();
            _process = null;
            StatusText = "Stopped";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public void SendHlsPlaybackState(double durationSeconds, double positionSeconds, float rate)
    {
        if (_process is null || _process.HasExited || durationSeconds <= 0 || positionSeconds < 0)
        {
            return;
        }

        try
        {
            var line = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"AIRMIRROR_STATE duration={durationSeconds:F3} position={positionSeconds:F3} rate={rate:F3}");
            _process.StandardInput.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    public void LogDiagnostic(string message)
    {
        Log(message);
    }

    public static IReadOnlyList<string> BuildArguments(AppSettings settings, MonitorInfo monitor)
    {
        var width = settings.ResolutionMode == ResolutionMode.Auto ? monitor.Width : settings.CustomWidth;
        var height = settings.ResolutionMode == ResolutionMode.Auto ? monitor.Height : settings.CustomHeight;
        var refresh = settings.ResolutionMode == ResolutionMode.Auto ? monitor.RefreshRate : settings.CustomRefreshRate;

        width = Math.Clamp(width, 640, 7680);
        height = Math.Clamp(height, 360, 4320);
        refresh = Math.Clamp(refresh, 24, 240);

        var args = new List<string>
        {
            "-n",
            string.IsNullOrWhiteSpace(settings.AirPlayName) ? "AirMirror" : settings.AirPlayName.Trim(),
            "-nh",
            "-s",
            $"{width}x{height}@{refresh}",
            "-vs",
            "d3d12videosink"
        };

        if (settings.EnableHlsVideo)
        {
            if (settings.AutoFullscreenVideo)
            {
                args.Add("-hlsfs");
            }

            args.Add("-hls");
            args.Add("2");
            if (settings.UseInAppVideoPlayer)
            {
                args.Add("-hls-external");
            }
        }

        if (settings.StartMode == StartMode.Fullscreen)
        {
            args.Add("-fs");
        }

        if (settings.AudioOutput == AudioOutputMode.Pc)
        {
            args.Add("-as");
            args.Add("wasapisink");
        }
        else
        {
            args.Add("-a");
        }

        if (ShouldEnableHevc(settings.HdrSupport, width, height))
        {
            args.Add("-h265");
        }

        return args;
    }

    public static bool ShouldEnableHevc(HdrMode hdrMode, int width, int height)
    {
        return hdrMode switch
        {
            HdrMode.On => true,
            HdrMode.Off => false,
            _ => Math.Max(width, height) >= 3840 || Math.Min(width, height) > 1080
        };
    }

    private static void AddReceiverRuntimePath(ProcessStartInfo startInfo, string uxplayPath)
    {
        var binDirectory = Path.GetDirectoryName(uxplayPath);
        if (string.IsNullOrWhiteSpace(binDirectory))
        {
            return;
        }

        // Bundled DLLs (MSYS2/UCRT64 runtime) live next to uxplay.exe in shipped builds.
        var runtimePaths = new List<string> { binDirectory };

        // Fall back to a developer's local MSYS2 install when bundled DLLs are absent
        // (e.g. running from `dotnet build` without having run stage-uxplay-deps.ps1).
        if (Directory.Exists(@"C:\msys64\ucrt64\bin"))
        {
            runtimePaths.Add(@"C:\msys64\ucrt64\bin");
        }
        if (Directory.Exists(@"C:\msys64\mingw64\bin"))
        {
            runtimePaths.Add(@"C:\msys64\mingw64\bin");
        }

        var existingPath = startInfo.Environment.TryGetValue("PATH", out var value)
            ? value
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = $"{string.Join(';', runtimePaths.Distinct(StringComparer.OrdinalIgnoreCase))};{existingPath}";

        // Prefer bundled GStreamer plugins (shipped with the installer) over any
        // dev-machine MSYS2 install, so the same code path works on end-user PCs.
        var bundledGstPlugins = Path.Combine(binDirectory, "lib", "gstreamer-1.0");
        if (Directory.Exists(bundledGstPlugins))
        {
            startInfo.Environment["GST_PLUGIN_PATH"] = bundledGstPlugins;
            startInfo.Environment["GST_PLUGIN_SYSTEM_PATH"] = bundledGstPlugins;
            startInfo.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = bundledGstPlugins;
            // Disable GStreamer's plugin registry caching across machines.
            startInfo.Environment["GST_REGISTRY_FORK"] = "no";
        }
        else
        {
            // Dev fallback: point at MSYS2's plugin tree.
            var gstPlugins = @"C:\msys64\ucrt64\lib\gstreamer-1.0";
            if (Directory.Exists(gstPlugins))
            {
                startInfo.Environment["GST_PLUGIN_SYSTEM_PATH_1_0"] = gstPlugins;
            }
        }

        // Bundled scanner if present, otherwise dev MSYS2 fallback.
        var bundledScanner = Path.Combine(binDirectory, "gst-plugin-scanner.exe");
        if (File.Exists(bundledScanner))
        {
            startInfo.Environment["GST_PLUGIN_SCANNER"] = bundledScanner;
        }
        else
        {
            var gstScanner = @"C:\msys64\ucrt64\libexec\gstreamer-1.0\gst-plugin-scanner.exe";
            if (File.Exists(gstScanner))
            {
                startInfo.Environment["GST_PLUGIN_SCANNER"] = gstScanner;
            }
        }
    }

    private static string? ResolveUxPlayPath(AppSettings settings)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.UxPlayPath))
        {
            candidates.Add(settings.UxPlayPath);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "uxplay", "uxplay.exe"));
        candidates.Add(@"C:\msys64\ucrt64\bin\uxplay.exe");
        candidates.Add(@"C:\msys64\mingw64\bin\uxplay.exe");

        var workspaceRoot = WorkspaceLocator.FindWorkspaceRoot();
        if (workspaceRoot is not null)
        {
            candidates.Add(Path.Combine(workspaceRoot, "third_party", "UxPlay", "build", "uxplay.exe"));
            candidates.Add(Path.Combine(workspaceRoot, "third_party", "UxPlay", "build-ucrt64", "uxplay.exe"));
            candidates.Add(Path.Combine(workspaceRoot, "tools", "uxplay", "uxplay.exe"));
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "uxplay.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
    }

    private void Log(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LogReceived?.Invoke(this, message);
    }

    private void HandleLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        const string marker = "__AIRMIRROR_HLS__";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            try
            {
                ParseHlsEvent(message.Substring(idx + marker.Length));
            }
            catch (Exception ex)
            {
                Log($"HLS event parse error: {ex.Message}");
            }
            // still emit to log so the user can see it in diagnostics
        }
        else
        {
            ParseHlsFallbackLog(message);
        }

        Log(message);
    }

    private void ParseHlsFallbackLog(string message)
    {
        if (message.Contains("client HTTP request POST stop", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("action type playlistRemove", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("hls video has sent EOS", StringComparison.OrdinalIgnoreCase))
        {
            HlsStopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

    }

    public void SendHlsStopCommand()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            _process.StandardInput.WriteLine("AIRMIRROR_STOP");
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void ParseHlsEvent(string payload)
    {
        // payload looks like:  event=play url=http://localhost:NNN/master.m3u8 start=0.000
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        var span = payload.AsSpan().TrimStart();
        while (i < span.Length)
        {
            // skip whitespace
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            var keyStart = i;
            while (i < span.Length && span[i] != '=' && !char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length || span[i] != '=') break;
            var key = span.Slice(keyStart, i - keyStart).ToString();
            i++; // skip =
            var valStart = i;
            // value runs until next whitespace followed by 'word=' or end
            while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
            var value = span.Slice(valStart, i - valStart).ToString();
            fields[key] = value;
        }

        if (!fields.TryGetValue("event", out var ev)) return;

        switch (ev)
        {
            case "play":
                if (fields.TryGetValue("url", out var url) && !string.IsNullOrWhiteSpace(url))
                {
                    float start = 0f;
                    if (fields.TryGetValue("start", out var startStr))
                    {
                        float.TryParse(startStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out start);
                    }
                    HlsPlayRequested?.Invoke(this, new HlsPlayEventArgs(url, start));
                }
                break;
            case "stop":
            case "eos":
                HlsStopRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "rate":
                if (fields.TryGetValue("value", out var rateStr) &&
                    float.TryParse(rateStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
                {
                    HlsRateChanged?.Invoke(this, new HlsRateEventArgs(rate));
                }
                break;
            case "scrub":
                if (fields.TryGetValue("position", out var positionStr) &&
                    float.TryParse(positionStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var position))
                {
                    HlsScrubRequested?.Invoke(this, new HlsScrubEventArgs(position));
                }
                break;
            case "duration":
                if (fields.TryGetValue("value", out var durationStr) &&
                    double.TryParse(durationStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration) &&
                    duration > 0)
                {
                    HlsDurationAvailable?.Invoke(this, new HlsDurationEventArgs(duration));
                }
                break;
            case "alive":
                HlsClientAlive?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}

public sealed record HlsPlayEventArgs(string Url, float StartSeconds);
public sealed record HlsRateEventArgs(float Rate);
public sealed record HlsScrubEventArgs(float PositionSeconds);
public sealed record HlsDurationEventArgs(double DurationSeconds);

public sealed record HlsPositionEventArgs(double DurationSeconds, double PositionSeconds, float Rate);
public sealed record HlsAudioTracksEventArgs(System.Collections.Generic.IReadOnlyList<string> Labels, int CurrentIndex);

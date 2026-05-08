using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AirMirror.Models;

namespace AirMirror.Services;

/// <summary>
/// Lightweight, opportunistic update checker.
///
/// Design choices (per user request):
///   * Never runs in the background — it's only kicked off when something user-driven
///     happens (an iPhone/iPad/Mac connects to AirPlay). That way we don't burn battery
///     or hit GitHub when the app is just sitting idle in the tray.
///   * Throttled to roughly once a week via <see cref="AppSettings.LastUpdateCheckUtc"/>
///     so even a chatty user with many connections won't spam the GitHub API (60 req/h
///     unauthenticated limit per IP).
///   * Honours <see cref="AppSettings.LastDismissedUpdateVersion"/> so once the user has
///     seen "v1.2.3 is available" we don't keep nagging them about that exact version on
///     every subsequent connect. They'll see the prompt again only when an even newer
///     release lands.
///   * Asks GitHub for /releases/latest, which by definition skips drafts and pre-releases.
/// </summary>
public sealed class UpdateCheckService
{
    private const string GitHubOwner = "MuhannadYT";
    private const string GitHubRepo = "AirMirror";
    // Throttle the auto-check to roughly once a month so we don't spam the GitHub API
    // even for users who connect from their iPhone many times a day.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(30);

    // Single shared HttpClient — creating one per call leaks sockets.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SettingsStore _store;
    private readonly Action<string> _log;
    private int _checkInFlight; // 0 = idle, 1 = a check is currently running

    public UpdateCheckService(SettingsStore store, Action<string> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Fired on the UI thread (via the caller's dispatcher) when a newer release is found
    /// that hasn't already been dismissed. The handler should show the upgrade prompt.
    /// </summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    public string CurrentVersion { get; } = ResolveCurrentVersion();

    /// <summary>
    /// Call this when an AirPlay client connects. Returns immediately if a check isn't due,
    /// or if one is already running. Otherwise issues the GitHub query on a background task
    /// and raises <see cref="UpdateAvailable"/> when a newer release is found.
    /// </summary>
    public void TriggerCheckIfDue()
    {
        var settings = _store.Load();

        if (settings.LastUpdateCheckUtc is { } last && (DateTime.UtcNow - last) < CheckInterval)
        {
            return;
        }

        // Ensure only one check at a time even if connections fire rapidly back to back.
        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(settings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Update checks must never crash the app. Just log and move on; we'll try
                // again next time the user-defined interval elapses.
                _log($"Update check failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _checkInFlight, 0);
            }
        });
    }

    private async Task CheckAsync(AppSettings settings)
    {
        var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);

        // Always update the timestamp on a successful HTTP round-trip (even 404 / rate-limited)
        // so we don't hammer GitHub when something's off — we'll retry in a week.
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        _store.Save(settings);

        if (!resp.IsSuccessStatusCode)
        {
            _log($"Update check: GitHub returned {(int)resp.StatusCode}.");
            return;
        }

        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // /releases/latest already excludes prereleases & drafts, but double-check
        // defensively in case GitHub ever changes that behaviour.
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        var html = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        if (!TryParseSemVer(tag, out var latest) || !TryParseSemVer(CurrentVersion, out var current))
        {
            _log($"Update check: could not compare versions (current={CurrentVersion}, latest={tag}).");
            return;
        }

        if (latest <= current)
        {
            return;
        }

        if (string.Equals(settings.LastDismissedUpdateVersion, tag, StringComparison.OrdinalIgnoreCase))
        {
            // Already showed the user this exact version; don't nag again until something newer ships.
            return;
        }

        _log($"Update check: new version {tag} available (current {CurrentVersion}).");
        UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(CurrentVersion, tag!, html!));
    }

    /// <summary>
    /// Persist the user's dismissal so we don't show this same version again.
    /// </summary>
    public void MarkDismissed(string version)
    {
        var settings = _store.Load();
        settings.LastDismissedUpdateVersion = version;
        _store.Save(settings);
    }

    /// <summary>
    /// Forces an immediate update check, bypassing both the weekly throttle and the
    /// previously-dismissed-version filter. Used by the manual "Check for updates" button
    /// in the main window. Returns the upgrade info if a newer version exists, or null if
    /// the user is on the latest version (or the check failed for any reason).
    /// </summary>
    public async Task<UpdateAvailableEventArgs?> CheckForUpdatesNowAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);

            var settings = _store.Load();
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _store.Save(settings);

            if (!resp.IsSuccessStatusCode)
            {
                _log($"Manual update check: GitHub returned {(int)resp.StatusCode}.");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var html = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(html)) return null;
            if (!TryParseSemVer(tag, out var latest) || !TryParseSemVer(CurrentVersion, out var current)) return null;
            if (latest <= current) return null;

            return new UpdateAvailableEventArgs(CurrentVersion, tag!, html!);
        }
        catch (Exception ex)
        {
            _log($"Manual update check failed: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API requires a User-Agent and recommends an explicit Accept header.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirMirror", ResolveCurrentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string ResolveCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Trim "+commit-sha" suffix sometimes added by SourceLink.
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Parses a version that may be prefixed with "v" (e.g. "v1.2.3" or "1.2.3"). Returns false
    /// if it isn't a recognisable Major.Minor[.Build[.Revision]] form.
    /// </summary>
    private static bool TryParseSemVer(string raw, out Version version)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }
        // Strip any "-pre.1" / "+meta" suffix before handing to System.Version which only
        // understands plain numeric forms.
        var dash = trimmed.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }
        return Version.TryParse(trimmed, out version!);
    }
}

public sealed record UpdateAvailableEventArgs(string CurrentVersion, string NewVersion, string ReleaseUrl);

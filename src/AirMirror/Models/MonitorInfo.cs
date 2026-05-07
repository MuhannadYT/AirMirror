namespace AirMirror.Models;

public sealed record MonitorInfo(
    string DeviceName,
    string DisplayName,
    int Width,
    int Height,
    int RefreshRate,
    bool IsPrimary)
{
    public string ResolutionText => $"{Width} x {Height} @ {RefreshRate} Hz";
}

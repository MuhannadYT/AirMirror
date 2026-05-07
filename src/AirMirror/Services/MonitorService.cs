using System.Runtime.InteropServices;
using AirMirror.Models;

namespace AirMirror.Services;

public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var refresh = GetRefreshRate(screen.DeviceName);
            var name = screen.Primary ? "Primary display" : screen.DeviceName.TrimStart('\\', '.');
            monitors.Add(new MonitorInfo(
                screen.DeviceName,
                name,
                screen.Bounds.Width,
                screen.Bounds.Height,
                refresh <= 1 ? 60 : refresh,
                screen.Primary));
        }

        return monitors;
    }

    public static MonitorInfo GetPrimaryMonitor()
    {
        return GetMonitors().FirstOrDefault(m => m.IsPrimary)
               ?? GetMonitors().FirstOrDefault()
               ?? new MonitorInfo("\\\\.\\DISPLAY1", "Primary display", 1920, 1080, 60, true);
    }

    private static int GetRefreshRate(string deviceName)
    {
        var mode = new DevMode();
        mode.dmSize = (short)Marshal.SizeOf<DevMode>();
        return EnumDisplaySettings(deviceName, -1, ref mode) ? mode.dmDisplayFrequency : 60;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}

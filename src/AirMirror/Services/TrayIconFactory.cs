using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AirMirror.Services;

public static class TrayIconFactory
{
    public static Icon Create()
    {
        // Prefer the embedded WPF Resource icon so the tray matches the EXE / window icon exactly.
        try
        {
            var uri = new System.Uri("pack://application:,,,/Assets/AirMirror.ico", System.UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch { }

        // Fallback: pull the icon embedded in the EXE.
        try
        {
            var exePath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(exePath))
            {
                var fromExe = Icon.ExtractAssociatedIcon(exePath);
                if (fromExe is not null)
                {
                    return fromExe;
                }
            }
        }
        catch { }

        // Final fallback: SystemIcons so we never crash if assets are missing.
        return (Icon)SystemIcons.Application.Clone();
    }
}

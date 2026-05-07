using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace AirMirror.Services;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AirMirror";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath)) return;
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // best-effort; ignore registry failures
        }
    }

    private static string GetExecutablePath()
    {
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entry))
        {
            var exe = Path.ChangeExtension(entry, ".exe");
            if (File.Exists(exe)) return exe;
        }
        return Environment.ProcessPath ?? string.Empty;
    }
}

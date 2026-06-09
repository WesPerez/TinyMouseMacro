using Microsoft.Win32;

namespace TinyMouseMacro;

internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TinyMouseMacro";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.SetValue(ValueName, Application.ExecutablePath);
                return key is not null;
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return key is not null;
            }
        }
        catch
        {
            return false;
        }
    }
}

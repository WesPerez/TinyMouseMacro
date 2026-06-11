using Microsoft.Win32;

namespace TinyMouseMacro;

internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TinyMouseMacro";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath)) return false;

                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(ValueName, """" + processPath + """");
                return true;
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}
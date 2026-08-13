using Microsoft.Win32;
using System.IO;

namespace CodexUsageWidget;

public static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsageWidget";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            if (enabled)
            {
                var launcher = Path.Combine(AppContext.BaseDirectory, "LaunchWidget.vbs");
                if (File.Exists(launcher))
                {
                    var wscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe");
                    key.SetValue(ValueName, $"\"{wscript}\" \"{launcher}\"");
                }
                else
                {
                    var executable = Path.Combine(AppContext.BaseDirectory, "CodexUsageWidget.exe");
                    key.SetValue(ValueName, $"\"{executable}\"");
                }
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }
}

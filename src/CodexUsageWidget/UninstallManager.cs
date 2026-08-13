using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CodexUsageWidget;

internal static class UninstallManager
{
    private const string ProductName = "Codex 用量悬浮插件";
    private const string ProductKeyName = "CodexUsageWidget";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexUsageWidget";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRequested(string[] args) => args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var silent = args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        var expectedDirectory = GetInstallDirectory();
        var executable = Environment.ProcessPath ?? string.Empty;
        var currentDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
        if (!PathsEqual(currentDirectory, expectedDirectory))
        {
            if (!silent) System.Windows.MessageBox.Show("该程序不是已安装版本，无法执行卸载。", ProductName);
            return 1;
        }

        if (!silent && System.Windows.MessageBox.Show(
                "确定要卸载 Codex 用量悬浮插件吗？\n\n用户设置和诊断日志会保留。",
                ProductName,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return 2;

        StopOtherInstances(executable);
        DeleteShortcuts();
        try
        {
            using var startup = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true);
            startup.DeleteValue(ProductKeyName, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }

        ScheduleRemoval(expectedDirectory);
        if (!silent) System.Windows.MessageBox.Show("卸载已开始，程序文件将在退出后清理。", ProductName);
        return 0;
    }

    private static void StopOtherInstances(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (process.Id == Environment.ProcessId || !PathsEqual(process.MainModule?.FileName ?? string.Empty, executable)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }

    private static void DeleteShortcuts()
    {
        TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName));
        TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"));
    }

    private static void ScheduleRemoval(string installDirectory)
    {
        var batch = Path.Combine(Path.GetTempPath(), "CodexUsageWidget-Uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(batch,
            "@echo off\r\n" +
            "ping 127.0.0.1 -n 3 > nul\r\n" +
            $"rmdir /s /q \"{installDirectory}\"\r\n" +
            "del /q \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", batch }
        });
    }

    private static string GetInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductKeyName);

    private static bool PathsEqual(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

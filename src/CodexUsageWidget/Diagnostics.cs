using System.Text;
using System.IO;

namespace CodexUsageWidget;

internal static class Diagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageWidget",
        "error.log");

    public static void Initialize()
    {
        System.Windows.Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) Write(exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception);
    }

    private static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var text = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}]")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(LogPath, text);
        }
        catch { }
    }
}

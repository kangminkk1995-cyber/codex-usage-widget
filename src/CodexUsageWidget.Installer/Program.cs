using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexUsageWidget.Installer;

internal static class Program
{
    private const string ProductName = "Codex 用量悬浮插件";
    private const string AppFileName = "CodexUsageWidget.exe";
    private const string HelperFileName = "CodexAppServer.exe";
    private const string LicenseFileName = "OPENAI_CODEX_LICENSE.txt";
    private const string PayloadResourceName = "CodexUsageWidget.Payload";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexUsageWidget";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var silent = args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        try { return Install(silent); }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show($"安装失败：\n\n{ex.Message}", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install(bool silent)
    {
        if (!silent)
        {
            var answer = MessageBox.Show(
                "将为当前 Windows 用户安装 Codex 用量悬浮插件。\n\n" +
                "• 无需管理员权限\n• 自动匹配本机 Codex 版本\n• 创建桌面和开始菜单快捷方式\n• 安装完成后立即启动并实时刷新额度\n\n继续安装吗？",
                ProductName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return 2;
        }

        var installDirectory = GetInstallDirectory();
        Directory.CreateDirectory(installDirectory);
        var codexSource = FindInstalledCodexExecutable(installDirectory)
            ?? throw new FileNotFoundException("没有找到可用的 Codex。请先安装并登录 Codex 桌面版或 Codex CLI，然后重新运行安装包。");

        StopRunningWidget();
        InstallAndValidateHelper(codexSource, installDirectory);
        ExtractPayload(installDirectory);
        DeleteLegacyInstaller(installDirectory);
        CreateLauncher(installDirectory);
        CreateShortcuts(installDirectory);
        RegisterUninstaller(installDirectory);

        Process.Start(new ProcessStartInfo(GetWscriptPath())
        {
            UseShellExecute = true,
            ArgumentList = { Path.Combine(installDirectory, "LaunchWidget.vbs") }
        });
        if (!silent)
        {
            MessageBox.Show(
                "安装完成。插件已启动，并会使用当前 Windows 用户的 Codex 登录状态实时刷新额度。",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        return 0;
    }

    private static void ExtractPayload(string installDirectory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("安装包缺少程序数据。");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.FullName != entry.Name)
                throw new InvalidDataException("安装包包含无效文件路径。");
            ExtractRequiredEntry(archive, entry.Name, Path.Combine(installDirectory, entry.Name));
        }
        if (!File.Exists(Path.Combine(installDirectory, AppFileName)) ||
            !File.Exists(Path.Combine(installDirectory, "CodexUsageWidget.runtimeconfig.json")) ||
            !File.Exists(Path.Combine(installDirectory, LicenseFileName)))
            throw new InvalidDataException("安装包程序文件不完整。");
    }

    private static void DeleteLegacyInstaller(string installDirectory)
    {
        try
        {
            var legacy = Path.Combine(installDirectory, "Uninstall.exe");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CreateLauncher(string installDirectory)
    {
        var launcher = Path.Combine(installDirectory, "LaunchWidget.vbs");
        var script = "Set fso = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
                     "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                     "base = fso.GetParentFolderName(WScript.ScriptFullName)\r\n" +
                     "runtime = base & \"\\runtime\"\r\n" +
                     "If fso.FileExists(runtime & \"\\dotnet.exe\") Then\r\n" +
                     "  shell.Environment(\"PROCESS\")(\"DOTNET_ROOT\") = runtime\r\n" +
                     "  shell.Environment(\"PROCESS\")(\"DOTNET_ROOT_X64\") = runtime\r\n" +
                     "End If\r\n" +
                     "cmd = Chr(34) & base & \"\\CodexUsageWidget.exe\" & Chr(34)\r\n" +
                     "For Each arg In WScript.Arguments\r\n" +
                     "  cmd = cmd & \" \" & Chr(34) & Replace(arg, Chr(34), Chr(34) & Chr(34)) & Chr(34)\r\n" +
                     "Next\r\n" +
                     "shell.Run cmd, 0, False\r\n";
        File.WriteAllText(launcher, script, System.Text.Encoding.ASCII);
    }

    private static void InstallAndValidateHelper(string source, string installDirectory)
    {
        var destination = Path.Combine(installDirectory, HelperFileName);
        var temporary = destination + ".new";
        File.Copy(source, temporary, true);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(temporary, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("无法验证本机 Codex。");
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("验证本机 Codex 超时。");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("找到的 Codex 无法启动 app-server，请更新 Codex 后重试。");
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string? FindInstalledCodexExecutable(string installDirectory)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in directories.Where(directory => !IsWindowsApps(directory)))
        {
            var executable = SafeCombine(directory, "codex.exe");
            if (executable is not null && File.Exists(executable)) return executable;
        }

        foreach (var directory in directories)
        {
            var wrapper = SafeCombine(directory, "codex.cmd");
            if (wrapper is null || !File.Exists(wrapper)) continue;
            var native = FindNpmNativeExecutable(directory);
            if (native is not null) return native;
        }

        foreach (var directory in directories.Where(IsWindowsApps))
        {
            var executable = SafeCombine(directory, "codex.exe");
            if (executable is not null && File.Exists(executable)) return executable;
        }

        var packagedDesktopExecutable = FindPackagedDesktopExecutable();
        if (packagedDesktopExecutable is not null) return packagedDesktopExecutable;

        var existingHelper = Path.Combine(installDirectory, HelperFileName);
        return File.Exists(existingHelper) ? existingHelper : null;
    }

    private static string? FindPackagedDesktopExecutable()
    {
        const string packageRegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(packageRegistryPath, false);
            var packageName = root?.GetSubKeyNames()
                .Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (packageName is null) return null;
            using var package = root!.OpenSubKey(packageName, false);
            var packageRoot = package?.GetValue("PackageRootFolder") as string;
            if (string.IsNullOrWhiteSpace(packageRoot)) return null;
            var executable = Path.Combine(packageRoot, "app", "resources", "codex.exe");
            return File.Exists(executable) ? executable : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static string? FindNpmNativeExecutable(string directory)
    {
        var candidates = new[]
        {
            Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
            Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe")
        };
        foreach (var relative in candidates)
        {
            var executable = SafeCombine(directory, relative);
            if (executable is not null && File.Exists(executable)) return executable;
        }
        return null;
    }

    private static bool IsWindowsApps(string directory) => directory.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);

    private static string? SafeCombine(string directory, string fileName)
    {
        try { return Path.Combine(directory.Trim('"'), fileName); }
        catch (ArgumentException) { return null; }
    }

    private static void ExtractRequiredEntry(ZipArchive archive, string entryName, string destination)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"安装包缺少 {entryName}。");
        var temporary = destination + ".new";
        entry.ExtractToFile(temporary, true);
        File.Move(temporary, destination, true);
    }

    private static void CreateShortcuts(string installDirectory)
    {
        var app = Path.Combine(installDirectory, AppFileName);
        var icon = Path.Combine(installDirectory, "CodexUsageWidget.ico");
        var launcher = Path.Combine(installDirectory, "LaunchWidget.vbs");
        var target = GetWscriptPath();
        var startMenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
        Directory.CreateDirectory(startMenuDirectory);
        ShellLink.Create(Path.Combine(startMenuDirectory, ProductName + ".lnk"), target, installDirectory, "显示 Codex 实时剩余额度", $"\"{launcher}\"", icon);
        ShellLink.Create(Path.Combine(startMenuDirectory, "卸载.lnk"), target, installDirectory, "卸载 Codex 用量悬浮插件", $"\"{launcher}\" --uninstall", icon);
        ShellLink.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"), target, installDirectory, "显示 Codex 实时剩余额度", $"\"{launcher}\"", icon);
    }

    private static void RegisterUninstaller(string installDirectory)
    {
        var app = Path.Combine(installDirectory, AppFileName);
        var launcher = Path.Combine(installDirectory, "LaunchWidget.vbs");
        var uninstallCommand = $"\"{GetWscriptPath()}\" \"{launcher}\" --uninstall";
        var quietUninstallCommand = uninstallCommand + " --silent";
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, true);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", "1.1.0");
        key.SetValue("Publisher", "Codex Usage Widget");
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("DisplayIcon", app);
        key.SetValue("UninstallString", uninstallCommand);
        key.SetValue("QuietUninstallString", quietUninstallCommand);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void StopRunningWidget()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppFileName)))
        {
            try
            {
                if (process.CloseMainWindow() && process.WaitForExit(3000)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }

    private static string GetInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "CodexUsageWidget");

    private static string GetWscriptPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe");
}

internal static class ShellLink
{
    public static void Create(string shortcutPath, string targetPath, string workingDirectory, string description, string? arguments = null, string? iconPath = null)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        link.SetPath(targetPath);
        link.SetWorkingDirectory(workingDirectory);
        link.SetDescription(description);
        if (!string.IsNullOrWhiteSpace(arguments)) link.SetArguments(arguments);
        if (!string.IsNullOrWhiteSpace(iconPath)) link.SetIconLocation(iconPath, 0);
        ((IPersistFile)link).Save(shortcutPath, true);
        Marshal.FinalReleaseComObject(link);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageWidget.Core;

public interface ILiveUsageSource : IAsyncDisposable
{
    event EventHandler? RateLimitsUpdated;
    Task<UsageSnapshot> QueryAsync(CancellationToken cancellationToken = default);
}

public interface IJsonLineConnection : IAsyncDisposable
{
    event Action<string>? LineReceived;
    event Action<Exception?>? Closed;
    int? ProcessId { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
}

public sealed record AppServerLaunchInfo(string FileName, IReadOnlyList<string> Arguments);

public static class CodexExecutableLocator
{
    public const string BundledHelperFileName = "CodexAppServer.exe";

    public static AppServerLaunchInfo? FindAppServer()
        => FindAppServer(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));

    public static AppServerLaunchInfo? FindAppServer(string appBaseDirectory, string? pathEnvironment)
    {
        var bundledHelper = SafeCombine(appBaseDirectory, BundledHelperFileName);
        if (bundledHelper is not null && File.Exists(bundledHelper))
            return new AppServerLaunchInfo(bundledHelper, ["app-server", "--stdio"]);

        var directories = (pathEnvironment ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in directories.Where(directory => !IsWindowsApps(directory)))
        {
            var candidate = SafeCombine(directory, "codex.exe");
            if (candidate is not null && File.Exists(candidate)) return new AppServerLaunchInfo(candidate, ["app-server", "--stdio"]);
        }

        foreach (var directory in directories)
        {
            var candidate = SafeCombine(directory, "codex.cmd");
            if (candidate is null || !File.Exists(candidate)) continue;
            var nativeExecutable = FindNativeExecutableBesideWrapper(directory);
            if (nativeExecutable is not null) return new AppServerLaunchInfo(nativeExecutable, ["app-server", "--stdio"]);
            var command = $"\"\"{candidate}\" app-server --stdio\"";
            return new AppServerLaunchInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/s", "/c", command]);
        }

        foreach (var directory in directories.Where(IsWindowsApps))
        {
            var candidate = SafeCombine(directory, "codex.exe");
            if (candidate is not null && File.Exists(candidate)) return new AppServerLaunchInfo(candidate, ["app-server", "--stdio"]);
        }

        return null;
    }

    private static bool IsWindowsApps(string directory) =>
        directory.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) ||
        directory.EndsWith("\\WindowsApps", StringComparison.OrdinalIgnoreCase);

    private static string? FindNativeExecutableBesideWrapper(string directory)
    {
        var relativeCandidates = Environment.Is64BitOperatingSystem
            ? new[]
            {
                Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
                Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe")
            }
            : Array.Empty<string>();
        foreach (var relative in relativeCandidates)
        {
            var candidate = SafeCombine(directory, relative);
            if (candidate is not null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? SafeCombine(string directory, string fileName)
    {
        try { return Path.Combine(directory.Trim('"'), fileName); }
        catch (ArgumentException) { return null; }
    }
}

public sealed class ProcessJsonLineConnection : IJsonLineConnection
{
    private readonly AppServerLaunchInfo _launchInfo;
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _closedRaised;

    public ProcessJsonLineConnection(AppServerLaunchInfo launchInfo) => _launchInfo = launchInfo;

    public event Action<string>? LineReceived;
    public event Action<Exception?>? Closed;
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is not null) return Task.CompletedTask;

        var startInfo = new ProcessStartInfo
        {
            FileName = _launchInfo.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in _launchInfo.Arguments) startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => RaiseClosed(null);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Codex app-server");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        _process = process;
        _stdoutTask = ReadStdoutAsync(process);
        _stderrTask = DrainStderrAsync(process);
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex app-server 尚未启动");
        if (process.HasExited) throw new IOException("Codex app-server 已退出");
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) LineReceived?.Invoke(line);
            }
            RaiseClosed(process.HasExited && process.ExitCode != 0 ? new IOException($"Codex app-server 退出，代码 {process.ExitCode}") : null);
        }
        catch (Exception ex) { RaiseClosed(ex); }
    }

    private static async Task DrainStderrAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void RaiseClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception) when (process.HasExited) { }
        catch (TimeoutException) { }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
            if (_stdoutTask is not null) await IgnoreFailure(_stdoutTask).ConfigureAwait(false);
            if (_stderrTask is not null) await IgnoreFailure(_stderrTask).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }
}

public sealed class CodexAppServerClient : ILiveUsageSource
{
    private readonly Func<IJsonLineConnection> _connectionFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();
    private IJsonLineConnection? _connection;
    private long _requestId;
    private bool _initialized;
    private bool _disposed;

    public CodexAppServerClient(TimeSpan? requestTimeout = null)
        : this(CreateDefaultConnection, requestTimeout) { }

    public CodexAppServerClient(Func<IJsonLineConnection> connectionFactory, TimeSpan? requestTimeout = null)
    {
        _connectionFactory = connectionFactory;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    }

    public event EventHandler? RateLimitsUpdated;
    public int? OwnedProcessId => _connection?.ProcessId;

    public async Task<UsageSnapshot> QueryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                await EnsureInitializedAsync(timeout.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _requestId);
                var response = await SendRequestAsync(id, "account/rateLimits/read", null, timeout.Token).ConfigureAwait(false);
                var retrievedAt = DateTimeOffset.Now;
                if (!CodexAppServerParser.TryParseRateLimitsResponse(response, id, retrievedAt, out var snapshot) || snapshot is null)
                    throw new InvalidDataException("Codex app-server 返回了无法识别的额度数据");
                return snapshot;
            }
            catch
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _queryGate.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            var connection = _connectionFactory();
            connection.LineReceived += OnLineReceived;
            connection.Closed += OnConnectionClosed;
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            _connection = connection;
            _initialized = false;
        }
        if (_initialized) return;

        var id = Interlocked.Increment(ref _requestId);
        var parameters = new
        {
            clientInfo = new { name = "codex-usage-widget", title = "Codex Usage Widget", version = "1.0.0" },
            capabilities = (object?)null
        };
        var response = await SendRequestAsync(id, "initialize", parameters, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt64() != id ||
            !document.RootElement.TryGetProperty("result", out _))
            throw new InvalidDataException("Codex app-server 初始化失败");
        _initialized = true;
    }

    private async Task<string> SendRequestAsync(long id, string method, object? parameters, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new IOException("Codex app-server 连接不可用");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("请求编号重复");
        try
        {
            var payload = parameters is null
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(new { id, method, @params = parameters });
            await connection.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void OnLineReceived(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
            {
                if (_pending.TryGetValue(id, out var completion)) completion.TrySetResult(line);
                return;
            }
            if (root.TryGetProperty("method", out var method) && method.GetString() == "account/rateLimits/updated")
                RateLimitsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonException) { }
    }

    private void OnConnectionClosed(Exception? error)
    {
        var failure = error ?? new IOException("Codex app-server 连接已关闭");
        foreach (var completion in _pending.Values) completion.TrySetException(failure);
    }

    private async Task ResetConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        _initialized = false;
        if (connection is null) return;
        connection.LineReceived -= OnLineReceived;
        connection.Closed -= OnConnectionClosed;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static IJsonLineConnection CreateDefaultConnection()
    {
        var launch = CodexExecutableLocator.FindAppServer() ?? throw new FileNotFoundException("未找到 Codex CLI");
        return new ProcessJsonLineConnection(launch);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _queryGate.WaitAsync().ConfigureAwait(false);
        try { await ResetConnectionAsync().ConfigureAwait(false); }
        finally
        {
            _queryGate.Release();
            _queryGate.Dispose();
        }
    }
}

using CodexUsageWidget.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

var tests = new (string Name, Action Run)[]
{
    ("解析主额度", ParsePrimary),
    ("解析双额度和积分", ParseMultiple),
    ("忽略损坏及缺失字段", IgnoreInvalid),
    ("扫描器选取事件时间最新记录", ScannerChoosesLatest),
    ("百分比边界", PercentageBounds),
    ("跨日与过期倒计时", CountdownFormatting),
    ("只读界面属性使用单向绑定", ReadOnlyBindingsAreOneWay),
    ("紧凑额度优先级", CompactQuotaSelection),
    ("展开位置保持在工作区", ExpandedPlacementStaysVisible),
    ("悬停收起意图可取消", HoverIntentCanBeCancelled),
    ("解析 app-server 完整额度", ParseAppServerRateLimits),
    ("忽略损坏响应和未知通知", IgnoreInvalidAppServerMessages),
    ("app-server 初始化和请求编号匹配", AppServerHandshakeAndRequestMatching),
    ("app-server 请求超时", AppServerRequestTimeout),
    ("app-server 断线后重连", AppServerReconnectsAfterFailure),
    ("并发刷新合并", ConcurrentRefreshesAreCoalesced),
    ("实时失败自动回退日志", LiveFailureFallsBackToLog),
    ("日志回退后恢复实时", LiveRecoversAfterLogFallback),
    ("额度通知去抖", RefreshNotificationDebounce),
    ("优先使用安装包内置 helper", BundledHelperIsPreferred),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed == 0 && args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        LiveAppServerProbe();
        Console.WriteLine("PASS 真实 app-server 查询及子进程清理");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL 真实 app-server 查询及子进程清理: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ParsePrimary()
{
    const string line = "{\"timestamp\":\"2026-08-13T03:45:40Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":23.5,\"window_minutes\":10080,\"resets_at\":1787197494},\"secondary\":null,\"credits\":{\"has_credits\":false,\"unlimited\":false,\"balance\":\"0\"}}}}";
    Assert(CodexUsageParser.TryParseLine(line, "sample.jsonl", out var result), "应成功解析");
    Assert(result!.Windows.Count == 1, "应有一个额度窗口");
    Assert(Math.Abs(result.Windows[0].RemainingPercent - 76.5) < 0.001, "剩余额度错误");
    Assert(result.Windows[0].WindowMinutes == 10080, "周期错误");
    Assert(result.Windows[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1787197494), "重置时间错误");
}

static void ParseMultiple()
{
    const string line = "{\"timestamp\":\"2026-08-13T03:45:40Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":10,\"window_minutes\":300,\"resets_at\":1787197494},\"secondary\":{\"used_percent\":50,\"window_minutes\":10080,\"resets_at\":1787197494},\"individual_limit\":{\"used_percent\":20,\"window_minutes\":60,\"resets_at\":1787197494},\"credits\":{\"has_credits\":true,\"unlimited\":false,\"balance\":\"12.50\"}}}}";
    Assert(CodexUsageParser.TryParseLine(line, "sample.jsonl", out var result), "应成功解析");
    Assert(result!.Windows.Count == 3, "应解析全部额度窗口");
    Assert(result.Credits?.Balance == 12.50m, "积分余额错误");
}

static void IgnoreInvalid()
{
    Assert(!CodexUsageParser.TryParseLine("{bad json", "bad.jsonl", out _), "损坏 JSON 不应成功");
    Assert(!CodexUsageParser.TryParseLine("{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}}", "missing.jsonl", out _), "缺失 rate_limits 不应成功");
}

static void ScannerChoosesLatest()
{
    var directory = Path.Combine(Path.GetTempPath(), "CodexUsageWidgetTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllLines(Path.Combine(directory, "newer-file.jsonl"), [Line("2026-08-10T01:00:00Z", 70), "{incomplete"]);
        File.WriteAllText(Path.Combine(directory, "older-file.jsonl"), Line("2026-08-12T01:00:00Z", 20));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "newer-file.jsonl"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "older-file.jsonl"), DateTime.UtcNow.AddDays(-2));
        var result = new CodexUsageScanner([directory]).FindLatest();
        Assert(result is not null && Math.Abs(result.Windows[0].UsedPercent - 20) < 0.001, "必须按事件时间而非文件时间选取");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void PercentageBounds()
{
    Assert(new QuotaWindow("x", "x", -5, null, null).RemainingPercent == 100, "上界应限制为 100");
    Assert(new QuotaWindow("x", "x", 120, null, null).RemainingPercent == 0, "下界应限制为 0");
}

static void CountdownFormatting()
{
    var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(8));
    Assert(UsageTimeFormatter.FormatCountdown(now.AddDays(2).AddHours(3), now) == "2天 3小时后重置", "跨日倒计时错误");
    Assert(UsageTimeFormatter.FormatCountdown(now.AddMinutes(-1), now) == "等待新周期数据", "过期状态错误");
    Assert(UsageTimeFormatter.FormatCountdown(null, now) == "重置时间未知", "缺失时间状态错误");
}

static void ReadOnlyBindingsAreOneWay()
{
    var xamlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CodexUsageWidget", "MainWindow.xaml"));
    var document = XDocument.Load(xamlPath);
    var bindings = document.Descendants()
        .SelectMany(element => element.Attributes())
        .Select(attribute => attribute.Value)
        .Where(value => value.StartsWith("{Binding ", StringComparison.Ordinal))
        .ToArray();
    Assert(bindings.All(value => value.Contains("Mode=OneWay", StringComparison.Ordinal)), "所有界面数据绑定都必须显式使用 OneWay");
    var textAttributes = document.Descendants()
        .SelectMany(element => element.Attributes())
        .Where(attribute => attribute.Name.LocalName == "Text")
        .Select(attribute => attribute.Value)
        .ToArray();
    Assert(textAttributes.Any(value => value.Contains("RemainingText") && value.Contains("Mode=OneWay")), "RemainingText 必须显式使用 OneWay 绑定");
    var valueAttributes = document.Descendants()
        .SelectMany(element => element.Attributes())
        .Where(attribute => attribute.Name.LocalName == "Value")
        .Select(attribute => attribute.Value)
        .ToArray();
    Assert(valueAttributes.Any(value => value.Contains("RemainingPercent") && value.Contains("Mode=OneWay")), "RemainingPercent 必须显式使用 OneWay 绑定");
    Assert(valueAttributes.Any(value => value.Contains("CompactRemainingPercent") && value.Contains("Mode=OneWay")), "紧凑进度必须显式使用 OneWay 绑定");
    Assert(textAttributes.Any(value => value.Contains("CompactRemainingText") && value.Contains("Mode=OneWay")), "紧凑额度必须显式使用 OneWay 绑定");
    Assert(textAttributes.Any(value => value.Contains("CompactRemainingSuffix") && value.Contains("Mode=OneWay")), "紧凑额度后缀必须显式使用 OneWay 绑定");
}

static void CompactQuotaSelection()
{
    var secondary = new QuotaWindow("secondary", "短周期", 20, 300, null);
    var primary = new QuotaWindow("primary", "主额度", 35, 10080, null);
    Assert(ReferenceEquals(QuotaSelection.SelectCompact([secondary, primary]), primary), "应优先选择 primary");
    Assert(ReferenceEquals(QuotaSelection.SelectCompact([secondary]), secondary), "没有 primary 时应选择首项");
    Assert(QuotaSelection.SelectCompact([]) is null, "无额度时应返回 null");
}

static void ExpandedPlacementStaysVisible()
{
    var area = new WidgetRect(100, 50, 1200, 800);
    var rightBottom = new WidgetRect(1090, 750, 204, 76);
    var placement = WidgetPlacement.CalculateExpandedTarget(rightBottom, 368, 420, area);
    Assert(placement.AnchorRight && placement.AnchorBottom, "右下区域应锚定右边和底边");
    Assert(placement.Rect.X >= area.X && placement.Rect.Y >= area.Y, "展开左上角不得越界");
    Assert(placement.Rect.Right <= area.Right && placement.Rect.Bottom <= area.Bottom, "展开右下角不得越界");
    var restored = WidgetPlacement.CompactFromExpanded(placement.Rect, 204, 76, true, true, area);
    Assert(Math.Abs(restored.Right - placement.Rect.Right) < 0.001, "收起应保持右边缘");
    Assert(Math.Abs(restored.Bottom - placement.Rect.Bottom) < 0.001, "收起应保持底边缘");

    var leftTop = WidgetPlacement.CalculateExpandedTarget(new WidgetRect(110, 60, 204, 76), 368, 420, area);
    Assert(!leftTop.AnchorRight && !leftTop.AnchorBottom, "左上区域应向右下展开");
}

static void HoverIntentCanBeCancelled()
{
    var tracker = new HoverIntentTracker();
    tracker.PointerLeft();
    tracker.PointerEntered();
    Assert(!tracker.ConsumeCollapse(false), "重新进入后必须取消收起");
    tracker.PointerLeft();
    tracker.BeginDrag();
    Assert(!tracker.ConsumeCollapse(false), "拖动期间不得收起");
    tracker.EndDrag();
    tracker.PointerLeft();
    Assert(tracker.ConsumeCollapse(false), "离开且未拖动时应收起");
}

static void ParseAppServerRateLimits()
{
    const string json = """
        {"id":7,"result":{"rateLimits":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":12,"windowDurationMins":10080,"resetsAt":1787197494},"secondary":{"usedPercent":41,"windowDurationMins":300,"resetsAt":1786608000},"credits":{"hasCredits":true,"unlimited":false,"balance":"12.50"},"individualLimit":{"limit":"100","remainingPercent":75,"resetsAt":1787197494,"used":"25"}}}}
        """;
    var retrievedAt = new DateTimeOffset(2026, 8, 13, 14, 0, 0, TimeSpan.FromHours(8));
    Assert(CodexAppServerParser.TryParseRateLimitsResponse(json, 7, retrievedAt, out var snapshot), "应解析实时额度响应");
    Assert(snapshot!.Source == UsageDataSource.LiveAppServer, "数据源必须标记为实时接口");
    Assert(snapshot.LastRetrievedAt == retrievedAt, "校准时间错误");
    Assert(snapshot.Windows.Count == 3, "应解析主额度、短周期和独立额度");
    Assert(snapshot.Windows[0].RemainingPercent == 88, "主额度剩余量错误");
    Assert(snapshot.Windows[2].UsedPercent == 25, "独立额度转换错误");
    Assert(snapshot.Credits?.Balance == 12.50m, "Credits 解析错误");
}

static void IgnoreInvalidAppServerMessages()
{
    Assert(!CodexAppServerParser.TryParseRateLimitsResponse("{bad", 1, DateTimeOffset.Now, out _), "损坏 JSON 不应成功");
    Assert(!CodexAppServerParser.TryParseRateLimitsResponse("{\"id\":2,\"result\":{}}", 1, DateTimeOffset.Now, out _), "错误请求编号不应成功");
    Assert(!CodexAppServerParser.IsRateLimitsUpdatedNotification("{\"method\":\"unknown/notification\"}"), "未知通知不应触发刷新");
    Assert(CodexAppServerParser.IsRateLimitsUpdatedNotification("{\"method\":\"account/rateLimits/updated\",\"params\":{}}"), "额度通知应被识别");
}

static void AppServerHandshakeAndRequestMatching()
{
    var connection = new FakeJsonLineConnection((request, fake) =>
    {
        var id = request.GetProperty("id").GetInt64();
        var method = request.GetProperty("method").GetString();
        if (method == "initialize") fake.Emit($"{{\"id\":{id},\"result\":{{\"userAgent\":\"test\"}}}}");
        else
        {
            fake.Emit("{\"id\":999,\"result\":{}}");
            fake.Emit(LiveResponse(id, 12));
        }
    });
    using var client = new AsyncDisposableScope(new CodexAppServerClient(() => connection, TimeSpan.FromSeconds(1)));
    var snapshot = client.Value.QueryAsync().GetAwaiter().GetResult();
    Assert(snapshot.Windows[0].UsedPercent == 12, "必须等待匹配请求编号的响应");
    Assert(connection.Methods.SequenceEqual(["initialize", "account/rateLimits/read"]), "初始化必须先于额度查询");
}

static void AppServerRequestTimeout()
{
    var connection = new FakeJsonLineConnection((request, fake) =>
    {
        var id = request.GetProperty("id").GetInt64();
        if (request.GetProperty("method").GetString() == "initialize") fake.Emit($"{{\"id\":{id},\"result\":{{}}}}");
    });
    using var client = new AsyncDisposableScope(new CodexAppServerClient(() => connection, TimeSpan.FromMilliseconds(80)));
    var timedOut = false;
    try { client.Value.QueryAsync().GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { timedOut = true; }
    Assert(timedOut, "无响应请求必须超时");
    Assert(connection.Disposed, "超时后必须清理故障连接");
}

static void AppServerReconnectsAfterFailure()
{
    var created = 0;
    IJsonLineConnection Factory()
    {
        var generation = ++created;
        return new FakeJsonLineConnection((request, fake) =>
        {
            var id = request.GetProperty("id").GetInt64();
            var method = request.GetProperty("method").GetString();
            if (method == "initialize") fake.Emit($"{{\"id\":{id},\"result\":{{}}}}");
            else if (generation == 1) fake.Fail(new IOException("connection lost"));
            else fake.Emit(LiveResponse(id, 22));
        });
    }

    using var client = new AsyncDisposableScope(new CodexAppServerClient(Factory, TimeSpan.FromSeconds(1)));
    try { client.Value.QueryAsync().GetAwaiter().GetResult(); } catch (IOException) { }
    var recovered = client.Value.QueryAsync().GetAwaiter().GetResult();
    Assert(created == 2, "失败后应创建新连接");
    Assert(recovered.Windows[0].UsedPercent == 22, "重连后应返回实时数据");
}

static void ConcurrentRefreshesAreCoalesced()
{
    var source = new FakeLiveUsageSource(async () =>
    {
        await Task.Delay(80);
        return Snapshot(UsageDataSource.LiveAppServer, 10);
    });
    var coordinator = new UsageRefreshCoordinator(source, _ => Task.FromResult<UsageSnapshot?>(null));
    var first = coordinator.RefreshAsync();
    var second = coordinator.RefreshAsync();
    Task.WhenAll(first, second).GetAwaiter().GetResult();
    Assert(source.QueryCount == 1, "并发刷新只能发出一次实时查询");
}

static void LiveFailureFallsBackToLog()
{
    var source = new FakeLiveUsageSource(() => throw new IOException("offline"));
    var log = Snapshot(UsageDataSource.LocalLog, 36) with { CapturedAt = DateTimeOffset.Now.AddMinutes(-20) };
    var coordinator = new UsageRefreshCoordinator(source, _ => Task.FromResult<UsageSnapshot?>(log));
    var result = coordinator.RefreshAsync().GetAwaiter().GetResult();
    Assert(result.UsedLogFallback, "实时失败必须进入日志回退");
    Assert(result.Snapshot?.Source == UsageDataSource.LocalLog, "回退数据源标记错误");
    Assert(result.Snapshot?.Windows[0].UsedPercent == 36, "回退日志内容错误");
}

static void LiveRecoversAfterLogFallback()
{
    var attempts = 0;
    var source = new FakeLiveUsageSource(() =>
    {
        attempts++;
        return attempts == 1
            ? Task.FromException<UsageSnapshot>(new IOException("offline"))
            : Task.FromResult(Snapshot(UsageDataSource.LiveAppServer, 18));
    });
    var coordinator = new UsageRefreshCoordinator(
        source,
        _ => Task.FromResult<UsageSnapshot?>(Snapshot(UsageDataSource.LocalLog, 44)));
    var fallback = coordinator.RefreshAsync().GetAwaiter().GetResult();
    var recovered = coordinator.RefreshAsync().GetAwaiter().GetResult();
    Assert(fallback.Snapshot?.Source == UsageDataSource.LocalLog, "首次失败应使用日志");
    Assert(recovered.Snapshot?.Source == UsageDataSource.LiveAppServer, "下次查询成功应恢复实时数据");
    Assert(recovered.Snapshot?.Windows[0].UsedPercent == 18, "恢复后的实时数据错误");
}

static void RefreshNotificationDebounce()
{
    var tracker = new RefreshSignalDebouncer(TimeSpan.FromMilliseconds(500));
    var now = DateTimeOffset.Now;
    tracker.Signal(now);
    tracker.Signal(now.AddMilliseconds(300));
    Assert(!tracker.TryConsume(now.AddMilliseconds(600)), "重复通知应延后刷新时间");
    Assert(tracker.TryConsume(now.AddMilliseconds(801)), "去抖期结束后应刷新一次");
    Assert(!tracker.TryConsume(now.AddMilliseconds(900)), "一次信号只能消费一次");
}

static void BundledHelperIsPreferred()
{
    var directory = Path.Combine(Path.GetTempPath(), "CodexUsageWidgetTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var helper = Path.Combine(directory, CodexExecutableLocator.BundledHelperFileName);
        File.WriteAllBytes(helper, [77, 90]);
        var launch = CodexExecutableLocator.FindAppServer(directory, string.Empty);
        Assert(launch?.FileName == helper, "安装目录中的 helper 必须优先于系统 PATH");
        Assert(launch?.Arguments.SequenceEqual(["app-server", "--stdio"]) == true, "helper 启动参数错误");
    }
    finally { Directory.Delete(directory, true); }
}

static void LiveAppServerProbe()
{
    var client = new CodexAppServerClient(TimeSpan.FromSeconds(10));
    var snapshot = client.QueryAsync().GetAwaiter().GetResult();
    var processId = client.OwnedProcessId;
    Assert(snapshot.Source == UsageDataSource.LiveAppServer && snapshot.Windows.Count > 0, "未获取到真实实时额度");
    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    if (processId is { } id)
    {
        Thread.Sleep(200);
        var stillRunning = true;
        try { stillRunning = !Process.GetProcessById(id).HasExited; }
        catch (ArgumentException) { stillRunning = false; }
        Assert(!stillRunning, "客户端退出后 app-server 子进程仍然存在");
    }
}

static UsageSnapshot Snapshot(UsageDataSource source, double used) => new(
    DateTimeOffset.Now,
    "codex",
    null,
    [new QuotaWindow("primary", "主额度", used, 10080, DateTimeOffset.Now.AddDays(2))],
    null,
    source == UsageDataSource.LiveAppServer ? "Codex app-server" : "sample.jsonl",
    source,
    DateTimeOffset.Now);

static string LiveResponse(long id, int used) => $"{{\"id\":{id},\"result\":{{\"rateLimits\":{{\"limitId\":\"codex\",\"primary\":{{\"usedPercent\":{used},\"windowDurationMins\":10080,\"resetsAt\":1787197494}}}}}}}}";

static string Line(string timestamp, double used) => $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"rate_limits\":{{\"limit_id\":\"codex\",\"primary\":{{\"used_percent\":{used.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"window_minutes\":10080,\"resets_at\":1787197494}}}}}}}}";
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FakeJsonLineConnection : IJsonLineConnection
{
    private readonly Action<JsonElement, FakeJsonLineConnection> _handler;
    public FakeJsonLineConnection(Action<JsonElement, FakeJsonLineConnection> handler) => _handler = handler;
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Closed;
    public int? ProcessId => 12345;
    public List<string> Methods { get; } = [];
    public bool Disposed { get; private set; }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(line);
        Methods.Add(document.RootElement.GetProperty("method").GetString()!);
        _handler(document.RootElement.Clone(), this);
        return Task.CompletedTask;
    }
    public void Emit(string line) => LineReceived?.Invoke(line);
    public void Fail(Exception error) => Closed?.Invoke(error);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

sealed class FakeLiveUsageSource : ILiveUsageSource
{
    private readonly Func<Task<UsageSnapshot>> _query;
    public FakeLiveUsageSource(Func<Task<UsageSnapshot>> query) => _query = query;
    public event EventHandler? RateLimitsUpdated { add { } remove { } }
    public int QueryCount { get; private set; }
    public Task<UsageSnapshot> QueryAsync(CancellationToken cancellationToken = default) { QueryCount++; return _query(); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class AsyncDisposableScope : IDisposable
{
    public AsyncDisposableScope(CodexAppServerClient value) => Value = value;
    public CodexAppServerClient Value { get; }
    public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

namespace CodexUsageWidget.Core;

public sealed record UsageRefreshResult(UsageSnapshot? Snapshot, bool UsedLogFallback, string? LiveError);

public sealed class UsageRefreshCoordinator
{
    private readonly ILiveUsageSource _liveSource;
    private readonly Func<CancellationToken, Task<UsageSnapshot?>> _readLogAsync;
    private readonly object _sync = new();
    private Task<UsageRefreshResult>? _inFlight;

    public UsageRefreshCoordinator(ILiveUsageSource liveSource, Func<CancellationToken, Task<UsageSnapshot?>> readLogAsync)
    {
        _liveSource = liveSource;
        _readLogAsync = readLogAsync;
    }

    public Task<UsageRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task<UsageRefreshResult> task;
        lock (_sync)
        {
            if (_inFlight is null || _inFlight.IsCompleted) _inFlight = RefreshCoreAsync();
            task = _inFlight;
        }
        _ = ClearWhenCompleteAsync(task);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<UsageRefreshResult> RefreshCoreAsync()
    {
        try
        {
            var live = await _liveSource.QueryAsync().ConfigureAwait(false);
            return new UsageRefreshResult(live, false, null);
        }
        catch (Exception ex)
        {
            UsageSnapshot? logSnapshot = null;
            try { logSnapshot = await _readLogAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            if (logSnapshot is not null)
            {
                logSnapshot = logSnapshot with
                {
                    Source = UsageDataSource.LocalLog,
                    RetrievedAt = DateTimeOffset.Now
                };
            }
            return new UsageRefreshResult(logSnapshot, true, ex.Message);
        }
    }

    private async Task ClearWhenCompleteAsync(Task<UsageRefreshResult> task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        lock (_sync)
        {
            if (ReferenceEquals(_inFlight, task)) _inFlight = null;
        }
    }
}

public sealed class RefreshSignalDebouncer
{
    private readonly TimeSpan _delay;
    private DateTimeOffset? _dueAt;

    public RefreshSignalDebouncer(TimeSpan delay) => _delay = delay;
    public void Signal(DateTimeOffset now) => _dueAt = now + _delay;
    public void Reset() => _dueAt = null;

    public bool TryConsume(DateTimeOffset now)
    {
        if (_dueAt is null || now < _dueAt) return false;
        _dueAt = null;
        return true;
    }
}

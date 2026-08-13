using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CodexUsageWidget.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CodexUsageWidget;

public sealed class QuotaViewModel : INotifyPropertyChanged
{
    private static readonly Brush Green = Freeze("#43D19E");
    private static readonly Brush Orange = Freeze("#F5AA4B");
    private static readonly Brush Red = Freeze("#F06472");
    private readonly QuotaWindow _quota;

    public QuotaViewModel(QuotaWindow quota)
    {
        _quota = quota;
        Name = BuildName(quota);
    }

    public string Id => _quota.Id;
    public string Name { get; }
    public double RemainingPercent => _quota.RemainingPercent;
    public string RemainingText => RemainingPercent.ToString("0.#", CultureInfo.CurrentCulture);
    public string WindowText => _quota.WindowMinutes is > 0 ? $"额度周期：{FormatDuration(_quota.WindowMinutes.Value)}" : "额度周期：未知";
    public string ResetTimeText => _quota.ResetsAt is { } reset ? $"{reset.LocalDateTime:MM-dd HH:mm} 重置" : "未提供重置时间";
    public string CountdownText => UsageTimeFormatter.FormatCountdown(_quota.ResetsAt, DateTimeOffset.Now);
    public Brush StatusBrush => RemainingPercent <= 15 ? Red : RemainingPercent <= 35 ? Orange : Green;

    public void Tick()
    {
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(ResetTimeText));
    }

    private static string BuildName(QuotaWindow quota)
    {
        var prefix = quota.Id switch
        {
            "primary" => "主额度",
            "secondary" => "短周期额度",
            "individual_limit" => "独立额度",
            _ => quota.DisplayName
        };
        return quota.WindowMinutes is > 0 ? $"{prefix} · {FormatDuration(quota.WindowMinutes.Value)}" : prefix;
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes % 10080 == 0) return $"{minutes / 10080} 周";
        if (minutes % 1440 == 0) return $"{minutes / 1440} 天";
        if (minutes % 60 == 0) return $"{minutes / 60} 小时";
        return $"{minutes} 分钟";
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush Fresh = Freeze("#43D19E");
    private static readonly Brush Stale = Freeze("#F5AA4B");
    private static readonly Brush Offline = Freeze("#718096");
    private UsageSnapshot? _snapshot;
    private string _statusText = "正在连接 Codex 实时额度…";
    private bool _refreshFailed;

    public ObservableCollection<QuotaViewModel> Quotas { get; } = [];
    public QuotaViewModel? CompactQuota => Quotas.FirstOrDefault(quota => quota.Id.Equals("primary", StringComparison.OrdinalIgnoreCase)) ?? Quotas.FirstOrDefault();
    public string CompactRemainingText => CompactQuota?.RemainingText ?? "--";
    public string CompactRemainingSuffix => CompactQuota is null ? string.Empty : "%";
    public double CompactRemainingPercent => CompactQuota?.RemainingPercent ?? 0d;
    public Brush CompactStatusBrush => CompactQuota?.StatusBrush ?? Offline;
    public bool ShowEmptyState => Quotas.Count == 0 && !ShowCredits;
    public bool ShowCredits => _snapshot?.Credits is not null;
    public string CreditsText => _snapshot?.Credits switch
    {
        { Unlimited: true } => "不限量",
        { Balance: { } balance } => balance.ToString("0.##", CultureInfo.CurrentCulture),
        { HasCredits: true } => "可用",
        _ => "—"
    };
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string LastUpdatedText => _snapshot switch
    {
        { Source: UsageDataSource.LiveAppServer } live => $"校准于 {live.LastRetrievedAt.LocalDateTime:HH:mm:ss}",
        { Source: UsageDataSource.LocalLog } log => $"记录于 {log.CapturedAt.LocalDateTime:MM-dd HH:mm:ss}",
        _ => string.Empty
    };
    public Brush FreshnessBrush => _snapshot switch
    {
        _ when _refreshFailed => Stale,
        { Source: UsageDataSource.LiveAppServer } => Fresh,
        { Source: UsageDataSource.LocalLog } => Stale,
        _ => Offline
    };

    public bool Apply(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            if (_snapshot is null) StatusText = "未找到可用记录";
            RaiseAll();
            return false;
        }

        if (_snapshot is not null && snapshot.LastRetrievedAt < _snapshot.LastRetrievedAt) return false;

        var quotaDataChanged = _snapshot is null ||
                               !_snapshot.Windows.SequenceEqual(snapshot.Windows) ||
                               _snapshot.Credits != snapshot.Credits;
        _refreshFailed = false;
        _snapshot = snapshot;
        if (quotaDataChanged)
        {
            Quotas.Clear();
            foreach (var quota in snapshot.Windows) Quotas.Add(new QuotaViewModel(quota));
        }
        RefreshFreshness();
        RaiseAll();
        return quotaDataChanged;
    }

    public void Tick()
    {
        foreach (var quota in Quotas) quota.Tick();
        RefreshFreshness();
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(FreshnessBrush));
        OnPropertyChanged(nameof(CompactQuota));
        OnPropertyChanged(nameof(CompactRemainingText));
        OnPropertyChanged(nameof(CompactRemainingSuffix));
        OnPropertyChanged(nameof(CompactRemainingPercent));
        OnPropertyChanged(nameof(CompactStatusBrush));
    }

    public void SetRefreshError()
    {
        _refreshFailed = true;
        StatusText = _snapshot is null ? "实时接口不可用，且未找到本地记录" : "实时连接异常 · 显示上次数据";
        OnPropertyChanged(nameof(FreshnessBrush));
    }

    private void RefreshFreshness()
    {
        StatusText = _snapshot switch
        {
            _ when _refreshFailed => _snapshot is null ? "实时接口不可用，且未找到本地记录" : "实时连接异常 · 显示上次数据",
            null => "未找到可用记录",
            { Source: UsageDataSource.LocalLog } => "日志回退 · 实时接口暂不可用",
            _ => BuildLiveStatus()
        };
        OnPropertyChanged(nameof(FreshnessBrush));
    }

    private string BuildLiveStatus()
    {
        if (_snapshot is null) return "未找到可用记录";
        var elapsed = Math.Max(0, (int)(DateTimeOffset.Now - _snapshot.LastRetrievedAt).TotalSeconds);
        return elapsed < 5 ? "实时 · 刚刚校准" : $"实时 · {elapsed} 秒前校准";
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowCredits));
        OnPropertyChanged(nameof(CreditsText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(FreshnessBrush));
        OnPropertyChanged(nameof(CompactQuota));
        OnPropertyChanged(nameof(CompactRemainingText));
        OnPropertyChanged(nameof(CompactRemainingSuffix));
        OnPropertyChanged(nameof(CompactRemainingPercent));
        OnPropertyChanged(nameof(CompactStatusBrush));
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}

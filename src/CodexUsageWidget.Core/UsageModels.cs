namespace CodexUsageWidget.Core;

public enum UsageDataSource
{
    LocalLog,
    LiveAppServer
}

public sealed record QuotaWindow(
    string Id,
    string DisplayName,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

public sealed record CreditInfo(bool HasCredits, bool Unlimited, decimal? Balance);

public sealed record UsageSnapshot(
    DateTimeOffset CapturedAt,
    string LimitId,
    string? LimitName,
    IReadOnlyList<QuotaWindow> Windows,
    CreditInfo? Credits,
    string SourceFile,
    UsageDataSource Source = UsageDataSource.LocalLog,
    DateTimeOffset? RetrievedAt = null)
{
    public DateTimeOffset LastRetrievedAt => RetrievedAt ?? CapturedAt;
}

namespace CodexUsageWidget.Core;

public static class UsageTimeFormatter
{
    public static string FormatCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return "重置时间未知";
        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "等待新周期数据";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}天 {remaining.Hours}小时后重置";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}小时 {remaining.Minutes}分后重置";
        return $"{Math.Max(0, remaining.Minutes)}分 {Math.Max(0, remaining.Seconds)}秒后重置";
    }
}

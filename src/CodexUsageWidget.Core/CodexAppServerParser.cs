using System.Globalization;
using System.Text.Json;

namespace CodexUsageWidget.Core;

public static class CodexAppServerParser
{
    public static bool TryParseRateLimitsResponse(
        string json,
        long expectedRequestId,
        DateTimeOffset retrievedAt,
        out UsageSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParseRateLimitsResponse(document.RootElement, expectedRequestId, retrievedAt, out snapshot);
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static bool IsRateLimitsUpdatedNotification(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryString(document.RootElement, "method") == "account/rateLimits/updated";
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryParseRateLimitsResponse(
        JsonElement root,
        long expectedRequestId,
        DateTimeOffset retrievedAt,
        out UsageSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryLong(root, "id", out var responseId) || responseId != expectedRequestId) return false;
        if (!TryObject(root, "result", out var result) || !TryObject(result, "rateLimits", out var limits)) return false;

        var windows = new List<QuotaWindow>(3);
        AddWindow(windows, limits, "primary", "主额度");
        AddWindow(windows, limits, "secondary", "短周期额度");
        AddIndividualLimit(windows, limits);
        var credits = ReadCredits(limits);
        if (windows.Count == 0 && credits is null) return false;

        snapshot = new UsageSnapshot(
            retrievedAt,
            TryString(limits, "limitId") ?? "codex",
            TryString(limits, "limitName"),
            windows,
            credits,
            "Codex app-server",
            UsageDataSource.LiveAppServer,
            retrievedAt);
        return true;
    }

    private static void AddWindow(List<QuotaWindow> windows, JsonElement limits, string propertyName, string fallbackName)
    {
        if (!TryObject(limits, propertyName, out var window)) return;
        var usedPercent = TryDouble(window, "usedPercent");
        if (usedPercent is null) return;
        windows.Add(new QuotaWindow(
            propertyName,
            fallbackName,
            Math.Clamp(usedPercent.Value, 0d, 100d),
            TryInt(window, "windowDurationMins"),
            TryUnixTimestamp(window, "resetsAt")));
    }

    private static void AddIndividualLimit(List<QuotaWindow> windows, JsonElement limits)
    {
        if (!TryObject(limits, "individualLimit", out var individual)) return;
        var remaining = TryDouble(individual, "remainingPercent");
        if (remaining is null) return;
        windows.Add(new QuotaWindow(
            "individual_limit",
            "独立额度",
            Math.Clamp(100d - remaining.Value, 0d, 100d),
            null,
            TryUnixTimestamp(individual, "resetsAt")));
    }

    private static CreditInfo? ReadCredits(JsonElement limits)
    {
        if (!TryObject(limits, "credits", out var credits)) return null;
        var hasCredits = TryBool(credits, "hasCredits") ?? false;
        var unlimited = TryBool(credits, "unlimited") ?? false;
        var balance = TryDecimal(credits, "balance");
        return hasCredits || unlimited || balance is > 0 ? new CreditInfo(hasCredits, unlimited, balance) : null;
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static string? TryString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool TryLong(JsonElement parent, string name, out long result)
    {
        result = 0;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out result);
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static double? TryDouble(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static decimal? TryDecimal(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static int? TryInt(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static bool? TryBool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? TryUnixTimestamp(JsonElement parent, string name) =>
        TryLong(parent, name, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}

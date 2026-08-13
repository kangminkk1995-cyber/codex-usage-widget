using System.Globalization;
using System.Text.Json;

namespace CodexUsageWidget.Core;

public static class CodexUsageParser
{
    public static bool TryParseLine(string line, string sourceFile, out UsageSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryProperty(root, "type", out var envelopeType) || envelopeType.GetString() != "event_msg") return false;
            if (!TryProperty(root, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
            if (!TryProperty(payload, "type", out var payloadType) || payloadType.GetString() != "token_count") return false;
            if (!TryProperty(payload, "rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return false;

            var capturedAt = ReadTimestamp(root, "timestamp") ?? DateTimeOffset.MinValue;
            var limitId = ReadString(limits, "limit_id") ?? "codex";
            var windows = new List<QuotaWindow>(3);
            AddWindow(windows, limits, "primary", "主额度");
            AddWindow(windows, limits, "secondary", "短周期额度");
            AddWindow(windows, limits, "individual_limit", "独立额度");
            var credits = ReadCredits(limits);

            if (windows.Count == 0 && credits is null) return false;
            snapshot = new UsageSnapshot(
                capturedAt,
                limitId,
                ReadString(limits, "limit_name"),
                windows,
                credits,
                sourceFile);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void AddWindow(List<QuotaWindow> windows, JsonElement limits, string propertyName, string fallbackName)
    {
        if (!TryProperty(limits, propertyName, out var value) || value.ValueKind != JsonValueKind.Object) return;
        var used = ReadDouble(value, "used_percent");
        if (used is null) return;

        var name = ReadString(value, "limit_name") ?? ReadString(value, "name") ?? fallbackName;
        windows.Add(new QuotaWindow(
            propertyName,
            name,
            Math.Clamp(used.Value, 0d, 100d),
            ReadInt(value, "window_minutes"),
            ReadUnixTimestamp(value, "resets_at")));
    }

    private static CreditInfo? ReadCredits(JsonElement limits)
    {
        if (!TryProperty(limits, "credits", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var hasCredits = ReadBool(value, "has_credits") ?? false;
        var unlimited = ReadBool(value, "unlimited") ?? false;
        var balance = ReadDecimal(value, "balance");
        return hasCredits || unlimited || balance is > 0 ? new CreditInfo(hasCredits, unlimited, balance) : null;
    }

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static decimal? ReadDecimal(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static bool? ReadBool(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) ? result : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement parent, string name)
    {
        var text = ReadString(parent, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}

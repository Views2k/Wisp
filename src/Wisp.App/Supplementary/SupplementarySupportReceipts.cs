using System.Text.Json;

namespace Wisp.App.Supplementary;

internal static class SupplementarySupportReceipts
{
    internal static SupplementarySupportResult Read(SupplementaryResponse response, DateTimeOffset now)
    {
        if (response.Status != SupplementaryRequestStatus.Success && response.HttpStatus != 429) return new(response.Status);
        try
        {
            using var doc = SupplementaryJson.Parse(response.Body, 2048);
            if (response.HttpStatus == 429)
            {
                var p = SupplementaryJson.Object(doc.RootElement, "schemaVersion", "error");
                SupplementaryJson.Require(SupplementaryJson.Int(p["schemaVersion"]) == 1, "schema");
                // Capacity limits are not a per-installation daily quota. Never display arbitrary server error text.
                var error = p["error"];
                if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code) ||
                    code.ValueKind != JsonValueKind.String || code.GetString() != "support_daily_limit")
                    return new(SupplementaryRequestStatus.Unavailable);
                var e = SupplementaryJson.Object(error, "code", "message", "nextAllowedAt", "retryAfterSeconds");
                var next = NextAllowed(e["nextAllowedAt"], now);
                SupplementaryJson.Require(SupplementaryJson.Int(e["retryAfterSeconds"]) is > 0 and <= 90000, "quota-delay");
                return new(SupplementaryRequestStatus.DailyLimit, NextAllowedAt: next);
            }
            var hasQuota = doc.RootElement.TryGetProperty("nextAllowedAt", out _);
            var fields = hasQuota
                ? SupplementaryJson.Object(doc.RootElement, "schemaVersion", "reference", "nextAllowedAt", "quota")
                : SupplementaryJson.Object(doc.RootElement, "schemaVersion", "reference");
            var reference = SupplementaryJson.String(fields["reference"], 40);
            SupplementaryJson.Require(SupplementaryJson.Int(fields["schemaVersion"]) == 1 &&
                reference.Length >= 6 && reference.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "receipt");
            DateTimeOffset? nextAllowed = null;
            if (hasQuota)
            {
                nextAllowed = NextAllowed(fields["nextAllowedAt"], now);
                var quota = SupplementaryJson.Object(fields["quota"], "policy", "limit");
                SupplementaryJson.Require(SupplementaryJson.String(quota["policy"], 32) == "installation-utc-day" &&
                    SupplementaryJson.Int(quota["limit"]) == 1, "quota");
            }
            return new(SupplementaryRequestStatus.Success, reference, nextAllowed);
        }
        catch (Exception e) when (e is SupplementaryValidationException or JsonException or FormatException or InvalidOperationException)
        { return new(SupplementaryRequestStatus.Unavailable); } // A malformed receipt cannot establish that a message was not saved.
    }

    private static DateTimeOffset NextAllowed(JsonElement element, DateTimeOffset now)
    {
        var next = SupplementaryJson.Utc(element);
        SupplementaryJson.Require(next.TimeOfDay == TimeSpan.Zero && next <= now.AddDays(2), "quota-time");
        return next;
    }
}

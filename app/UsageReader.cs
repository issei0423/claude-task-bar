using System;
using System.IO;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>
/// Reads how much of the 5-hour usage window is used, from the file the
/// statusline writes (default ~/.claude/statusline-last.json):
/// { "rate_limits": { "five_hour": { "used_percentage": 89, "resets_at": 1795766400 } } }
/// WPF-free so CoreTests can check it. "Unknown" is null and never makes the
/// pets flee: a missing, unreadable or broken file means nothing is known.
/// </summary>
public static class UsageReader
{
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "statusline-last.json");

    /// <summary>
    /// The used percentage (0-100) of the 5-hour window at `now`, or null when
    /// it cannot be known. A window whose resets_at has passed counts as 0: the
    /// file is only rewritten when the statusline is drawn, so an old 90% must
    /// not keep the pets running. A resets_at that is missing or not a number
    /// cannot say that, so the percentage is taken as it is.
    /// </summary>
    public static double? Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object ||
                !limits.TryGetProperty("five_hour", out var five) || five.ValueKind != JsonValueKind.Object ||
                !five.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number ||
                !used.TryGetDouble(out var pct) || double.IsNaN(pct) || double.IsInfinity(pct) ||
                pct < 0 || pct > 100)
                return null;

            if (five.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.Number &&
                resets.TryGetDouble(out var at) && !double.IsNaN(at) && at <= now.ToUnixTimeSeconds())
                return 0;
            return pct;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Read the file and parse it. Null when it is missing, locked or cannot be read.</summary>
    public static double? ReadFile(string path, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var rd = new StreamReader(fs))
                text = rd.ReadToEnd();
            return Parse(text, now);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Flee when the usage is known and has reached the threshold (percent).</summary>
    public static bool ShouldFlee(double? used, double threshold) => used.HasValue && used.Value >= threshold;
}

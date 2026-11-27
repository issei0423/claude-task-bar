using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>
/// One running Claude Code process, as it describes itself in
/// ~/.claude/sessions/&lt;pid&gt;.json.
/// </summary>
public sealed class LiveSessionEntry
{
    public int Pid { get; init; }
    public string SessionId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>When the process started, as a Windows FILETIME (UTC). Null when the file did not say.</summary>
    public long? ProcStartFileTime { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>When the status last changed (statusUpdatedAt). Null when the file did not say.</summary>
    public DateTimeOffset? StatusUpdatedAt { get; init; }
}

/// <summary>
/// Reads Claude Code's own list of running sessions (WPF-free, so CoreTests
/// can check it). The files are an internal, undocumented format: every
/// reader here treats a missing field, a wrong type, or half-written text as
/// "skip this one", never as an error.
/// </summary>
public static class LiveSessions
{
    /// <summary>
    /// Parse one file's text. Null unless it has a pid of 1 or more and a
    /// non-empty sessionId.
    /// </summary>
    public static LiveSessionEntry? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("pid", out var pidEl) || pidEl.ValueKind != JsonValueKind.Number ||
                !pidEl.TryGetInt32(out var pid) || pid < 1) return null;

            var sessionId = Str(root, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            long? procStart = null;
            if (root.TryGetProperty("procStart", out var ps))
            {
                if (ps.ValueKind == JsonValueKind.String &&
                    long.TryParse(ps.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var a) && a > 0)
                    procStart = a;
                else if (ps.ValueKind == JsonValueKind.Number && ps.TryGetInt64(out var b) && b > 0)
                    procStart = b;
            }

            var updatedAt = UnixMs(root, "updatedAt");
            var statusUpdatedAt = UnixMs(root, "statusUpdatedAt");

            return new LiveSessionEntry
            {
                Pid = pid,
                SessionId = sessionId,
                Cwd = Str(root, "cwd"),
                Kind = Str(root, "kind"),
                Status = Str(root, "status"),
                ProcStartFileTime = procStart,
                UpdatedAt = updatedAt,
                StatusUpdatedAt = statusUpdatedAt,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A Unix-milliseconds number field, or null (missing, not a number, out of range).</summary>
    private static DateTimeOffset? UnixMs(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    /// <summary>
    /// What the list's status means for a pet: busy / shell -> Running (working),
    /// waiting -> Asking (waiting for the user), idle -> DoneSeen (at rest).
    /// Anything else, or empty, -> null (do not use it). Case is ignored.
    /// </summary>
    public static SessionStatus? MapStatus(string? status) =>
        (status ?? "").Trim().ToLowerInvariant() switch
        {
            "busy" or "shell" => SessionStatus.Running,
            "waiting" => SessionStatus.Asking,
            "idle" => SessionStatus.DoneSeen,
            _ => null,
        };

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Only interactive sessions get a pet (an empty kind counts as interactive).</summary>
    public static bool IsInteractive(LiveSessionEntry e) =>
        string.IsNullOrEmpty(e.Kind) || string.Equals(e.Kind, "interactive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Is the process that is running now the one the file talks about (not a
    /// later process that reused the pid)? No process -> false. No start time
    /// in the file -> true (cannot compare, so a live process counts).
    /// DateTime.MinValue is the "could not look" marker -> true. Otherwise the
    /// two start times must be within 2 seconds.
    /// </summary>
    public static bool SameProcess(long? procStartFileTime, DateTime? actualStart)
    {
        if (actualStart == null) return false;
        if (procStartFileTime == null) return true;
        if (actualStart.Value == DateTime.MinValue) return true;
        long actual;
        try { actual = actualStart.Value.ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException) { return true; }
        return Math.Abs(actual - procStartFileTime.Value) <= 2L * TimeSpan.TicksPerSecond;
    }

    /// <summary>CLAUDE_CONFIG_DIR\sessions when that is set, else the profile's .claude\sessions.</summary>
    public static string DefaultDir()
    {
        var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(cfg)) return Path.Combine(cfg, "sessions");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
    }
}

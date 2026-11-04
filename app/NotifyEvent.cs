using System;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>Kinds of session events sent by hooks (notify.ps1).</summary>
public enum NotifyEventKind
{
    Unknown,
    Started,   // SessionStart
    Running,   // UserPromptSubmit (a new turn began)
    Done,      // Stop
    Ask,       // Notification (Claude is waiting for the user)
    Ended,     // SessionEnd
}

/// <summary>
/// One event from a Claude session. The session's identity is its
/// <see cref="SessionId"/>; older hooks that do not send one fall back to
/// the project path, then to source+surface, so a pet never duplicates
/// just because an event arrived through the legacy GET route.
/// </summary>
public sealed class NotifyEvent
{
    public NotifyEventKind Kind { get; init; } = NotifyEventKind.Unknown;
    public string SessionId { get; init; } = "";
    public string Source { get; init; } = "Claude";
    public string ProjectPath { get; init; } = "";
    public string Hwnd { get; init; } = "";
    public string Surface { get; init; } = "";

    public string EffectiveId =>
        !string.IsNullOrWhiteSpace(SessionId) ? SessionId :
        !string.IsNullOrWhiteSpace(ProjectPath) ? "path:" + ProjectPath :
        "source:" + Source + "|" + Surface;

    public static NotifyEventKind ParseKind(string? type) =>
        (type ?? "").Trim().ToLowerInvariant() switch
        {
            "started" or "sessionstart" or "session_start" => NotifyEventKind.Started,
            "running" or "prompt" or "userpromptsubmit" or "user_prompt_submit" => NotifyEventKind.Running,
            "done" or "stop" => NotifyEventKind.Done,
            "ask" or "notification" => NotifyEventKind.Ask,
            "ended" or "sessionend" or "session_end" => NotifyEventKind.Ended,
            _ => NotifyEventKind.Unknown,
        };

    /// <summary>Parse the JSON body posted by the current notify.ps1.</summary>
    public static NotifyEvent FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new NotifyEvent();

            string Get(params string[] names)
            {
                foreach (var name in names)
                {
                    if (!root.TryGetProperty(name, out var v)) continue;
                    return v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString() ?? "",
                        JsonValueKind.Number => v.GetRawText(),
                        _ => "",
                    };
                }
                return "";
            }

            var kind = ParseKind(Get("type", "event", "hook_event_name"));
            var path = Get("cwd", "projectPath", "project_path");
            var source = Get("source");
            if (string.IsNullOrWhiteSpace(source)) source = LeafName(path);
            return new NotifyEvent
            {
                Kind = kind,
                SessionId = Get("session_id", "sessionId", "id"),
                Source = string.IsNullOrWhiteSpace(source) ? "Claude" : source,
                ProjectPath = path,
                Hwnd = Get("hwnd"),
                Surface = Get("surface"),
            };
        }
        catch
        {
            return new NotifyEvent();
        }
    }

    /// <summary>Parse the legacy GET /done?source=..&amp;hwnd=.. route.</summary>
    public static NotifyEvent FromQuery(string path)
    {
        string source = "Claude", hwnd = "", type = "done", surface = "", sessionId = "", cwd = "";
        int q = path.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in path.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair.Substring(0, eq);
                var val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                if (key == "source" && !string.IsNullOrWhiteSpace(val)) source = val;
                else if (key == "hwnd") hwnd = val;
                else if (key == "type" && !string.IsNullOrWhiteSpace(val)) type = val;
                else if (key == "surface") surface = val;
                else if (key == "session_id") sessionId = val;
                else if (key == "cwd") cwd = val;
            }
        }
        if (string.IsNullOrWhiteSpace(source) || source == "Claude")
        {
            var leaf = LeafName(cwd);
            if (!string.IsNullOrWhiteSpace(leaf)) source = leaf;
        }
        return new NotifyEvent
        {
            Kind = ParseKind(type),
            SessionId = sessionId,
            Source = source,
            ProjectPath = cwd,
            Hwnd = hwnd,
            Surface = surface,
        };
    }

    public static string LeafName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.Trim().TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeTaskBar;

public enum SessionStatus
{
    Running,
    Asking,      // waiting for the user (Notification hook)
    DoneUnseen,  // finished, not yet acknowledged
    DoneSeen,    // finished and acknowledged
}

public sealed class SessionInfo
{
    internal SessionInfo(string id) { Id = id; }

    public string Id { get; }
    public string Source { get; internal set; } = "Claude";
    public string ProjectPath { get; internal set; } = "";
    public string Surface { get; internal set; } = "";
    public string Hwnd { get; internal set; } = "";
    public SessionStatus Status { get; internal set; } = SessionStatus.Running;
    public DateTimeOffset StartedAt { get; internal set; }
    public DateTimeOffset LastEventAt { get; internal set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Source) ? Source : NotifyEvent.LeafName(ProjectPath);

    /// <summary>Display priority: lower comes first in the bar.</summary>
    public int Priority => Status switch
    {
        SessionStatus.Asking => 0,
        SessionStatus.DoneUnseen => 1,
        SessionStatus.Running => 2,
        _ => 3, // DoneSeen
    };
}

/// <summary>
/// The session ledger the old design lacked: every known session, keyed by
/// its session id, with its current status. Events update the ledger; the
/// pet bar is only a view over it, so a missed popup loses nothing.
/// </summary>
public sealed class SessionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new();

    /// <summary>Raised after any change. May fire on a background thread.</summary>
    public event Action? Changed;

    public int Count { get { lock (_lock) return _sessions.Count; } }

    /// <summary>Apply one hook event. Returns false for events we ignore.</summary>
    public bool Apply(NotifyEvent e, DateTimeOffset now)
    {
        if (e.Kind == NotifyEventKind.Unknown) return false;
        var id = e.EffectiveId;
        bool changed;
        lock (_lock)
        {
            if (e.Kind == NotifyEventKind.Ended)
            {
                changed = _sessions.Remove(id);
            }
            else
            {
                if (!_sessions.TryGetValue(id, out var s))
                {
                    s = new SessionInfo(id) { StartedAt = now };
                    _sessions[id] = s;
                }
                if (!string.IsNullOrWhiteSpace(e.Source)) s.Source = e.Source;
                if (!string.IsNullOrWhiteSpace(e.ProjectPath)) s.ProjectPath = e.ProjectPath;
                if (!string.IsNullOrWhiteSpace(e.Surface)) s.Surface = e.Surface;
                if (!string.IsNullOrWhiteSpace(e.Hwnd)) s.Hwnd = e.Hwnd;
                s.Status = e.Kind switch
                {
                    NotifyEventKind.Done => SessionStatus.DoneUnseen,
                    NotifyEventKind.Ask => SessionStatus.Asking,
                    _ => SessionStatus.Running, // Started / Running
                };
                s.LastEventAt = now;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Mark a finished session as acknowledged (running/asking keep their status).</summary>
    public void Acknowledge(string id)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s) && s.Status == SessionStatus.DoneUnseen)
            {
                s.Status = SessionStatus.DoneSeen;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Remove a pet from the bar without touching the real session.</summary>
    public void Dismiss(string id)
    {
        bool changed;
        lock (_lock) changed = _sessions.Remove(id);
        if (changed) Changed?.Invoke();
    }

    public void Clear()
    {
        bool changed;
        lock (_lock) { changed = _sessions.Count > 0; _sessions.Clear(); }
        if (changed) Changed?.Invoke();
    }

    /// <summary>All sessions, in display order (priority, then longest-waiting first).</summary>
    public IReadOnlyList<SessionInfo> Snapshot()
    {
        lock (_lock)
        {
            return _sessions.Values
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.LastEventAt)
                .ThenBy(s => s.StartedAt)
                .ToList();
        }
    }
}

/// <summary>Which pets fit in the bar (at most MaxDisplayed) and how many overflow.</summary>
public static class PetSelection
{
    public const int MaxDisplayed = 5;

    public static (IReadOnlyList<SessionInfo> Displayed, IReadOnlyList<SessionInfo> Overflow)
        Select(IReadOnlyList<SessionInfo> ordered, int max = MaxDisplayed)
    {
        if (max < 0) max = 0;
        var displayed = ordered.Take(max).ToList();
        var overflow = ordered.Skip(max).ToList();
        return (displayed, overflow);
    }
}

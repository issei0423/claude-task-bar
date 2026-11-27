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
    /// <summary>Orca terminal tab handle; empty when the session is not in Orca.</summary>
    public string OrcaTerminal { get; internal set; } = "";
    /// <summary>Claude's process id (0 = unknown). SessionLiveness watches it to see the session is gone.</summary>
    public int Pid { get; internal set; }
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
    // Ids that left on purpose (SessionEnd, right click, clear). Reconcile does
    // not bring them back from Claude's session list; the session's own next
    // hook event does (Apply forgets the id).
    private readonly HashSet<string> _left = new();

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
                _left.Add(id);
            }
            else
            {
                _left.Remove(id);
                if (!_sessions.TryGetValue(id, out var s))
                {
                    s = new SessionInfo(id) { StartedAt = now };
                    _sessions[id] = s;
                }
                if (!string.IsNullOrWhiteSpace(e.Source)) s.Source = e.Source;
                if (!string.IsNullOrWhiteSpace(e.ProjectPath)) s.ProjectPath = e.ProjectPath;
                if (!string.IsNullOrWhiteSpace(e.Surface)) s.Surface = e.Surface;
                if (!string.IsNullOrWhiteSpace(e.Hwnd)) s.Hwnd = e.Hwnd;
                if (!string.IsNullOrWhiteSpace(e.OrcaTerminal)) s.OrcaTerminal = e.OrcaTerminal;
                if (e.Pid >= 1) s.Pid = e.Pid;
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

    /// <summary>
    /// The user went to the session (clicked its pet): take back the call for
    /// attention, but keep the pet. DoneUnseen -> DoneSeen, Asking -> Running;
    /// Running and DoneSeen stay as they are. Raises Changed only on a change.
    /// </summary>
    public void Visit(string id)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
            {
                if (s.Status == SessionStatus.DoneUnseen) { s.Status = SessionStatus.DoneSeen; changed = true; }
                else if (s.Status == SessionStatus.Asking) { s.Status = SessionStatus.Running; changed = true; }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Remove a pet from the bar without touching the real session.</summary>
    public void Dismiss(string id)
    {
        bool changed;
        lock (_lock) { changed = _sessions.Remove(id); if (changed) _left.Add(id); }
        if (changed) Changed?.Invoke();
    }

    public void Clear()
    {
        bool changed;
        lock (_lock)
        {
            changed = _sessions.Count > 0;
            foreach (var id in _sessions.Keys) _left.Add(id);
            _sessions.Clear();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Fit the ledger to one running session from Claude Code's own list
    /// (~/.claude/sessions). Returns true (and raises Changed) when it changed
    /// something. Hooks stay in charge of status: an existing session keeps its
    /// status; a new one starts as Running when the list says "busy" and as
    /// DoneSeen otherwise.
    /// "Others" are ledger sessions with the same pid but another id (the old id
    /// of a process whose session changed). If any of them had an event more
    /// recent than the list's UpdatedAt, the list is older than what the hooks
    /// said, so nothing is done. Otherwise they are removed.
    /// A session that left on purpose (ended, right click) is not brought back.
    /// </summary>
    public bool Reconcile(LiveSessionEntry e, DateTimeOffset now)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_left.Contains(e.SessionId)) return false;

            List<SessionInfo>? others = null;
            foreach (var s in _sessions.Values)
                if (s.Pid == e.Pid && s.Id != e.SessionId) (others ??= new List<SessionInfo>()).Add(s);

            if (others != null && e.UpdatedAt != null)
            {
                foreach (var o in others)
                    if (o.LastEventAt > e.UpdatedAt.Value) return false;
            }
            if (others != null)
            {
                foreach (var o in others) _sessions.Remove(o.Id);
                changed = true;
            }

            if (!_sessions.TryGetValue(e.SessionId, out var cur))
            {
                var leaf = NotifyEvent.LeafName(e.Cwd);
                _sessions[e.SessionId] = new SessionInfo(e.SessionId)
                {
                    Source = string.IsNullOrWhiteSpace(leaf) ? "Claude" : leaf,
                    ProjectPath = e.Cwd,
                    Pid = e.Pid,
                    Status = string.Equals(e.Status, "busy", StringComparison.OrdinalIgnoreCase)
                        ? SessionStatus.Running : SessionStatus.DoneSeen,
                    StartedAt = now,
                    LastEventAt = now,
                };
                changed = true;
            }
            else
            {
                if (cur.Pid == 0) { cur.Pid = e.Pid; changed = true; }
                if (string.IsNullOrWhiteSpace(cur.ProjectPath) && !string.IsNullOrWhiteSpace(e.Cwd))
                {
                    cur.ProjectPath = e.Cwd;
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Remember the window of a session, only if it has none yet. No Changed.</summary>
    public void SetHwnd(string id, string hwnd)
    {
        if (string.IsNullOrWhiteSpace(hwnd)) return;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s) && string.IsNullOrWhiteSpace(s.Hwnd)) s.Hwnd = hwnd;
        }
    }

    /// <summary>
    /// Set the window the session's console is shown in. Unlike SetHwnd this
    /// replaces what is there (the console's own answer is surer than the
    /// hook's parent-process guess). An empty value is ignored. No Changed.
    /// </summary>
    public void SetConsoleHwnd(string id, string hwnd)
    {
        if (string.IsNullOrWhiteSpace(hwnd)) return;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) s.Hwnd = hwnd;
        }
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

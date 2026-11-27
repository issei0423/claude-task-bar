using System;
using System.Collections.Generic;

namespace ClaudeTaskBar;

/// <summary>
/// Finds sessions that no longer exist, so their pets can leave. WPF-free: the
/// caller passes in how to look at processes and windows, which keeps this
/// testable.
/// A session with a Pid is gone when that process is gone, or when a process
/// with the same pid started at a different time (the pid was reused). It is
/// only judged once the process has been seen alive, so a pid that was never
/// seen is left alone. A session without a Pid but with a window handle is
/// gone when the window is. A session with neither is never removed here.
/// </summary>
public sealed class SessionLiveness
{
    private readonly Dictionary<string, (int Pid, DateTime Start)> _seen = new();

    /// <param name="processStart">Start time of the process, or null when there is no such process.</param>
    /// <param name="windowExists">Does this window handle (as text) still name a window?</param>
    /// <returns>Ids of the sessions that are gone, in the order given.</returns>
    public IReadOnlyList<string> FindGone(
        IReadOnlyList<SessionInfo> sessions,
        Func<int, DateTime?> processStart,
        Func<string, bool> windowExists)
    {
        var gone = new List<string>();
        var present = new HashSet<string>();

        foreach (var s in sessions)
        {
            present.Add(s.Id);

            if (s.Pid >= 1)
            {
                var start = processStart(s.Pid);
                if (_seen.TryGetValue(s.Id, out var known) && known.Pid == s.Pid)
                {
                    if (start == null || start.Value != known.Start)
                    {
                        gone.Add(s.Id);
                        _seen.Remove(s.Id);
                    }
                }
                else if (start != null)
                {
                    _seen[s.Id] = (s.Pid, start.Value);
                }
                continue;
            }

            if (TryWindowHandle(s.Hwnd) && !windowExists(s.Hwnd))
                gone.Add(s.Id);
        }

        // Forget sessions that are not in the ledger any more.
        List<string>? stale = null;
        foreach (var id in _seen.Keys)
            if (!present.Contains(id)) (stale ??= new List<string>()).Add(id);
        if (stale != null) foreach (var id in stale) _seen.Remove(id);

        return gone;
    }

    /// <summary>True when the text is a window handle we can ask about: a number above 0.</summary>
    private static bool TryWindowHandle(string? text) =>
        long.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0;
}

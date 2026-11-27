using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeTaskBar;

/// <summary>
/// Pure choices for "where does a click on a pet go" (WPF-free so CoreTests
/// can check them): which window, and which tab inside it.
/// </summary>
public static class JumpTarget
{
    /// <summary>
    /// The window to bring forward: the console's window if it reads as a
    /// handle (a whole number of 1 or more) and is usable; else the fallback
    /// (the hook's hwnd) under the same conditions; else "". `usable` is only
    /// asked about values that read as handles.
    /// </summary>
    public static string ChooseWindow(string? consoleHwnd, string? fallbackHwnd, Func<string, bool> usable)
    {
        foreach (var candidate in new[] { consoleHwnd, fallbackHwnd })
        {
            if (long.TryParse(candidate?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1)
            {
                var text = v.ToString(CultureInfo.InvariantCulture);
                if (usable(text)) return text;
            }
        }
        return "";
    }

    /// <summary>
    /// The index of the tab whose name is exactly the console title (case
    /// matters), when exactly one tab has it. Otherwise -1: no title, no
    /// match, or several tabs with the same name (we cannot tell which one).
    /// </summary>
    public static int PickTab(IReadOnlyList<string> tabNames, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return -1;
        int found = -1;
        for (int i = 0; i < tabNames.Count; i++)
        {
            if (!string.Equals(tabNames[i], title, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }
}

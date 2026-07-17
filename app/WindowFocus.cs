using System;
using System.Runtime.InteropServices;

namespace ClaudeTaskBar;

/// <summary>
/// Bring a target window to the foreground. The hwnd string is supplied by the
/// adapter (for Claude Code it is the console window of the terminal running the hook).
/// </summary>
public static class WindowFocus
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    public static void FocusByHandleString(string hwndStr)
    {
        if (!long.TryParse(hwndStr, out var raw) || raw == 0) return;
        var hWnd = new IntPtr(raw);
        if (!IsWindow(hWnd)) return;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }
}

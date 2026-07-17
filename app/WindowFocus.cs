using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeTaskBar;

public static class WindowFocus
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr h, bool alt);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    private const int SW_RESTORE = 9, SW_SHOW = 5;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;
    private const uint ASFW_ANY = 0xFFFFFFFF;

    private static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ctb.log"),
              DateTime.Now.ToString("HH:mm:ss.fff ") + m + Environment.NewLine); }
        catch { }
    }

    public static void Focus(string hwndStr, string source)
    {
        Log($"Focus() hwnd='{hwndStr}' source='{source}'");
        IntPtr target = IntPtr.Zero;
        if (long.TryParse(hwndStr, out var raw) && raw != 0)
        {
            var h = new IntPtr(raw);
            bool win = IsWindow(h), vis = IsWindowVisible(h);
            Log($"  hwnd parsed=0x{raw:X} IsWindow={win} IsVisible={vis}");
            if (win) target = h; // accept even if minimized/not-visible; we restore below
        }
        if (target == IntPtr.Zero && !string.IsNullOrWhiteSpace(source) && source != "Claude" && source != "test")
        {
            target = FindByTitle(source);
            Log($"  title match -> 0x{target.ToInt64():X}");
        }
        if (target == IntPtr.Zero) { Log("  NO TARGET -> return"); return; }
        ForceForeground(target);
        Log($"  foreground now=0x{GetForegroundWindow().ToInt64():X} target=0x{target.ToInt64():X}");
    }

    private static void ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        AllowSetForegroundWindow(ASFW_ANY);

        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint cur = GetCurrentThreadId();
        uint tgt = GetWindowThreadProcessId(hWnd, out _);

        AttachThreadInput(cur, fg, true);
        AttachThreadInput(cur, tgt, true);
        BringWindowToTop(hWnd);
        ShowWindow(hWnd, SW_SHOW);
        SetForegroundWindow(hWnd);
        SwitchToThisWindow(hWnd, true);
        AttachThreadInput(cur, fg, false);
        AttachThreadInput(cur, tgt, false);
    }

    private static IntPtr FindByTitle(string needleRaw)
    {
        var needle = needleRaw.ToLowerInvariant();
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().ToLowerInvariant().Contains(needle)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

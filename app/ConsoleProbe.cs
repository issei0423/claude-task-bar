using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeTaskBar;

/// <summary>The window and title of the console a process is running in.</summary>
public sealed class ConsoleInfo
{
    public ConsoleInfo(string hwnd, string title) { Hwnd = hwnd; Title = title; }
    /// <summary>The top-level window the console belongs to, as decimal text.</summary>
    public string Hwnd { get; }
    /// <summary>The console's title (in Windows Terminal: the name of the tab).</summary>
    public string Title { get; }
}

/// <summary>
/// Asks Windows which window a process's console is shown in. Walking up the
/// parent processes finds the wrong window when the shell was started from
/// explorer, or when one Windows Terminal process owns several windows. The
/// console itself knows: attach to it, take its window, and go up to the
/// window that owns it (for Windows Terminal, the terminal window of that
/// session, not another one).
/// Only P/Invoke; no WPF.
/// </summary>
public static class ConsoleProbe
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetConsoleTitleW(StringBuilder title, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

    private const uint GA_ROOTOWNER = 3;

    // Attaching is process-wide state: one probe at a time.
    private static readonly object Gate = new();

    /// <summary>The console window and title of the process, or null when it cannot be found. Never throws.</summary>
    public static ConsoleInfo? Find(int pid)
    {
        if (pid < 1) return null;
        lock (Gate)
        {
            bool attached = false;
            try
            {
                // While attached, a Ctrl+C in that console must not end this app.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                if (!AttachConsole((uint)pid)) return null;
                attached = true;

                var console = GetConsoleWindow();
                if (console == IntPtr.Zero) return null;

                var sb = new StringBuilder(1024);
                uint n = GetConsoleTitleW(sb, (uint)sb.Capacity);
                var title = n > 0 ? sb.ToString() : "";

                var root = GetAncestor(console, GA_ROOTOWNER);
                var window = root != IntPtr.Zero ? root : console;
                if (!IsWindow(window)) return null;

                return new ConsoleInfo(window.ToInt64().ToString(CultureInfo.InvariantCulture), title);
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    if (attached) FreeConsole();
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
                catch { }
            }
        }
    }
}

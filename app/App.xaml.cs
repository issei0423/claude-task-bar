using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using WForms = System.Windows.Forms;

namespace ClaudeTaskBar;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private WForms.NotifyIcon? _tray;
    private PetBarWindow? _petBar;
    private NotifyServer? _server;
    private readonly SessionRegistry _registry = new();
    private AppConfig _config = new(); // read once at startup
    private int _testSeq;
    private DispatcherTimer? _watch;
    private readonly SessionLiveness _liveness = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "ClaudeTaskBar_SingleInstance_v1", out bool created);
        if (!created) { Shutdown(); return; }

        _config = AppConfig.Load();
        _petBar = new PetBarWindow(_registry, JumpToSession, _config.PetScale);
        _registry.Changed += () => Dispatcher.BeginInvoke(new Action(() => _petBar?.Refresh()));

        _tray = new WForms.NotifyIcon { Icon = LoadTrayIcon(), Visible = true, Text = "claude-task-bar" };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.DoubleClick += (_, __) => AddTestSession();

        if (_petBar.LoadError != null)
        {
            // Without the pictures no pet can be drawn; say so instead of staying silent.
            _tray.ShowBalloonTip(5000, "claude-task-bar",
                "ペットの絵を読めませんでした。" + _petBar.LoadError, WForms.ToolTipIcon.Warning);
        }

        _server = new NotifyServer(_config.Port, ev =>
            Dispatcher.BeginInvoke(new Action(() => HandleEvent(ev))));
        try { _server.Start(); }
        catch (Exception ex)
        {
            // Surface it instead of dying silently: the old design's
            // invisible failures were a core complaint.
            System.Diagnostics.Debug.WriteLine("NotifyServer start failed: " + ex.Message);
            _tray.ShowBalloonTip(5000, "claude-task-bar",
                "受信ポート " + _config.Port + " を開けませんでした。別のアプリが使っているかもしれません。",
                WForms.ToolTipIcon.Warning);
        }

        StartWatchingSessions();

        // startup: full-screen transparent splash
        Dispatcher.BeginInvoke(new Action(ShowSplash), DispatcherPriority.ApplicationIdle);
    }

    private WForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WForms.ContextMenuStrip();
        menu.Items.Add("テスト: セッション追加（実行中）", null, (_, __) => AddTestSession());
        menu.Items.Add("テスト: 最新セッションを完了にする", null, (_, __) => ApplyToLatestTest(NotifyEventKind.Done));
        menu.Items.Add("テスト: 最新セッションを質問待ちにする", null, (_, __) => ApplyToLatestTest(NotifyEventKind.Ask));
        menu.Items.Add("テスト: 7件まとめて追加（あふれ確認）", null, (_, __) => { for (int i = 0; i < 7; i++) AddTestSession(); });
        menu.Items.Add("テスト: 全ペットを消す", null, (_, __) => _registry.Clear());
        var flee = new WForms.ToolStripMenuItem("テスト: 大焦り（使用量 90% 超えのふり）") { CheckOnClick = true };
        flee.CheckedChanged += (_, __) => { _testFlee = flee.Checked; CheckUsage(); };
        menu.Items.Add(flee);
        menu.Items.Add("起動演出をもう一度", null, (_, __) => ShowSplash());
        menu.Items.Add("終了", null, (_, __) => Shutdown());
        return menu;
    }

    private static readonly string[] TestSurfaces = { "terminal", "ide", "web", "chrome", "desktop" };

    private void AddTestSession()
    {
        _testSeq++;
        var surface = TestSurfaces[(_testSeq - 1) % TestSurfaces.Length];
        HandleEvent(new NotifyEvent
        {
            Kind = NotifyEventKind.Started,
            SessionId = "test-" + _testSeq,
            Source = "test-" + _testSeq,
            ProjectPath = "C:\\test\\test-" + _testSeq,
            Surface = surface,
        });
    }

    private void ApplyToLatestTest(NotifyEventKind kind)
    {
        if (_testSeq == 0) { AddTestSession(); }
        HandleEvent(new NotifyEvent
        {
            Kind = kind,
            SessionId = "test-" + _testSeq,
            Source = "test-" + _testSeq,
        });
    }

    /// <summary>
    /// Update the ledger (the source of truth); the pet bar redraws from it
    /// via <see cref="SessionRegistry.Changed"/>.
    /// </summary>
    private void HandleEvent(NotifyEvent ev)
    {
        // "Claude is waiting for you" that arrives while Claude's own list says the
        // session is idle is the reminder sent after a long wait (idle_prompt), not a
        // question: do not call the user over for it.
        if (ev.Kind == NotifyEventKind.Ask && ev.Pid >= 1 && ListSaysIdle(ev))
        {
            LogOnce("ask ignored: " + ev.Source + " is idle");
            return;
        }

        _registry.Apply(ev, DateTimeOffset.Now);
        // The hook's own hwnd (a parent-process guess) was just stored; replace it
        // with the console's window.
        if (ev.Pid >= 1 && ev.Kind != NotifyEventKind.Ended)
            ApplyConsoleWindow(ev.EffectiveId, ev.Source, ev.Pid);
        // Look at the list right away: its status may already be newer than this
        // event, and the process that sent the event is surely alive now
        // (SessionLiveness only judges a pid it has seen alive, and the 5-second tick
        // alone would miss a session that ends within seconds).
        CheckSessions();
    }

    /// <summary>
    /// Does the list file of this event's pid belong to the same session and say
    /// "idle"? False when the file is missing, unreadable, of another session, or
    /// not idle.
    /// </summary>
    private static bool ListSaysIdle(NotifyEvent ev)
    {
        try
        {
            var path = Path.Combine(LiveSessions.DefaultDir(), ev.Pid + ".json");
            if (!File.Exists(path)) return false;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var rd = new StreamReader(fs))
                text = rd.ReadToEnd();
            var entry = LiveSessions.Parse(text);
            return entry != null &&
                   entry.SessionId == ev.EffectiveId &&
                   string.Equals(entry.Status.Trim(), "idle", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private FileSystemWatcher? _listWatcher;
    private DispatcherTimer? _listDebounce;

    /// <summary>
    /// Look at the list as soon as a file in it is written (many writes come in a
    /// row, so they are gathered for 150 ms). The 5-second timer stays as the
    /// fallback; if the folder cannot be watched, only that timer runs.
    /// </summary>
    private void StartWatchingList()
    {
        try
        {
            var dir = LiveSessions.DefaultDir();
            if (!Directory.Exists(dir))
            {
                LogOnce("session list folder not found; checking every 5 seconds only: " + dir);
                return;
            }
            _listDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _listDebounce.Tick += (_, __) =>
            {
                _listDebounce.Stop();
                CheckSessions();
            };

            var w = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // Called on a watcher thread: hand over to the UI thread.
            void Kick()
            {
                try { Dispatcher.BeginInvoke(new Action(() => { if (_listDebounce is { IsEnabled: false }) _listDebounce.Start(); })); }
                catch { }
            }
            w.Changed += (_, __) => Kick();
            w.Created += (_, __) => Kick();
            w.Deleted += (_, __) => Kick();
            w.Renamed += (_, __) => Kick();
            w.Error += (_, a) => LogOnce("session list watcher error: " + a.GetException().Message);
            w.EnableRaisingEvents = true;
            _listWatcher = w;
        }
        catch (Exception ex) { LogOnce("session list watcher failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private void JumpToSession(SessionInfo s)
    {
        // Ask the session's console which window it is in (a Claude process
        // that has a pid). Walking up the parent processes can end in an
        // explorer window or in another window of the same Windows Terminal.
        var console = s.Pid >= 1 ? ConsoleProbe.Find(s.Pid) : null;
        var hwnd = JumpTarget.ChooseWindow(console?.Hwnd, s.Hwnd, WindowUsable);
        var how = hwnd.Length == 0 ? "none" : (console != null && hwnd == console.Hwnd ? "console" : "fallback");

        // Window first, so a failed tab switch still lands the user in the right app.
        WindowFocus.Focus(hwnd, s.Source);

        // In Windows Terminal, also select the tab named like the console's
        // title (only when exactly one tab has that name). Off the UI thread:
        // UI Automation can be slow.
        var source = s.Source;
        var pid = s.Pid;
        string JumpLine(string tab) =>
            "jump: " + source + " pid=" + pid + " window=" + (hwnd.Length > 0 ? hwnd : "-") + " (" + how + ") tab=" + tab;
        if (console != null && console.Title.Length > 0 && hwnd.Length > 0)
        {
            var title = console.Title;
            _ = Task.Run(() =>
            {
                string tab;
                try { tab = SelectTab(hwnd, title); }
                catch (Exception ex)
                {
                    LogOnce("tab switch failed: " + ex.GetType().Name + ": " + ex.Message);
                    tab = "見つからない";
                }
                OrcaJump.Log(JumpLine(tab));
            });
        }
        else
        {
            OrcaJump.Log(JumpLine("調べない"));
        }

        // Inside Orca, also switch to the session's own terminal tab. Off the
        // UI thread: the CLI may take up to OrcaJump.SwitchTimeout.
        var handle = s.OrcaTerminal;
        if (!string.IsNullOrEmpty(handle))
        {
            var orcaPath = _config.OrcaPath;
            _ = Task.Run(async () =>
            {
                try { await OrcaJump.TrySwitchAsync(orcaPath, handle); }
                catch (Exception ex) { OrcaJump.Log("Orca switch failed: " + ex.GetType().Name + ": " + ex.Message); }
            });
        }

        // The pet stays (owner, 2026-11-27: it is a resident pet). Going to the
        // session takes back the call for attention: a finished pet becomes
        // "seen" and walks home to idle, an asking pet goes back to running.
        // The pet leaves only when its session is gone (see WatchSessions) or
        // on a right click. Everything the Orca switch needs was copied above.
        _registry.Visit(s.Id);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>A window we may bring forward: it exists and does not belong to explorer (its internal helper windows are not terminals).</summary>
    private static bool WindowUsable(string hwnd)
    {
        if (!long.TryParse(hwnd, out var h)) return false;
        var ptr = new IntPtr(h);
        if (!IsWindow(ptr)) return false;
        try
        {
            if (GetWindowThreadProcessId(ptr, out var owner) == 0 || owner == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)owner);
            return !IsExplorer(p.ProcessName);
        }
        catch { return false; }
    }

    private static bool IsExplorer(string processName) =>
        string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Select the tab of the window whose name is the console's title, if
    /// exactly one tab has that name. Returns what happened, for the log.
    /// </summary>
    private static string SelectTab(string hwnd, string title)
    {
        var root = AutomationElement.FromHandle(new IntPtr(long.Parse(hwnd, System.Globalization.CultureInfo.InvariantCulture)));
        var tabs = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        var names = new List<string>(tabs.Count);
        for (int i = 0; i < tabs.Count; i++) names.Add(tabs[i].Current.Name ?? "");

        int index = JumpTarget.PickTab(names, title);
        if (index < 0) return "見つからない";
        if (!tabs[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) return "見つからない";
        var item = (SelectionItemPattern)pattern;
        if (item.Current.IsSelected) return "そのまま";
        item.Select();
        return "切り替えた";
    }

    // Console window per Claude pid, asked once (a failed ask is remembered as ""
    // so a slow or refusing process is not probed every 5 seconds).
    private readonly Dictionary<int, string> _consoleByPid = new();

    /// <summary>
    /// Put the console's window on the session (replacing the hook's guess).
    /// Asks Windows once per pid. Returns false when no console window is known.
    /// </summary>
    private bool ApplyConsoleWindow(string id, string source, int pid)
    {
        if (pid < 1) return false;
        if (!_consoleByPid.TryGetValue(pid, out var hwnd))
        {
            hwnd = ConsoleProbe.Find(pid)?.Hwnd ?? "";
            _consoleByPid[pid] = hwnd;
            if (hwnd.Length > 0) LogOnce("window of " + source + " (pid " + pid + "): " + hwnd);
        }
        if (hwnd.Length == 0) return false;
        _registry.SetConsoleHwnd(id, hwnd);
        return true;
    }

    /// <summary>
    /// Every few seconds, drop the pets whose session is gone: the Claude
    /// process ended (or its pid was reused), or, for events that carry no pid,
    /// the terminal window is gone. Sessions with neither (the tray's test
    /// sessions) are never dropped here.
    /// </summary>
    private void StartWatchingSessions()
    {
        CheckSessions(); // once right away, so pets of sessions that are already open appear at startup
        _petBar?.StartAnnouncing(); // ...and those do not play the birth animation; pets from now on do
        CheckUsage();
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watch.Tick += (_, __) => { CheckSessions(); CheckUsage(); };
        _watch.Start();
        StartWatchingList();
    }

    private bool _fleeing;
    /// <summary>Tray test switch: run away as if the usage were over the threshold.</summary>
    private bool _testFlee;

    /// <summary>
    /// Read the 5-hour usage and tell the pets to run away when it has reached
    /// the threshold. Unknown (no file, broken) or switched off = they stay.
    /// </summary>
    private void CheckUsage()
    {
        bool flee = false;
        double? used = null;
        try
        {
            if (_config.FleeEnabled)
            {
                used = UsageReader.ReadFile(_config.UsageFile, DateTimeOffset.Now);
                flee = UsageReader.ShouldFlee(used, _config.FleeThreshold);
            }
        }
        catch (Exception ex) { LogOnce("usage check failed: " + ex.GetType().Name + ": " + ex.Message); }
        if (_testFlee) flee = true; // the tray's test switch wins over the real usage

        if (flee != _fleeing)
        {
            _fleeing = flee;
            OrcaJump.Log("fleeing " + (flee ? "on" : "off") + " (5-hour usage " +
                (used.HasValue ? used.Value.ToString("0.#") + "%" : "unknown") + ", threshold " + _config.FleeThreshold + "%" +
                (_testFlee ? ", test switch on" : "") + ")");
        }
        _petBar?.SetFleeing(flee);
    }

    private void CheckSessions()
    {
        DiscoverSessions();
        try
        {
            foreach (var id in _liveness.FindGone(_registry.Snapshot(), ProcessStart, WindowExists))
                _registry.Dismiss(id);
        }
        catch (Exception ex) { LogOnce("session watch failed: " + ex.GetType().Name + ": " + ex.Message); }

        // Forget the console window of a process that is gone.
        foreach (var pid in _consoleByPid.Keys.ToList())
            if (ProcessStart(pid) == null) _consoleByPid.Remove(pid);
    }

    // The same failure every 5 seconds would fill the log: each text is written once.
    private readonly HashSet<string> _logged = new();

    private void LogOnce(string message)
    {
        bool first;
        lock (_logged) first = _logged.Add(message); // also called from background tasks
        if (first) OrcaJump.Log(message);
    }

    /// <summary>
    /// Read Claude Code's own list of running sessions (~/.claude/sessions/*.json,
    /// read only) and give every open interactive session a pet, so pets show
    /// up after this app restarts without waiting for the next hook. Hooks
    /// still decide the status; the list only says who exists. Any file that
    /// cannot be read or understood is skipped.
    /// </summary>
    private void DiscoverSessions()
    {
        try
        {
            var dir = LiveSessions.DefaultDir();
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var rd = new StreamReader(fs))
                        text = rd.ReadToEnd();

                    var entry = LiveSessions.Parse(text);
                    if (entry == null || !LiveSessions.IsInteractive(entry)) continue;
                    if (!LiveSessions.SameProcess(entry.ProcStartFileTime, ProcessStart(entry.Pid))) continue;

                    _registry.Reconcile(entry, DateTimeOffset.Now);
                    var added = _registry.Snapshot().FirstOrDefault(s => s.Id == entry.SessionId);
                    if (added != null)
                    {
                        // The console knows its window; walking up the parents is only the fallback.
                        if (!ApplyConsoleWindow(added.Id, added.Source, entry.Pid) && string.IsNullOrWhiteSpace(added.Hwnd))
                        {
                            var hwnd = FindWindowOf(entry.Pid);
                            if (hwnd.Length > 0) _registry.SetHwnd(entry.SessionId, hwnd);
                        }
                    }
                }
                catch (IOException) { }                    // being written or locked: next time
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) { LogOnce("session discovery failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
        ref ProcessBasicInformation info, int size, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The parent's pid, or 0 when it cannot be found.</summary>
    private static int ParentPid(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(h, 0 /* ProcessBasicInformation */, ref info,
                System.Runtime.InteropServices.Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0) return 0;
            long parent = info.InheritedFromUniqueProcessId.ToInt64();
            return parent > 0 && parent <= int.MaxValue ? (int)parent : 0;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// The window of a process: walk up its parents (at most 12 steps) and take
    /// the first main window handle that is not 0, as decimal text. "" if none
    /// (or on any failure).
    /// </summary>
    private static string FindWindowOf(int pid)
    {
        try
        {
            int cur = pid;
            for (int i = 0; i < 12 && cur > 0; i++)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(cur);
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        // explorer's windows are internal helpers, not the terminal: take none.
                        if (IsExplorer(p.ProcessName)) return "";
                        return h.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch (ArgumentException) { return ""; } // the process is gone
                catch { }                                // cannot look at this one: try its parent
                int parent = ParentPid(cur);
                if (parent <= 0 || parent == cur) break;
                cur = parent;
            }
        }
        catch { }
        return "";
    }

    private static DateTime? ProcessStart(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (ArgumentException) { return null; } // no such process
        catch { return DateTime.MinValue; }        // cannot look (e.g. access denied): treat as alive
    }

    private static bool WindowExists(string hwnd) =>
        long.TryParse(hwnd, out var h) && IsWindow(new IntPtr(h));

    private static void ShowSplash()
    {
        try { new SplashWindow().Show(); } catch { }
    }

    private static Icon LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "tray.ico");
        try { if (File.Exists(path)) return new Icon(path); } catch { }
        return SystemIcons.Application;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watch?.Stop();
        _listDebounce?.Stop();
        if (_listWatcher != null)
        {
            try { _listWatcher.EnableRaisingEvents = false; _listWatcher.Dispose(); } catch { }
            _listWatcher = null;
        }
        _server?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}

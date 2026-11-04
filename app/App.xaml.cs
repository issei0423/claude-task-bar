using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "ClaudeTaskBar_SingleInstance_v1", out bool created);
        if (!created) { Shutdown(); return; }

        _petBar = new PetBarWindow(_registry, JumpToSession);
        _registry.Changed += () => Dispatcher.BeginInvoke(new Action(() => _petBar?.Refresh()));

        _tray = new WForms.NotifyIcon { Icon = LoadTrayIcon(), Visible = true, Text = "claude-task-bar" };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.DoubleClick += (_, __) => AddTestSession();

        _config = AppConfig.Load();
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
        _registry.Apply(ev, DateTimeOffset.Now);
    }

    private void JumpToSession(SessionInfo s)
    {
        // Window first, so a failed tab switch still lands the user in the right app.
        WindowFocus.Focus(s.Hwnd, s.Source);

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

        // Then the pet leaves the bar, whatever its status and whether the
        // tab switch works (owner, 2026-11-04). Everything the switch needs
        // was copied into locals above. The session's next event brings it back.
        _registry.Dismiss(s.Id);
    }

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
        _server?.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}

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
    private OverlayWindow? _overlay;
    private NotifyServer? _server;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "ClaudeTaskBar_SingleInstance_v1", out bool created);
        if (!created) { Shutdown(); return; }

        _overlay = new OverlayWindow();
        _overlay.Show();
        _overlay.Hide();

        _tray = new WForms.NotifyIcon { Icon = LoadTrayIcon(), Visible = true, Text = "claude-task-bar" };

        var menu = new WForms.ContextMenuStrip();
        menu.Items.Add("テスト: terminal", null, (_, __) => ShowDone("test", "", "done", "terminal"));
        menu.Items.Add("テスト: ide", null, (_, __) => ShowDone("test", "", "done", "ide"));
        menu.Items.Add("テスト: web", null, (_, __) => ShowDone("test", "", "done", "web"));
        menu.Items.Add("テスト: chrome", null, (_, __) => ShowDone("test", "", "done", "chrome"));
        menu.Items.Add("テスト: desktop", null, (_, __) => ShowDone("test", "", "done", "desktop"));
        menu.Items.Add("テスト: 質問", null, (_, __) => ShowDone("test", "", "ask", "terminal"));
        menu.Items.Add("起動演出をもう一度", null, (_, __) => ShowSplash());
        menu.Items.Add("終了", null, (_, __) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, __) => ShowDone("test", "", "done", "terminal");

        _server = new NotifyServer(8765, (source, hwnd, type, surface) =>
            Dispatcher.Invoke(() => ShowDone(source, hwnd, type, surface)));
        _server.Start();

        // startup: full-screen transparent splash
        Dispatcher.BeginInvoke(new Action(ShowSplash), DispatcherPriority.ApplicationIdle);
    }

    private static void ShowSplash()
    {
        try { new SplashWindow().Show(); } catch { }
    }

    private void ShowDone(string source, string hwnd, string type, string surface)
        => _overlay?.Notify(source, hwnd, type, surface);

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

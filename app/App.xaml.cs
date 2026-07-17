using System;
using System.Drawing;
using System.IO;
using System.Windows;
using WForms = System.Windows.Forms;

namespace ClaudeTaskBar;

public partial class App : System.Windows.Application
{
    private WForms.NotifyIcon? _tray;
    private OverlayWindow? _overlay;
    private NotifyServer? _server;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _overlay = new OverlayWindow();
        _overlay.Show();
        _overlay.Hide();

        _tray = new WForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "claude-task-bar"
        };

        var menu = new WForms.ContextMenuStrip();
        menu.Items.Add("テスト: 完了", null, (_, __) => ShowDone("test", "", "done"));
        menu.Items.Add("テスト: 質問", null, (_, __) => ShowDone("test", "", "ask"));
        menu.Items.Add("終了", null, (_, __) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, __) => ShowDone("test", "", "done");

        _server = new NotifyServer(8765, (source, hwnd, type) =>
            Dispatcher.Invoke(() => ShowDone(source, hwnd, type)));
        _server.Start();
    }

    private void ShowDone(string source, string hwnd, string type) => _overlay?.Notify(source, hwnd, type);

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

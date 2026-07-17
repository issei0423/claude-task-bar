using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ClaudeTaskBar;

public partial class OverlayWindow : Window
{
    private bool _ready;
    private string _pendingHwnd = "";
    private readonly DispatcherTimer _autoHide;

    public OverlayWindow()
    {
        InitializeComponent();
        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _autoHide.Tick += (_, __) => { _autoHide.Stop(); HideOverlay(); };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        try
        {
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            await Web.EnsureCoreWebView2Async();
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            var html = Path.Combine(AppContext.BaseDirectory, "assets", "mascot.html");
            Web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
            _ready = true;
        }
        catch { /* WebView2 runtime missing -> see RUN.md */ }
        Hide();
    }

    public void Notify(string source, string hwnd)
    {
        _pendingHwnd = hwnd ?? "";
        PositionBottomRight();
        Show();
        Topmost = true;
        Activate();
        if (_ready && Web.CoreWebView2 != null)
        {
            var js = $"window.ctbNotify && window.ctbNotify({JsonSerializer.Serialize(source)});";
            Web.CoreWebView2.ExecuteScriptAsync(js);
        }
        _autoHide.Stop();
        _autoHide.Start();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); }
        catch { msg = e.WebMessageAsJson.Trim('"'); }

        if (msg == "jump")
        {
            _autoHide.Stop();
            WindowFocus.FocusByHandleString(_pendingHwnd);
            HideOverlay();
        }
        else if (msg == "hide")
        {
            _autoHide.Stop();
            HideOverlay();
        }
    }

    private void HideOverlay() => Hide();

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 6;
        Top = wa.Bottom - Height - 6;
    }
}

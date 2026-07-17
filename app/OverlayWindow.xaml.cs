using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeTaskBar;

public partial class OverlayWindow : Window
{
    private string _pendingHwnd = "";
    private readonly DispatcherTimer _autoHide;
    private const double OffX = 360;

    public OverlayWindow()
    {
        InitializeComponent();
        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _autoHide.Tick += (_, __) => { _autoHide.Stop(); SlideOut(); };
        Loaded += OnLoaded;
        Mascot.MouseLeftButtonDown += (_, __) => Jump();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var img = Path.Combine(AppContext.BaseDirectory, "assets", "mascot.png");
            if (File.Exists(img))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(img);
                bmp.EndInit();
                Mascot.Source = bmp;
            }
        }
        catch { }
        PositionBottomRight();
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.X = OffX;
        Hide();
    }

    public void Notify(string source, string hwnd)
    {
        _pendingHwnd = hwnd ?? "";
        Chip.Text = string.IsNullOrWhiteSpace(source) ? "Claude" : source;
        Msg.Text = "終わったよー！";
        PositionBottomRight();
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.X = OffX;
        Show();
        Topmost = true;
        Activate();

        var a = new DoubleAnimation(OffX, 0, TimeSpan.FromSeconds(0.55))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Slide.BeginAnimation(TranslateTransform.XProperty, a);

        _autoHide.Stop();
        _autoHide.Start();
    }

    private void Jump()
    {
        _autoHide.Stop();
        WindowFocus.FocusByHandleString(_pendingHwnd);
        SlideOut();
    }

    private void SlideOut()
    {
        var a = new DoubleAnimation(Slide.X, OffX, TimeSpan.FromSeconds(0.4))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, __) => Hide();
        Slide.BeginAnimation(TranslateTransform.XProperty, a);
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 6;
        Top = wa.Bottom - Height - 6;
    }
}

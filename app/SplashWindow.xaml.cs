using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeTaskBar;

public partial class SplashWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public SplashWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSrc;
        Loaded += OnLoaded;
    }

    private void OnSrc(object? sender, EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // smooth scaling for the hand-drawn splash image
        RenderOptions.SetBitmapScalingMode(Mascot, BitmapScalingMode.HighQuality);
        Mascot.Width = 340;
        Mascot.Height = 340;

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "assets");
            var path = Path.Combine(dir, "splash.png");
            if (!File.Exists(path)) path = Path.Combine(dir, "mascot.png");
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit();
                Mascot.Source = bmp;
            }
        }
        catch { }

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)));
        var pop = new DoubleAnimation(0.7, 1.0, TimeSpan.FromSeconds(0.55))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        t.Tick += (_, __) =>
        {
            t.Stop();
            var fout = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.6));
            fout.Completed += (a, b) => Close();
            Root.BeginAnimation(OpacityProperty, fout);
        };
        t.Start();
    }
}

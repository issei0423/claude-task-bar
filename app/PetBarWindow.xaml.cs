using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// UseWindowsForms brings System.Drawing / System.Windows.Forms in via implicit
// usings; pin the WPF types this file uses so the names are not ambiguous.
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace ClaudeTaskBar;

/// <summary>
/// The pet bar: pixel-art pets that live on the taskbar's top edge, one per
/// session, at most PetSelection.MaxDisplayed. PetStage decides where each pet
/// is and what it does; this window only draws that (one pet dot = PetScale
/// screen pixels) and handles clicks: left = jump to the session (the pet then
/// leaves), right = remove only the pet.
/// The window never activates, so it cannot steal focus from the user's
/// current work. Its transparent pixels let clicks through to the windows
/// below; each pet has a nearly-invisible hit box so it stays clickable.
/// </summary>
public partial class PetBarWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const double OverflowColumn = 28; // WPF units, right of the stage
    private const double BubbleBand = 44;     // WPF units, above the picture
    private const double RightMargin = 6;     // WPF units, from the work area's right edge

    private static readonly SolidColorBrush BubbleFill = Frozen(Color.FromRgb(0xfa, 0xf6, 0xee));
    private static readonly SolidColorBrush BubbleLine = Frozen(Color.FromRgb(0x7a, 0x3b, 0x28));
    private static readonly SolidColorBrush TitleInk = Frozen(Color.FromRgb(0x2b, 0x23, 0x20));
    private static readonly SolidColorBrush NameInk = Frozen(Color.FromRgb(0x6b, 0x5a, 0x52));
    private static readonly SolidColorBrush HitFill = Frozen(Color.FromArgb(0x01, 0, 0, 0));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly SessionRegistry _registry;
    private readonly Action<SessionInfo> _onJump;
    private readonly int _scale;
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _clock = new();
    private readonly PetStage _stage = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly Dictionary<string, PetView> _views = new();
    private readonly TextBlock _overflow;

    private PetSheet? _sheet;
    private readonly Dictionary<string, BitmapSource[]> _frames = new();
    private double _lastMs;
    private double _lastSlowMs;
    private double _dpi = 1.0;
    private int _hPx;
    private int _overflowCount;

    /// <summary>Why the pet pictures could not be loaded (null when they were).</summary>
    public string? LoadError { get; private set; }

    public PetBarWindow(SessionRegistry registry, Action<SessionInfo> onJump, int petScale = AppConfig.DefaultPetScale)
    {
        InitializeComponent();
        _registry = registry;
        _onJump = onJump;
        _scale = Math.Clamp(petScale, 1, 6);

        _overflow = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = BubbleLine,
            Background = HitFill,
            Width = OverflowColumn,
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Stage.Children.Add(_overflow);

        LoadSheet();

        SourceInitialized += OnSrc;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += OnTick;
    }

    private void LoadSheet()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "assets", "pet");
            var sheet = PetSheet.Load(Path.Combine(dir, "pet.json"));
            var loaded = new Dictionary<string, BitmapSource[]>();
            foreach (var (name, anim) in sheet.Anims)
            {
                var png = new BitmapImage();
                png.BeginInit();
                png.CacheOption = BitmapCacheOption.OnLoad;
                png.UriSource = new Uri(Path.GetFullPath(Path.Combine(dir, anim.File)), UriKind.Absolute);
                png.EndInit();
                png.Freeze();

                int n = anim.Ms.Count;
                if (png.PixelWidth != n * sheet.Frame || png.PixelHeight != sheet.Frame)
                    throw new InvalidDataException(anim.File + " の大きさが合いません: " +
                        png.PixelWidth + "x" + png.PixelHeight + " (必要: " + (n * sheet.Frame) + "x" + sheet.Frame + ")");

                var cells = new BitmapSource[n];
                for (int i = 0; i < n; i++)
                {
                    var cell = new CroppedBitmap(png, new Int32Rect(i * sheet.Frame, 0, sheet.Frame, sheet.Frame));
                    cell.Freeze();
                    cells[i] = cell;
                }
                loaded[name] = cells;
            }
            foreach (var kv in loaded) _frames[kv.Key] = kv.Value;
            _sheet = sheet;
        }
        catch (Exception ex)
        {
            _sheet = null;
            _frames.Clear();
            LoadError = ex.GetType().Name + ": " + ex.Message;
            OrcaJump.Log("pet pictures could not be loaded: " + LoadError);
        }
    }

    private void OnSrc(object? sender, EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // NOACTIVATE + TOOLWINDOW. Not TRANSPARENT: the pets themselves
        // must receive clicks (transparent pixels already let clicks through).
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Relayout();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Relayout();
        if (_tick.IsEnabled) Draw();
    }

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }
        if (_sheet == null) return; // pictures failed to load: stay hidden (see LoadError)

        var ordered = _registry.Snapshot();
        var (displayed, overflow) = PetSelection.Select(ordered);

        _sessions.Clear();
        foreach (var s in ordered) _sessions[s.Id] = s;
        _stage.Sync(displayed);

        _overflowCount = overflow.Count;
        if (overflow.Count > 0)
        {
            var names = "";
            foreach (var s in overflow) names += "\n" + s.DisplayName + "（" + StatusText(s.Status) + "）";
            _overflow.Text = "+" + overflow.Count;
            _overflow.ToolTip = "表示枠（5体）に入っていないセッション:" + names;
        }

        if (_stage.Actors.Count == 0)
        {
            _tick.Stop();
            _clock.Stop();
            ClearViews();
            Hide();
            return;
        }

        Relayout();
        if (!IsVisible) Show();
        if (!_tick.IsEnabled)
        {
            _clock.Restart();
            _lastMs = 0;
            _lastSlowMs = 0;
            _tick.Start();
        }
        Draw();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = now - _lastMs;
        _lastMs = now;
        _stage.Advance(dt);

        if (now - _lastSlowMs >= 1000)
        {
            _lastSlowMs = now;
            Relayout(); // follows the taskbar if it moved
            foreach (var v in _views.Values)
                if (_sessions.TryGetValue(v.SessionId, out var s)) v.TipText.Text = TooltipFor(s);
        }
        Draw();
    }

    /// <summary>Size and place the window: bottom-right of the work area, on whole screen pixels.</summary>
    private void Relayout()
    {
        var sheet = _sheet;
        if (sheet == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1.0;
        _dpi = dpi;

        int k = _scale;
        int wPx = PetStage.WidthDots * k + (int)Math.Round(OverflowColumn * dpi);
        int hPx = sheet.Frame * k + (int)Math.Round(BubbleBand * dpi);
        _hPx = hPx;
        Width = wPx / dpi;
        Height = hPx / dpi;

        var wa = SystemParameters.WorkArea;
        int leftPx = (int)Math.Round(wa.Right * dpi) - (int)Math.Round(RightMargin * dpi) - wPx;
        int topPx = (int)Math.Round(wa.Bottom * dpi) - hPx; // bottom edge = top edge of the taskbar
        Left = leftPx / dpi;
        Top = topPx / dpi;
    }

    private void ClearViews()
    {
        foreach (var v in _views.Values) v.RemoveFrom(Stage);
        _views.Clear();
        _overflow.Visibility = Visibility.Collapsed;
    }

    /// <summary>Put every pet where the stage says it is, in whole screen pixels.</summary>
    private void Draw()
    {
        var sheet = _sheet;
        if (sheet == null) return;
        double dpi = _dpi;
        int k = _scale;

        // Drop views of pets that left.
        List<string>? gone = null;
        foreach (var id in _views.Keys)
        {
            bool alive = false;
            foreach (var a in _stage.Actors) if (a.SessionId == id) { alive = true; break; }
            if (!alive) (gone ??= new List<string>()).Add(id);
        }
        if (gone != null)
            foreach (var id in gone) { _views[id].RemoveFrom(Stage); _views.Remove(id); }

        double framePx = sheet.Frame * k;
        int topPx = _hPx - (sheet.Ground + 1) * k; // the ground row's lower edge = the window's lower edge

        foreach (var a in _stage.Actors)
        {
            if (!_sessions.TryGetValue(a.SessionId, out var session)) continue;
            if (!_views.TryGetValue(a.SessionId, out var v))
            {
                v = new PetView(a.SessionId, framePx / dpi, this);
                v.TipText.Text = TooltipFor(session);
                v.AddTo(Stage);
                _views[a.SessionId] = v;
            }
            v.Img.Width = framePx / dpi; // dpi may have changed
            v.Img.Height = framePx / dpi;

            var cells = _frames[a.Anim];
            int fi = Math.Min(sheet.Anims[a.Anim].FrameAt(a.AnimMs), cells.Length - 1);
            if (!ReferenceEquals(v.Img.Source, cells[fi])) v.Img.Source = cells[fi];
            v.SetFlip(a.FacingLeft);

            int leftPx = (int)Math.Round((a.X - sheet.Frame / 2.0) * k);
            Canvas.SetLeft(v.Img, leftPx / dpi);
            Canvas.SetTop(v.Img, topPx / dpi);

            // The hit box mirrors with the picture.
            int hitX = a.FacingLeft ? sheet.Frame - sheet.Hit.X - sheet.Hit.W : sheet.Hit.X;
            v.Hit.Width = sheet.Hit.W * k / dpi;
            v.Hit.Height = sheet.Hit.H * k / dpi;
            Canvas.SetLeft(v.Hit, (leftPx + hitX * k) / dpi);
            Canvas.SetTop(v.Hit, (topPx + sheet.Hit.Y * k) / dpi);

            bool wantsBubble = a.AtFront &&
                (a.Status == SessionStatus.Asking || a.Status == SessionStatus.DoneUnseen);
            if (wantsBubble)
            {
                v.SetBubble(a.Status == SessionStatus.Asking ? "聞きたいことがある！" : "終わったよー！",
                    session.DisplayName);
                var size = v.BubbleSize;
                double centre = (leftPx + framePx / 2.0) / dpi;
                double bx = Math.Max(0, Math.Min(centre - size.Width / 2, Width - size.Width));
                double by = Math.Max(0, topPx / dpi - size.Height);
                Canvas.SetLeft(v.Bubble, Math.Round(bx * dpi) / dpi);
                Canvas.SetTop(v.Bubble, Math.Round(by * dpi) / dpi);
            }
            else
            {
                v.Bubble.Visibility = Visibility.Collapsed;
            }
        }

        if (_overflowCount > 0)
        {
            Canvas.SetLeft(_overflow, PetStage.WidthDots * k / dpi);
            Canvas.SetTop(_overflow, topPx / dpi + (sheet.Hit.Y + sheet.Hit.H / 2.0) * k / dpi - 12);
            _overflow.Visibility = Visibility.Visible;
        }
        else
        {
            _overflow.Visibility = Visibility.Collapsed;
        }
    }

    private void JumpTo(string id)
    {
        if (_sessions.TryGetValue(id, out var s)) _onJump(s);
    }

    private static string StatusText(SessionStatus st) => st switch
    {
        SessionStatus.Asking => "質問待ち",
        SessionStatus.DoneUnseen => "完了（未確認）",
        SessionStatus.DoneSeen => "完了",
        _ => "実行中",
    };

    private static string TooltipFor(SessionInfo s)
    {
        var elapsed = DateTimeOffset.Now - s.StartedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? (int)elapsed.TotalHours + "時間" + elapsed.Minutes + "分"
            : elapsed.Minutes + "分" + elapsed.Seconds + "秒";
        var path = string.IsNullOrWhiteSpace(s.ProjectPath) ? s.DisplayName : s.ProjectPath;
        return path + "\n状態: " + StatusText(s.Status) + "\n経過: " + elapsedText + "\nクリックでこのセッションへ／右クリックで消す";
    }

    /// <summary>The visuals of one pet: picture, hit box, speech bubble.</summary>
    private sealed class PetView
    {
        private static readonly ScaleTransform Flip = MakeFlip();
        private readonly TextBlock _title;
        private readonly TextBlock _name;
        private string _bubbleKey = "";
        private bool _flipped;

        public PetView(string sessionId, double frameUnits, PetBarWindow owner)
        {
            SessionId = sessionId;

            Img = new Image
            {
                Width = frameUnits,
                Height = frameUnits,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            RenderOptions.SetBitmapScalingMode(Img, BitmapScalingMode.NearestNeighbor);

            TipText = new TextBlock();
            Hit = new Rectangle
            {
                Fill = HitFill, // not fully transparent, so clicks on the pet's empty dots do not fall through
                Cursor = Cursors.Hand,
                ToolTip = new ToolTip { Content = TipText },
            };
            ToolTipService.SetShowDuration(Hit, 60000);
            Hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; owner.JumpTo(SessionId); };
            Hit.MouseRightButtonDown += (_, e) => { e.Handled = true; owner._registry.Dismiss(SessionId); };

            _title = new TextBlock { FontSize = 9, FontWeight = FontWeights.Bold, Foreground = TitleInk };
            _name = new TextBlock
            {
                FontSize = 8,
                Foreground = NameInk,
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var text = new StackPanel { Orientation = Orientation.Vertical };
            text.Children.Add(_title);
            text.Children.Add(_name);
            Bubble = new Border
            {
                Background = BubbleFill,
                BorderBrush = BubbleLine,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5, 2, 5, 2),
                Cursor = Cursors.Hand,
                Child = text,
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(Bubble, 100);
            Bubble.MouseLeftButtonDown += (_, e) => { e.Handled = true; owner.JumpTo(SessionId); };
        }

        public string SessionId { get; }
        public Image Img { get; }
        public Rectangle Hit { get; }
        public Border Bubble { get; }
        public TextBlock TipText { get; }
        public Size BubbleSize { get; private set; }

        private static ScaleTransform MakeFlip()
        {
            var t = new ScaleTransform(-1, 1);
            t.Freeze();
            return t;
        }

        public void SetFlip(bool left)
        {
            if (left == _flipped) return;
            _flipped = left;
            Img.RenderTransform = left ? Flip : Transform.Identity;
        }

        /// <summary>Show the bubble with these two lines (measured again only when the text changes).</summary>
        public void SetBubble(string title, string name)
        {
            // A collapsed element measures as 0, so make it visible first.
            Bubble.Visibility = Visibility.Visible;
            var key = title + "\n" + name;
            if (key == _bubbleKey) return;
            _bubbleKey = key;
            _title.Text = title;
            _name.Text = name;
            Bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            BubbleSize = Bubble.DesiredSize;
        }

        public void AddTo(Canvas c)
        {
            c.Children.Add(Img);
            c.Children.Add(Hit);
            c.Children.Add(Bubble);
        }

        public void RemoveFrom(Canvas c)
        {
            c.Children.Remove(Img);
            c.Children.Remove(Hit);
            c.Children.Remove(Bubble);
        }
    }
}

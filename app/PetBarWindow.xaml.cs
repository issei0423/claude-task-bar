using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeTaskBar;

/// <summary>
/// The pet bar: one pet per session, at most PetSelection.MaxDisplayed
/// pets on screen. Pets are a view over the SessionRegistry — clicking a
/// pet jumps to its session, the small x only removes the pet.
/// The window never activates, so it cannot steal focus from the user's
/// current work (the old overlay called Activate() on every event).
/// </summary>
public partial class PetBarWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly SessionRegistry _registry;
    private readonly Action<SessionInfo> _onJump;
    private readonly DispatcherTimer _tick;

    public PetBarWindow(SessionRegistry registry, Action<SessionInfo> onJump)
    {
        InitializeComponent();
        _registry = registry;
        _onJump = onJump;
        SourceInitialized += OnSrc;
        // Keep elapsed times and priority order fresh without any event.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tick.Tick += (_, __) => Refresh();
        _tick.Start();
    }

    private void OnSrc(object? sender, EventArgs e)
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // NOACTIVATE + TOOLWINDOW. Not TRANSPARENT: the pets themselves
        // must receive clicks (the bar's transparent gaps are outside the
        // pets because the window sizes to its content).
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }

        var ordered = _registry.Snapshot();
        var (displayed, overflow) = PetSelection.Select(ordered);

        Bar.Children.Clear();
        foreach (var s in displayed) Bar.Children.Add(BuildPet(s));
        if (overflow.Count > 0) Bar.Children.Add(BuildOverflow(overflow));

        if (ordered.Count == 0)
        {
            Hide();
            return;
        }
        PositionBottomRight();
        if (!IsVisible) Show();
        // SizeToContent settles after the first layout pass; re-anchor then.
        Dispatcher.BeginInvoke(new Action(PositionBottomRight), DispatcherPriority.Loaded);
    }

    private FrameworkElement BuildOverflow(System.Collections.Generic.IReadOnlyList<SessionInfo> overflow)
    {
        var names = "";
        foreach (var s in overflow) names += "\n" + s.DisplayName + "（" + StatusText(s.Status) + "）";
        return new TextBlock
        {
            Text = "+" + overflow.Count,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7a, 0x3b, 0x28)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 2, 18),
            ToolTip = "表示枠（5体）に入っていないセッション:" + names,
        };
    }

    private static string StatusText(SessionStatus st) => st switch
    {
        SessionStatus.Asking => "質問待ち",
        SessionStatus.DoneUnseen => "完了（未確認）",
        SessionStatus.DoneSeen => "完了",
        _ => "実行中",
    };

    private static Color StatusColor(SessionStatus st) => st switch
    {
        SessionStatus.Asking => Color.FromRgb(0xd9, 0x77, 0x06),
        SessionStatus.DoneUnseen => Color.FromRgb(0x16, 0xa3, 0x4a),
        SessionStatus.DoneSeen => Color.FromRgb(0x9c, 0xa3, 0xaf),
        _ => Color.FromRgb(0x3b, 0x82, 0xf6),
    };

    private FrameworkElement BuildPet(SessionInfo s)
    {
        var root = new Grid { Width = 92, Margin = new Thickness(2, 0, 2, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Speech bubble: only when the pet wants the user's attention.
        string bubbleText = s.Status switch
        {
            SessionStatus.Asking => "聞きたいことがある！",
            SessionStatus.DoneUnseen => "終わったよー！",
            _ => "",
        };
        if (bubbleText.Length > 0)
        {
            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xfa, 0xf6, 0xee)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7a, 0x3b, 0x28)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5, 2, 5, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = bubbleText,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2b, 0x23, 0x20)),
                },
            };
            Grid.SetRow(bubble, 0);
            root.Children.Add(bubble);
        }

        // Mascot image (click = jump to that session).
        var img = new Image
        {
            Width = 62,
            Height = 62,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Source = MascotImages.Load(s.Surface),
            ToolTip = TooltipFor(s),
        };
        img.MouseLeftButtonDown += (_, __) => _onJump(s);
        Grid.SetRow(img, 1);
        root.Children.Add(img);

        // Dismiss x: removes only the pet, never jumps.
        var close = new TextBlock
        {
            Text = "×",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7a, 0x3b, 0x28)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "このペットを消す（セッション自体は終わりません）",
        };
        close.MouseLeftButtonDown += (_, __) => _registry.Dismiss(s.Id);
        Grid.SetRow(close, 1);
        root.Children.Add(close);

        // Name + status dot underneath.
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(StatusColor(s.Status)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        label.Children.Add(new TextBlock
        {
            Text = s.DisplayName,
            FontSize = 10,
            MaxWidth = 78,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2b, 0x23, 0x20)),
            ToolTip = TooltipFor(s),
        });
        Grid.SetRow(label, 2);
        root.Children.Add(label);

        return root;
    }

    private static string TooltipFor(SessionInfo s)
    {
        var elapsed = DateTimeOffset.Now - s.StartedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? (int)elapsed.TotalHours + "時間" + elapsed.Minutes + "分"
            : elapsed.Minutes + "分" + elapsed.Seconds + "秒";
        var path = string.IsNullOrWhiteSpace(s.ProjectPath) ? s.DisplayName : s.ProjectPath;
        return path + "\n状態: " + StatusText(s.Status) + "\n経過: " + elapsedText + "\nクリックでこのセッションへ";
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        // Window sizes to content, so measure first when possible.
        Left = wa.Right - ActualWidth - 6;
        if (ActualWidth <= 0) Left = wa.Right - 500;
        Top = wa.Bottom - ActualHeight - 6;
        if (ActualHeight <= 0) Top = wa.Bottom - 130;
    }
}

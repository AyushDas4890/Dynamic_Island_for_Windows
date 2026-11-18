using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// HeyClicky-style cursor buddy: a small glowing pointer that rides next to the mouse with a caption
/// bubble (listening level, thinking, what Jarvis says), and can fly across the screen to point at
/// something. Full-screen, transparent and click-through so it never gets in the way.
/// </summary>
public sealed class BuddyWindow : Window
{
    private static readonly Color Accent = Color.FromRgb(0x3C, 0xB4, 0xFF);
    private readonly Canvas _root = new();
    private readonly Canvas _buddy = new() { Width = 0, Height = 0 };
    private readonly Border _bubble;
    private readonly TextBlock _caption = new() { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    private readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Rectangle _ring = new() { Width = 56, Height = 56, RadiusX = 28, RadiusY = 28, StrokeThickness = 3, Opacity = 0, IsHitTestVisible = false };
    private readonly Border _pointLabel;
    private readonly TextBlock _pointText = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };

    private Point _pos, _target;
    private Point? _pointAt;
    private DateTime _pointUntil;
    private readonly System.Windows.Threading.DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(5) };

    public BuddyWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        Content = _root;

        // pointer: a soft blue arrowhead with glow
        var arrow = new Path
        {
            Data = Geometry.Parse("M0,0 L15,6 L7,8 L5,16 Z"),
            Fill = new SolidColorBrush(Accent), Stroke = Brushes.White, StrokeThickness = 1.2,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.9 },
        };
        _buddy.Children.Add(arrow);

        for (int i = 0; i < 4; i++)
            _bars.Children.Add(new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Accent), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_bars);
        row.Children.Add(_caption);
        _bubble = new Border
        {
            Child = row, Padding = new Thickness(12, 7, 12, 8), CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x12, 0x14, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
        };
        Canvas.SetLeft(_bubble, 20); Canvas.SetTop(_bubble, 16);
        _buddy.Children.Add(_bubble);

        _ring.Stroke = new SolidColorBrush(Accent);
        _ring.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Accent, BlurRadius = 20, ShadowDepth = 0 };
        _pointLabel = new Border
        {
            Child = _pointText, Padding = new Thickness(10, 5, 10, 6), CornerRadius = new CornerRadius(10), Opacity = 0,
            Background = new SolidColorBrush(Accent),
        };
        _root.Children.Add(_ring);
        _root.Children.Add(_pointLabel);
        _root.Children.Add(_buddy);
        _root.Opacity = 0;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80 | 0x08000000 | 0x80000); // transparent, toolwindow, noactivate, layered
        };
        _hide.Tick += (_, _) => { _hide.Stop(); Fade(0); };
        CompositionTarget.Rendering += Tick;
    }

    // ---- states ----

    public void Listening() { ShowBuddy(); _bars.Visibility = Visibility.Visible; _caption.Text = "listening…"; }
    public void Level(double v)
    {
        for (int i = 0; i < _bars.Children.Count; i++)
            ((Rectangle)_bars.Children[i]).Height = 4 + 14 * Math.Clamp(v * (0.6 + 0.4 * Math.Sin(Environment.TickCount / 90.0 + i * 1.7)), 0, 1);
    }
    public void Thinking(string heard) { ShowBuddy(); _bars.Visibility = Visibility.Collapsed; _caption.Text = "“" + heard + "”  ·  thinking…"; }
    public void Saying(string text) { ShowBuddy(); _bars.Visibility = Visibility.Collapsed; _caption.Text = text.Length > 160 ? text[..157] + "…" : text; }
    public void Done(double after = 4) { _hide.Interval = TimeSpan.FromSeconds(after); _hide.Stop(); _hide.Start(); }
    public void HideNow() { _hide.Stop(); Fade(0); _pointAt = null; }

    /// <summary>Fly to a point on screen (physical pixels) and circle it with a label for a few seconds.</summary>
    public void PointAt(int px, int py, string label, System.Drawing.Rectangle? element = null)
    {
        ShowBuddy();
        Point p;
        if (element is { } r)
        {
            // Snap: outline the real control, like HeyClicky's highlight.
            var tl = ToDip(r.Left, r.Top); var br = ToDip(r.Right, r.Bottom);
            const double pad = 6;
            _ring.Width = br.X - tl.X + pad * 2; _ring.Height = br.Y - tl.Y + pad * 2;
            _ring.RadiusX = _ring.RadiusY = Math.Min(12, _ring.Height / 2);
            Canvas.SetLeft(_ring, tl.X - pad - Left); Canvas.SetTop(_ring, tl.Y - pad - Top);
            p = new Point((tl.X + br.X) / 2, br.Y);
            Canvas.SetLeft(_pointLabel, br.X + pad + 8 - Left); Canvas.SetTop(_pointLabel, (tl.Y + br.Y) / 2 - 14 - Top);
        }
        else
        {
            p = ToDip(px, py);
            _ring.Width = _ring.Height = 56; _ring.RadiusX = _ring.RadiusY = 28;
            Canvas.SetLeft(_ring, p.X - 28 - Left); Canvas.SetTop(_ring, p.Y - 28 - Top);
            Canvas.SetLeft(_pointLabel, p.X + 34 - Left); Canvas.SetTop(_pointLabel, p.Y - 14 - Top);
        }
        _pointAt = p;
        _pointUntil = DateTime.Now.AddSeconds(7);
        _pointText.Text = label;
        _pointLabel.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var pulse = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(5) };
        _ring.BeginAnimation(OpacityProperty, pulse);
        _pointLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        Done(8);
    }

    private void ShowBuddy()
    {
        if (!IsVisible) Show();
        _hide.Stop();
        if (_root.Opacity < 1) { _pos = Cursor(); Fade(1); }
    }

    private void Fade(double to) => _root.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 160 : 400)));

    // ---- motion: critically damped follow, like a buddy trailing the cursor ----

    private void Tick(object? s, EventArgs e)
    {
        if (_root.Opacity <= 0 && _pointAt == null) return;
        if (_pointAt != null && DateTime.Now > _pointUntil)
        {
            _pointAt = null;
            _ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            _pointLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        }
        _target = _pointAt is { } pa ? new Point(pa.X - 4, pa.Y - 4) : Offset(Cursor(), 16, 18);
        double k = _pointAt != null ? 0.12 : 0.28;
        _pos = new Point(_pos.X + (_target.X - _pos.X) * k, _pos.Y + (_target.Y - _pos.Y) * k);
        Canvas.SetLeft(_buddy, _pos.X - Left);
        Canvas.SetTop(_buddy, _pos.Y - Top);
    }

    private static Point Offset(Point p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    private Point Cursor()
    {
        GetCursorPos(out var c);
        return ToDip(c.X, c.Y);
    }

    private Point ToDip(int x, int y)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}

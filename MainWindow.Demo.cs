using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// `DynamicIsland.exe --screenshots <dir>` renders every view with made-up demo content to
/// transparent PNGs (used for the README; no personal data from the real machine).
/// </summary>
public partial class MainWindow
{
    internal void RenderDemo(string dir)
    {
        Directory.CreateDirectory(dir);
        var accent = Color.FromRgb(0xFF, 0x7A, 0x59);
        var accentBrush = new SolidColorBrush(accent);

        // Media
        ExpandedArt.Background = new ImageBrush(DemoArt()) { Stretch = Stretch.UniformToFill };
        ExpandedTitle.Text = "Neon Skyline";
        ExpandedArtist.Text = "The Night Drive";
        ProgressRow.Visibility = Visibility.Visible;
        ElapsedText.Text = "1:24";
        RemainingText.Text = "-2:31";
        ProgressFill.Width = 236 * 0.36; // track is 236 px wide in the 360 px card
        PlayIcon.Visibility = Visibility.Collapsed;
        PauseIcon.Visibility = Visibility.Visible;
        double[] bars = { 0.55, 1.0, 0.4, 0.8, 0.6 };
        int b = 0;
        foreach (Rectangle bar in ExpandedBars.Children) { bar.Fill = accentBrush; ((ScaleTransform)bar.RenderTransform).ScaleY = bars[b++]; }
        Render(View.MediaExpanded, dir, "now-playing");

        // Notification
        NoticeApp.Text = "Messages";
        NoticeTitle.Text = "Riya";
        NoticeBody.Text = "Are we still on for dinner at 7?";
        NoticeBody.Visibility = Visibility.Visible;
        NoticeIcon.Background = new LinearGradientBrush(Color.FromRgb(0x5E, 0xE0, 0x7A), Color.FromRgb(0x1F, 0xB0, 0x4B), 90);
        NoticeGlyph.Text = ""; // chat bubble
        NoticeGlyph.Visibility = Visibility.Visible;
        Render(View.Notice, dir, "notification");

        // Pills
        PillGlyph.Text = ""; PillGlyph.Foreground = Brushes.White;
        PillTitle.Text = "Copied"; PillTitle.Foreground = Brushes.White;
        PillValue.Text = "Meet at 6 pm near the library";
        Render(View.Pill, dir, "clipboard");

        var green = (Brush)FindResource("ChargeGreen");
        PillGlyph.Text = ""; PillGlyph.Foreground = green;
        PillTitle.Text = "Connected"; PillTitle.Foreground = green;
        PillValue.Text = "Wireless Earbuds";
        Render(View.Pill, dir, "bluetooth");

        ChargePercent.Text = "82%";
        ChargeFill.Width = 24 * 0.82;
        Render(View.Charging, dir, "charging");

        // Home + timer
        ClockText.Text = "9:41";
        DateText.Text = "Wednesday, 18 November";
        Render(View.Home, dir, "home");

        TimerCaption.Text = "Timer";
        TimerBig.Text = "4:32";
        TimerControls.Visibility = Visibility.Visible;
        Render(View.TimerExpanded, dir, "timer");

        // Resting slivers
        MusicDot.Fill = accentBrush; MusicDot.Visibility = Visibility.Visible;
        TimerDot.Visibility = Visibility.Hidden;
        CamDot.Visibility = Visibility.Collapsed; MicDot.Visibility = Visibility.Collapsed;
        Render(View.Sliver, dir, "sliver-music");
        CamDot.Visibility = Visibility.Visible;
        Render(View.Sliver, dir, "sliver-camera");
        CamDot.Visibility = Visibility.Collapsed; MicDot.Visibility = Visibility.Visible;
        Render(View.Sliver, dir, "sliver-mic");
    }

    private void Render(View view, string dir, string name)
    {
        var target = ViewFor(view);
        foreach (var v in Views)
        {
            bool on = v == target;
            v.BeginAnimation(OpacityProperty, null);
            v.Opacity = on ? 1 : 0;
            v.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            v.CacheMode = null;
            v.RenderTransform = new ScaleTransform(1, 1);
        }

        _peek = false;
        var (w, h, r) = SizeOf(view);
        Island.Width = w;
        Island.Height = h;
        Island.CornerRadius = new CornerRadius(r);
        IslandScale.ScaleX = IslandScale.ScaleY = 1;
        Island.UpdateLayout();

        const double scale = 3; // crisp on high-DPI screens
        const double margin = 6;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(Island), null, new Rect(margin, margin, w, h));
        var bmp = new RenderTargetBitmap((int)((w + 2 * margin) * scale), (int)((h + 2 * margin) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(System.IO.Path.Combine(dir, name + ".png"));
        png.Save(fs);
    }

    private static BitmapSource DemoArt()
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5F, 0x6D), Color.FromRgb(0x5B, 0x2A, 0x86), 45), null, new Rect(0, 0, 120, 120));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xC3, 0x71)), null, new Point(78, 46), 22, 22);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x20)), null, new Rect(0, 82, 120, 38));
        }
        var bmp = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>The hover dashboard (Claude Code card + tiles) and the state glow around the notch.</summary>
public partial class MainWindow
{
    private static readonly Color Working = Color.FromRgb(0xFF, 0x9A, 0x3C);
    private static readonly Color DoneGreen = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color AskBlue = Color.FromRgb(0x3C, 0xB4, 0xFF);
    private static readonly Color IdleBlue = Color.FromRgb(0x4A, 0x6B, 0xFF);
    private static readonly Color LovePink = Color.FromRgb(0xFF, 0x5C, 0x93);

    private AgentEvent? _lastAgent;
    private DateTime _doneUntil = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _projects = new();
    private int _focusMinutes = 25;

    private Mascot[] Mascots => new[] { NotchMascot, DashMascot, AgentMascot, SettingsMascot };

    private void InitDash()
    {
        Tap(TbHome, () => ((App)Application.Current).OpenHome(), hoverFill: true);
        Tap(TbSettings, () => ((App)Application.Current).OpenHome("island"), hoverFill: true);
        Tap(TileSettings, () => ((App)Application.Current).OpenHome("claude"));
        Tap(TbAsk, () => BeginAsk(AskClaude.ForegroundWindow()), hoverFill: true);
        Tap(TbCollapse, () => { _expand = Expand.None; Refresh(); }, hoverFill: true);
        Tap(TbSound, () => { AppSettings.Current.Sounds = !AppSettings.Current.Sounds; AppSettings.Current.Save(); UpdateDash(); }, hoverFill: true);
        Tap(TileMusic, () => _ = _media.TogglePlayPauseAsync());
        Tap(TileTimer, () => { if (TimerActive) ToggleTimerPause(); else StartTimer(_focusMinutes); });
        // Drag the robot onto any window to ask about it; click pokes it; double-click: in love.
        DashMascotHost.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { foreach (var m in Mascots) m.SetMood(Mood.Love); SetGlow(LovePink); return; }
            StartMascotDrag(DashMascot, askOnClick: false);
        };
        DashSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>Called for every Claude Code event, before the island reacts to it.</summary>
    private void RecordAgent(AgentEvent e)
    {
        _lastAgent = e;
        _projects[e.Project] = DateTime.Now;
        if (e.Kind == "done") _doneUntil = DateTime.Now.AddSeconds(20);
    }

    /// <summary>The island's state colour and the robot's mood follow what's happening.</summary>
    private (Color Color, Mood Mood) CurrentState()
    {
        if (_approvals.Count > 0) return (AskBlue, Mood.Curious);
        if (AgentActive) return (Working, Mood.Focused);
        if (DateTime.Now < _doneUntil) return (DoneGreen, Mood.Happy);
        return (IdleBlue, Mood.Idle);
    }

    private void SetGlow(Color c)
    {
        GlowShadow.Color = c;
        Island.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        foreach (var m in Mascots) m.SetGlow(c);
    }

    private void UpdateDash()
    {
        var (color, mood) = CurrentState();
        SetGlow(color);
        // Strong glow while open or something is happening; a faint one at rest.
        bool open = _expand != Expand.None || _alert != Alert.None || _approvals.Count > 0 || Asking;
        Glow.Opacity = open ? 1 : mood == Mood.Idle ? 0.35 : 0.8;
        foreach (var m in new[] { NotchMascot, DashMascot, SettingsMascot }) if (m.Mood != Mood.Love && m.Mood != Mood.Dizzy) m.SetMood(mood);

        // Claude Code card
        var a = _lastAgent;
        int active = _projects.Count(p => DateTime.Now - p.Value < TimeSpan.FromMinutes(10));
        DashStateDot.Fill = new SolidColorBrush(color);
        DashProject.Text = a?.Project ?? "Claude Code";
        DashAgentName.Visibility = a == null ? Visibility.Collapsed : Visibility.Visible;
        DashCount.Text = active > 1 ? $"{active} sessions" : "";
        bool working = mood == Mood.Focused;
        DashStatusGlyph.Text = working ? "" : mood == Mood.Happy ? "" : mood == Mood.Curious ? "" : "";
        DashStatusGlyph.Foreground = new SolidColorBrush(color);
        DashStatusGlyph.RenderTransform = working ? DashStatusGlyph.RenderTransform : null;
        DashStatus.Foreground = mood == Mood.Happy ? new SolidColorBrush(DoneGreen) : Brushes.White;
        DashStatus.Text = a == null ? "Waiting for Claude Code"
            : _approvals.Count > 0 ? "Needs your OK"
            : a.Kind == "done" ? "Done"
            : (a.Activity.TrimEnd('…') + " " + a.Detail).Trim();

        string diff = a?.Kind == "done" ? a.Detail : a?.Diff ?? "";
        DashFile.Text = a == null ? "Start Claude Code to see it here" : a.Kind == "done" ? "" : a.Detail;
        var lines = diff.Split('\n');
        SetDiffLine(DashLineA, lines.ElementAtOrDefault(0) ?? "");
        SetDiffLine(DashLineB, lines.ElementAtOrDefault(1) ?? "");
        if (a?.Kind == "done") { DashLineA.TextWrapping = TextWrapping.Wrap; DashLineA.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCC, 0xD3)); DashLineA.Background = null; }

        // Music tile
        bool hasMusic = _media.HasSession && AppSettings.Current.ShowMusic;
        TileMusicTitle.Text = hasMusic && _media.Title.Length > 0 ? _media.Title : "Music";
        TileMusicSub.Text = hasMusic ? (_media.IsPlaying ? _media.Artist : "Paused · " + _media.Artist) : "Nothing playing";
        TileMusicArt.Background = _media.Art != null && hasMusic ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        TileMusicGlyph.Visibility = _media.Art != null && hasMusic ? Visibility.Collapsed : Visibility.Visible;

        // Focus timer tile
        TileTimerText.Text = TimerActive ? Format(TimerLeft()) : $"{_focusMinutes}:00";
        TileTimerSub.Text = _timerPausedLeft != null ? "Paused" : "Focus";
        double p = TimerActive && _timerTotal.TotalSeconds > 0 ? TimerLeft().TotalSeconds / _timerTotal.TotalSeconds : 1;
        TileTimerRing.Data = Arc(17, 17, 15.5, p);

        // Clock tile
        var now = DateTime.Now;
        TileClock.Text = now.ToString("t");
        TileDate.Text = now.ToString("ddd d MMM");
        TileDayFill.Width = 80 * now.TimeOfDay.TotalHours / 24;

        TbSoundGlyph.Text = AppSettings.Current.Sounds ? "" : "";
    }

    private static void SetDiffLine(System.Windows.Controls.TextBlock tb, string line)
    {
        tb.Text = line;
        tb.TextWrapping = TextWrapping.NoWrap;
        tb.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        (tb.Background, tb.Foreground) = line.StartsWith("- ")
            ? (new SolidColorBrush(Color.FromRgb(0x3A, 0x14, 0x18)), new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x86)))
            : line.StartsWith("+ ")
                ? (new SolidColorBrush(Color.FromRgb(0x0F, 0x2E, 0x1A)), new SolidColorBrush(Color.FromRgb(0x6B, 0xE3, 0x8E)))
                : ((Brush?)null, new SolidColorBrush(Color.FromRgb(0xC9, 0xCC, 0xD3)));
    }

    private static Geometry? Arc(double cx, double cy, double r, double p)
    {
        if (p >= 0.999) return new EllipseGeometry(new Point(cx, cy), r, r);
        if (p <= 0.001) return null;
        double a = p * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx, cy - r), false, false);
            ctx.ArcTo(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

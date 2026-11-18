using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    // What the main island is showing. Idle = empty sliver (or empty pill while peeking);
    // Sliver = resting sliver with activity dots.
    private enum View { Idle, Sliver, Home, Charging, Pill, Notice, MediaExpanded, TimerExpanded, Agent, Approve, Ask, Dash }
    private enum Expand { None, Main, Bubble }
    private enum Alert { None, Charging, TimerDone, Pill, Notice, Agent }

    private const double IslandTop = 0; // attached to the top edge, like a notch
    private const double CompactHeight = 34;
    private const double BubbleSize = 34;
    private const double BubbleGap = 8;

    // ---- motion ----
    private const double SliverTop = 0;
    // Size springs: damping 0.88 = a hint of elasticity without wobble; settles in ~0.6 s.
    private readonly Spring _w = new(72, 0.42, 0.88), _h = new(6, 0.42, 0.88), _r = new(3, 0.42, 0.88), _top = new(SliverTop, 0.42, 0.88);
    private readonly Spring _scale = new(1, response: 0.28, dampingRatio: 0.7);
    private readonly Spring _bubble = new(0, response: 0.42, dampingRatio: 0.74);
    private readonly Spring _gap = new(0, response: 0.42, dampingRatio: 0.74);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private bool _animating;
    private readonly bool _reduceMotion = !SystemParameters.ClientAreaAnimation;

    // ---- state ----
    private readonly MediaWatcher _media;
    private Expand _expand = Expand.None;
    private Alert _alert = Alert.None;
    private bool _peek;
    private View _shown = View.Idle;

    private DateTime? _timerEnd;
    private TimeSpan? _timerPausedLeft;
    private TimeSpan _timerTotal;
    private bool TimerActive => _timerEnd != null || _timerPausedLeft != null;

    private bool _wasOnAc;

    // ---- extra activities ----
    private readonly NotificationWatcher _notifications;
    private readonly ClipboardWatcher _clipboard = new();
    private readonly BluetoothWatcher _bluetooth;
    private readonly DispatcherTimer _privacyPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _camOn, _micOn;
    private readonly CameraMonitor _camera = new();

    private readonly DispatcherTimer _hoverIntent = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly DispatcherTimer _leaveGrace = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _alertTimer = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Expand _pendingExpand;

    private readonly List<Storyboard> _bars = new();
    private bool _barsRunning;
    private FrameworkElement? _pressed;

    private FrameworkElement[] Views => new FrameworkElement[]
        { SliverView, HomeView, ChargingView, PillView, NoticeView, MediaExpandedView, TimerExpandedView, AgentView, ApproveView, AskView, DashView };

    // Mouse wheel over the open island flips between the music card and the home card (clock, timers, mascot).
    private bool _preferHome;

    public MainWindow()
    {
        InitializeComponent();

        if (_reduceMotion)
            foreach (var s in new[] { _w, _h, _r, _top, _bubble, _gap, _scale }) s.Configure(0.3, 1.0);

        _media = new MediaWatcher(Dispatcher);
        _media.Changed += Refresh;

        foreach (var v in Views) PrepareView(v);
        SetupBars(ExpandedBars);

        Tap(Preset1, () => StartTimer(1));
        Tap(Preset5, () => StartTimer(5));
        Tap(Preset25, () => StartTimer(25));
        Tap(PrevButton, () => _ = _media.PreviousAsync(), hoverFill: true);
        Tap(PlayButton, () => _ = _media.TogglePlayPauseAsync(), hoverFill: true);
        Tap(NextButton, () => _ = _media.NextAsync(), hoverFill: true);
        Tap(TimerPauseButton, ToggleTimerPause);
        Tap(TimerCancelButton, CancelTimer);

        HitZone.MouseEnter += (_, _) => PointerEnter(Expand.Main);
        HitZone.MouseLeave += (_, _) => PointerLeave();
        Island.MouseEnter += (_, _) => PointerEnter(Expand.Main);
        Island.MouseLeave += (_, _) => PointerLeave();
        Island.MouseLeftButtonDown += Island_Down;
        Island.MouseLeftButtonUp += Island_Up;
        Island.MouseWheel += (_, e) =>
        {
            if (_expand != Expand.Main || Asking || _approvals.Count > 0) return;
            _preferHome = !_preferHome;
            Refresh();
            e.Handled = true;
        };
        Bubble.MouseEnter += (_, _) => PointerEnter(Expand.Bubble);
        Bubble.MouseLeave += (_, _) => PointerLeave();

        _hoverIntent.Tick += (_, _) => { _hoverIntent.Stop(); _expand = _pendingExpand; Refresh(); };
        _leaveGrace.Tick += (_, _) => { _leaveGrace.Stop(); _expand = Expand.None; _preferHome = false; Refresh(); };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); _alert = Alert.None; Refresh(); };
        _tick.Tick += (_, _) => OnTick();

        _notifications = new NotificationWatcher(Dispatcher);
        _notifications.Arrived += ShowNotice;
        _clipboard.Copied += what => { if (AppSettings.Current.ShowClipboard) ShowPill("î£ˆ", "Copied", what, Brushes.White); };
        _bluetooth = new BluetoothWatcher(Dispatcher);
        _bluetooth.Changed += (name, on) => { if (AppSettings.Current.ShowBluetooth)
            ShowPill("îœ‚", on ? "Connected" : "Disconnected", name, on ? (Brush)FindResource("ChargeGreen") : Brushes.White); };
        _privacyPoll.Tick += (_, _) => PollPrivacy();

        _wasOnAc = Power.IsOnAc();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        ApplyGeometry();
        Refresh();
        try { await _media.StartAsync(); }
        catch (Exception ex) { Debug.WriteLine("Media watcher failed: " + ex.Message); }

        // Each source is optional: one failing must not take the island down.
        try { _clipboard.Attach(this); } catch (Exception ex) { Debug.WriteLine("Clipboard: " + ex.Message); }
        try { _bluetooth.Start(); } catch (Exception ex) { Debug.WriteLine("Bluetooth: " + ex.Message); }
        try { _camera.Start(); } catch (Exception ex) { Debug.WriteLine("Camera monitor: " + ex.Message); }
        InitAgent();
        PollPrivacy();
        _privacyPoll.Start();
        string status;
        try { await _notifications.StartAsync(); status = _notifications.Status; }
        catch (Exception ex) { status = "error (" + ex.GetType().Name + ")"; }
        NotificationStatusItem.Header = status == "Allowed"
            ? "Notifications: on"
            : $"Notifications: {status} (Settings > Privacy > Notifications)";
        if (Perf) System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "island-perf.log"), "notifications=" + status + "\n");
    }

    private void PollPrivacy()
    {
        bool cam = _camera.InUse, mic = false;
        try { mic = MicrophoneMonitor.InUse(); }
        catch (Exception) { /* no capture devices / audio service restarting: no indicator */ }
        if (!AppSettings.Current.ShowPrivacyDots) cam = mic = false;
        _camOn = cam; _micOn = mic;
        Refresh();
    }

    private void ShowPill(string glyph, string title, string value, Brush accent)
    {
        PillGlyph.Text = glyph;
        PillGlyph.Foreground = accent;
        PillTitle.Text = title;
        PillTitle.Foreground = accent;
        PillValue.Text = value;
        ShowAlert(Alert.Pill, 2.2);
    }

    private void ShowNotice(NotificationWatcher.Notice n)
    {
        if (!AppSettings.Current.ShowNotifications) return;
        NoticeApp.Text = n.App;
        NoticeTitle.Text = n.Title;
        NoticeBody.Text = n.Body;
        NoticeBody.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeIcon.Background = n.Icon != null
            ? new ImageBrush(n.Icon) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("Fill");
        NoticeGlyph.Visibility = n.Icon != null ? Visibility.Collapsed : Visibility.Visible;
        ShowAlert(Alert.Notice, 5);
    }

    // ================= window chrome =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Tool window: no taskbar / Alt-Tab entry. No-activate: never steals focus from your app.
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    // ================= state â†’ layout =================

    private View ResolveMain()
    {
        // A pending decision from Claude Code and an open question box outrank everything else.
        if (_approvals.Count > 0) return View.Approve;
        if (Asking) return View.Ask;
        if (_alert == Alert.Agent) return View.Agent;
        if (_alert == Alert.Charging) return View.Charging;
        if (_alert == Alert.TimerDone) return View.TimerExpanded;
        if (_alert == Alert.Pill) return View.Pill;
        if (_alert == Alert.Notice) return View.Notice;

        bool music = _media.IsPlaying && AppSettings.Current.ShowMusic, timer = TimerActive;
        if (_expand == Expand.Bubble) return View.TimerExpanded;
        if (_expand == Expand.Main)
        {
            return _preferHome ? View.Home : View.Dash;
        }
        // Resting: a sliver out of the way. Hover nudges it (instant feedback), dwell opens the card.
        return View.Sliver; // the robot notch is always there
    }

    private (double w, double h, double r) SizeOf(View v) => v switch
    {
        View.Idle => _peek ? (104, 12, 6) : (72, 6, 3),
        View.Sliver => _peek ? (170, 34, 16) : (150, 30, 14),
        View.Dash => (720, 214, 26),
        View.Charging => (300, 32, 16),
        View.Pill => (300, 32, 16),
        View.Notice => (372, 72, 36),
        // Expanded cards: radius concentric with the 46px circles inset 23px â†’ pill ends.
        View.Home => (400, 76, 38),
        View.Agent => (380, 76, 38),
        View.Approve => (400, 156, 34),
        View.Ask => _ask is AskStage.Thinking or AskStage.Answer ? (420, 240, 30) : (420, 104, 34),
        View.TimerExpanded => (352, 76, 38),
        View.MediaExpanded => (360, ProgressRow.Visibility == Visibility.Visible ? 140 : 116, 30),
        _ => (126, CompactHeight, 17),
    };

    private void Refresh()
    {
        UpdateContent();

        var main = ResolveMain();
        bool bubble = _alert == Alert.None && main == View.MediaExpanded && _media.IsPlaying && TimerActive;

        var (w, h, r) = SizeOf(main);
        _w.Target = w; _h.Target = h; _r.Target = r;
        _top.Target = main is View.Sliver or View.Idle ? SliverTop : IslandTop;
        // Waveform only animates while it is on screen: no per-frame work in the sliver.
        SetBarsRunning(_media.IsPlaying && main == View.MediaExpanded);
        _bubble.Target = bubble ? BubbleSize : 0;
        _gap.Target = bubble ? BubbleGap : 0;
        StartFrames();

        if (main != _shown)
        {
            _shown = main;
            foreach (var v in Views)
            {
                bool on = ViewFor(main) == v;
                if (on) Materialize(v); else Dematerialize(v);
            }
        }
        bool needTick = TimerActive || _expand != Expand.None;
        if (needTick && !_tick.IsEnabled) _tick.Start();
        if (!needTick && _tick.IsEnabled) _tick.Stop();
    }

    private FrameworkElement? ViewFor(View v) => v switch
    {
        View.Sliver => SliverView,
        View.Dash => DashView,
        View.Home => HomeView,
        View.Agent => AgentView,
        View.Approve => ApproveView,
        View.Ask => AskView,
        View.Charging => ChargingView,
        View.Pill => PillView,
        View.Notice => NoticeView,
        View.MediaExpanded => MediaExpandedView,
        View.TimerExpanded => TimerExpandedView,
        _ => null,
    };

    private void UpdateContent()
    {
        // Media
        Brush artBrush = _media.Art != null
            ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("Fill");
        ExpandedArt.Background = artBrush;
        ExpandedTitle.Text = _media.Title.Length > 0 ? _media.Title : "Not playing";
        ExpandedArtist.Text = _media.Artist;
        PlayIcon.Visibility = _media.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        ProgressRow.Visibility = _media.Duration > TimeSpan.FromSeconds(1) ? Visibility.Visible : Visibility.Collapsed;

        var accent = new SolidColorBrush(_media.Accent);
        accent.Freeze();
        foreach (Rectangle bar in ExpandedBars.Children) bar.Fill = accent;
        MusicDot.Fill = accent;
        MusicDot.Visibility = _media.IsPlaying ? Visibility.Visible : Visibility.Hidden;
        TimerDot.Visibility = TimerActive ? Visibility.Visible : Visibility.Hidden;
        CamDot.Visibility = _camOn ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = _micOn ? Visibility.Visible : Visibility.Collapsed;
        AgentDot.Visibility = AgentActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateApproval();
        UpdateDash();

        // Timer
        if (_alert == Alert.TimerDone)
        {
            TimerCaption.Text = "Timer";
            TimerBig.Text = "Done";
            TimerControls.Visibility = Visibility.Collapsed;
        }
        else
        {
            TimerCaption.Text = _timerPausedLeft != null ? "Paused" : "Timer";
            TimerBig.Text = Format(TimerLeft());
            TimerControls.Visibility = Visibility.Visible;
            TimerPauseGlyph.Text = _timerPausedLeft != null ? "î¨" : "î©";
        }
        UpdateRing();

        // Home
        ClockText.Text = DateTime.Now.ToString("t");
        DateText.Text = DateTime.Now.ToString("dddd, d MMMM");

        UpdateProgress();
    }

    private void OnTick()
    {
        if (_timerEnd != null && DateTime.Now >= _timerEnd.Value)
        {
            _timerEnd = null;
            if (AppSettings.Current.Sounds) SystemSounds.Exclamation.Play();
            ShowAlert(Alert.TimerDone, 5);
            return;
        }
        UpdateContent();
    }

    // ================= motion: shape =================

    private void StartFrames()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _clock.Elapsed;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double rawDt = (now - _lastFrame).TotalSeconds;
        double dt = Math.Min(rawDt, 1.0 / 30);
        _lastFrame = now;
        if (Perf) _frameTimes.Add(rawDt * 1000);

        bool moving = _w.Step(dt) | _h.Step(dt) | _r.Step(dt) | _top.Step(dt) | _scale.Step(dt) | _bubble.Step(dt) | _gap.Step(dt);
        ApplyGeometry();

        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame; // settled: zero per-frame work while idle
            _animating = false;
            if (Perf && _frameTimes.Count > 3)
            {
                var f = _frameTimes.Skip(1).ToList();
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "island-perf.log"),
                    $"{_shown}: frames={f.Count} avg={f.Average():F1}ms max={f.Max():F1}ms over25={f.Count(x => x > 25)}\n");
            }
            _frameTimes.Clear();
        }
    }

    // Set DI_PERF=1 to log frame pacing of each animation to %TEMP%\island-perf.log.
    private static readonly bool Perf = Environment.GetEnvironmentVariable("DI_PERF") == "1";
    private readonly List<double> _frameTimes = new();

    private void ApplyGeometry()
    {
        double w = Math.Max(_w.Value, 1), h = Math.Max(_h.Value, 1);
        double bs = Math.Max(_bubble.Value, 0), gap = Math.Max(_gap.Value, 0);
        double total = w + (bs > 0.5 ? gap + bs : 0);
        double left = (Root.ActualWidth > 0 ? Root.ActualWidth : Width) / 2 - total / 2;

        Canvas.SetLeft(Island, left);
        Canvas.SetTop(Island, _top.Value);
        Island.Width = w;
        Island.Height = h;
        double rr = Math.Clamp(_r.Value, 0, Math.Min(w, h) / 2);
        Island.CornerRadius = new CornerRadius(0, 0, rr, rr);
        Canvas.SetLeft(Glow, left); Canvas.SetTop(Glow, _top.Value);
        Glow.Width = w; Glow.Height = h; Glow.CornerRadius = Island.CornerRadius;
        IslandScale.CenterX = w / 2;
        IslandScale.CenterY = 0;
        IslandScale.ScaleX = IslandScale.ScaleY = _scale.Value;

        Bubble.Visibility = bs > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(Bubble, left + w + gap);
        Canvas.SetTop(Bubble, IslandTop + (CompactHeight - bs) / 2);
        Bubble.Width = Bubble.Height = bs;
        Bubble.CornerRadius = new CornerRadius(bs / 2);
    }

    // ================= motion: content =================

    private static void PrepareView(FrameworkElement v)
    {
        v.Opacity = 0;
        v.Visibility = Visibility.Hidden;
        v.IsHitTestVisible = false;
        v.RenderTransformOrigin = new Point(0.5, 0); // grow down and out from the island's top
        v.RenderTransform = new ScaleTransform(0.92, 0.92);
    }

    /// <summary>
    /// Content arrives with the shape: fade + scale from the island's top, slightly after it starts growing.
    /// No blur: effects inside a layered (transparent) window are expensive and made the open stutter.
    /// The view is cached as a bitmap while it animates so each frame is a cheap texture scale.
    /// </summary>
    private void Materialize(FrameworkElement v)
    {
        v.Visibility = Visibility.Visible;
        v.IsHitTestVisible = true;
        var begin = TimeSpan.FromMilliseconds(_reduceMotion ? 0 : 60);
        var dur = TimeSpan.FromMilliseconds(_reduceMotion ? 150 : 300);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(1, dur) { BeginTime = begin, EasingFunction = ease };
        fade.Completed += (_, _) => { if (v.IsHitTestVisible) v.CacheMode = null; }; // crisp text once settled
        v.CacheMode = new BitmapCache { RenderAtScale = 1.25 };
        v.BeginAnimation(OpacityProperty, fade);
        if (_reduceMotion) { ((ScaleTransform)v.RenderTransform).ScaleX = ((ScaleTransform)v.RenderTransform).ScaleY = 1; return; }

        var st = (ScaleTransform)v.RenderTransform;
        var grow = new DoubleAnimation(1, dur) { BeginTime = begin, EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Dematerialize(FrameworkElement v)
    {
        if (v.Visibility != Visibility.Visible) return;
        v.IsHitTestVisible = false;
        v.CacheMode = new BitmapCache { RenderAtScale = 1.25 };
        var dur = TimeSpan.FromMilliseconds(120);
        var fade = new DoubleAnimation(0, dur);
        fade.Completed += (_, _) => { if (!v.IsHitTestVisible) { v.Visibility = Visibility.Hidden; v.CacheMode = null; } };
        v.BeginAnimation(OpacityProperty, fade);
        if (_reduceMotion) return;

        var st = (ScaleTransform)v.RenderTransform;
        var shrink = new DoubleAnimation(0.94, dur);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    // ================= waveform =================

    private void SetupBars(Panel panel)
    {
        double[] periods = { 0.43, 0.31, 0.52, 0.37, 0.47 };
        double[] lows = { 0.30, 0.45, 0.25, 0.40, 0.35 };
        int i = 0;
        foreach (Rectangle bar in panel.Children)
        {
            var anim = new DoubleAnimation(lows[i], 1.0, TimeSpan.FromSeconds(periods[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                BeginTime = TimeSpan.FromSeconds(i * 0.07),
            };
            Storyboard.SetTarget(anim, bar);
            Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.ScaleY"));
            var sb = new Storyboard();
            sb.Children.Add(anim);
            _bars.Add(sb);
            i++;
        }
    }

    private void SetBarsRunning(bool run)
    {
        if (run == _barsRunning) return;
        _barsRunning = run;
        foreach (var sb in _bars)
        {
            if (run) sb.Begin(this, true);
            else sb.Stop(this);
        }
    }

    // ================= input =================

    /// <summary>
    /// Tap = feedback on press (instant scale-down), commit on release while still over the target.
    /// Avoids Button mouse-capture, which is unreliable in a no-activate window.
    /// </summary>
    private void Tap(Border el, Action action, bool hoverFill = false)
    {
        el.RenderTransform = new ScaleTransform(1, 1);
        var restBackground = el.Background;
        var hoverBackground = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

        el.MouseEnter += (_, _) => { if (hoverFill) el.Background = hoverBackground; };
        el.MouseLeave += (_, _) =>
        {
            if (hoverFill) el.Background = restBackground;
            if (_pressed == el) { _pressed = null; PressScale(el, false); }
        };
        el.MouseLeftButtonDown += (_, e) => { _pressed = el; PressScale(el, true); e.Handled = true; };
        el.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_pressed != el) return;
            _pressed = null;
            PressScale(el, false);
            action();
        };
    }

    private static void PressScale(FrameworkElement el, bool down)
    {
        var st = (ScaleTransform)el.RenderTransform;
        var anim = down
            ? new DoubleAnimation(0.86, TimeSpan.FromMilliseconds(90)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void PointerEnter(Expand target)
    {
        _leaveGrace.Stop();
        if (_alert != Alert.None) return;

        if (_expand == Expand.None)
        {
            if (target == Expand.Main && !_peek) { _peek = true; Refresh(); } // instant response: sliver nudges
            _pendingExpand = target;
            _hoverIntent.Stop();
            _hoverIntent.Start(); // expand only on deliberate hover, not a pass-by
        }
    }

    private void PointerLeave()
    {
        // Moving between the hit strip, the island and the bubble is not a real leave.
        if (Island.IsMouseOver || Bubble.IsMouseOver || HitZone.IsMouseOver) return;
        _hoverIntent.Stop();
        if (_peek) { _peek = false; Refresh(); }
        if (_expand != Expand.None) _leaveGrace.Start();
    }

    private void Island_Down(object sender, MouseButtonEventArgs e)
    {
        _scale.Target = 0.95; // squish under the pointer, immediately
        StartFrames();
    }

    private void Island_Up(object sender, MouseButtonEventArgs e)
    {
        _scale.Target = 1;
        StartFrames();

        if (_approvals.Count > 0 || Asking) return; // these close only through their own buttons
        if (_alert != Alert.None) { _alertTimer.Stop(); _alert = Alert.None; Refresh(); return; }

        _hoverIntent.Stop();
        _expand = _expand == Expand.None ? Expand.Main : Expand.None;
        Refresh();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ================= alerts =================

    private void ShowAlert(Alert alert, double seconds)
    {
        _alert = alert;
        _alertTimer.Stop();
        _alertTimer.Interval = TimeSpan.FromSeconds(seconds);
        _alertTimer.Start();
        Refresh();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        Dispatcher.InvokeAsync(() =>
        {
            bool onAc = Power.IsOnAc();
            if (onAc && !_wasOnAc && AppSettings.Current.ShowCharging && Power.BatteryPercent() is int pct)
            {
                ChargePercent.Text = pct + "%";
                ChargeFill.Width = Math.Max(2, 24 * pct / 100.0);
                ShowAlert(Alert.Charging, 3);
            }
            _wasOnAc = onAc;
        });
    }

    // ================= timer =================

    private TimeSpan TimerLeft()
    {
        if (_timerPausedLeft != null) return _timerPausedLeft.Value;
        if (_timerEnd == null) return TimeSpan.Zero;
        var left = _timerEnd.Value - DateTime.Now;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private void StartTimer(int minutes)
    {
        _timerTotal = TimeSpan.FromMinutes(minutes);
        _timerEnd = DateTime.Now + _timerTotal;
        _timerPausedLeft = null;
        Refresh();
    }

    private void ToggleTimerPause()
    {
        if (_timerPausedLeft != null) { _timerEnd = DateTime.Now + _timerPausedLeft.Value; _timerPausedLeft = null; }
        else if (_timerEnd != null) { _timerPausedLeft = TimerLeft(); _timerEnd = null; }
        Refresh();
    }

    private void CancelTimer()
    {
        _timerEnd = null;
        _timerPausedLeft = null;
        if (_expand == Expand.Bubble) _expand = Expand.None;
        Refresh();
    }

    private void UpdateRing()
    {
        double p = _timerTotal.TotalSeconds > 0 ? TimerLeft().TotalSeconds / _timerTotal.TotalSeconds : 0;
        const double cx = 17, cy = 17, rad = 9.5;
        if (p >= 0.999) { BubbleRing.Data = new EllipseGeometry(new Point(cx, cy), rad, rad); return; }
        if (p <= 0.001) { BubbleRing.Data = null; return; }

        double a = p * 2 * Math.PI;
        var end = new Point(cx + rad * Math.Sin(a), cy - rad * Math.Cos(a));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(cx, cy - rad), false, false);
            ctx.ArcTo(end, new Size(rad, rad), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        BubbleRing.Data = geo;
    }

    // ================= media progress =================

    private void UpdateProgress()
    {
        if (ProgressRow.Visibility != Visibility.Visible) return;
        var pos = _media.Position;
        var dur = _media.Duration;
        ElapsedText.Text = Format(pos);
        RemainingText.Text = "-" + Format(dur - pos);
        double track = ProgressTrack.ActualWidth;
        if (track > 0 && dur.TotalSeconds > 0) ProgressFill.Width = track * Math.Clamp(pos / dur, 0, 1);
    }

    private static string Format(TimeSpan t)
    {
        int s = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}

internal static class Power
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool IsOnAc() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 1;

    /// <summary>Battery percent, or null on desktops without a battery.</summary>
    public static int? BatteryPercent()
        => GetSystemPowerStatus(out var s) && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
}

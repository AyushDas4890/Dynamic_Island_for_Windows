using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>Claude Code companion: live activity, Allow / Deny, and "ask about this window".</summary>
public partial class MainWindow
{
    // ---- Claude Code activity ----
    private AgentBridge? _bridge;
    private readonly List<ApprovalRequest> _approvals = new();
    private DateTime _agentLastEvent = DateTime.MinValue;
    private bool _agentWorking;

    /// <summary>Working, and heard from it recently (Claude Code sends no event while it only thinks).</summary>
    private bool AgentActive => _agentWorking && DateTime.Now - _agentLastEvent < TimeSpan.FromMinutes(5);

    internal static string TokenPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "hook-token.txt");

    internal static string HookToken()
    {
        if (File.Exists(TokenPath)) return File.ReadAllText(TokenPath).Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(TokenPath, token);
        return token;
    }

    private void InitAgent()
    {
        Tap(AllowButton, () => _approvals.FirstOrDefault()?.Allow());
        Tap(DenyButton, () => _approvals.FirstOrDefault()?.Deny());
        Tap(AskClose, EndAsk);
        AgentMascotHost.MouseLeftButtonDown += (_, e) => { AgentMascot.Poke(); e.Handled = true; };
        AskInput.TextChanged += (_, _) => AskPlaceholder.Visibility = AskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AskInput.PreviewKeyDown += AskInput_KeyDown;
        SetupHomeMascotDrag();
        InitDash();
        StartPulse(AgentDot);

        try
        {
            _bridge = new AgentBridge(Dispatcher, HookToken());
            _bridge.Activity += OnAgentActivity;
            _bridge.ApprovalNeeded += r =>
            {
                _approvals.Add(r);
                AppState.Pending.Add(r);
                AppState.Log(r.Project, r.Title + ": " + r.Detail, "ask");
                Chime(SystemSounds.Question);
                Refresh();
            };
            _bridge.ApprovalClosed += r =>
            {
                _approvals.Remove(r);
                AppState.Pending.Remove(r);
                string outcome = r.Decision.Task.IsCompletedSuccessfully && r.Decision.Task.Result is bool b ? (b ? "Allowed" : "Denied") : "Left to Claude Code";
                AppState.Log(r.Project, outcome + ": " + r.Detail, outcome == "Allowed" ? "allow" : "deny");
                Refresh();
            };
            _bridge.Start();
        }
        catch (Exception ex)
        {
            // Port busy (another copy?) - the island still works without Claude Code.
            System.Diagnostics.Debug.WriteLine("Agent bridge: " + ex.Message);
        }
    }

    private static void Chime(SystemSound sound) { if (AppSettings.Current.Sounds) sound.Play(); }

    private void OnAgentActivity(AgentEvent e)
    {
        RecordAgent(e);
        if (e.Kind != "working" || e.Detail.Length > 0)
            AppState.Log(e.Project, e.Kind == "done" ? "Done: " + e.Detail : (e.Activity.TrimEnd('…') + " " + e.Detail).Trim(), e.Kind);
        if (!AppSettings.Current.ShowClaudeActivity) return;
        _agentLastEvent = DateTime.Now;
        AgentProject.Text = "Claude Code · " + e.Project;
        AgentActivity.Text = e.Activity;
        AgentDetail.Text = e.Detail;
        AgentDetail.Visibility = e.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        switch (e.Kind)
        {
            case "working":
                _agentWorking = true;
                AgentMascot.SetMood(Mood.Focused);
                break;
            case "notify": // Claude called island_notify over MCP
                AgentProject.Text = "Claude";
                AgentMascot.SetMood(Mood.Happy);
                Chime(SystemSounds.Asterisk);
                ShowAlert(Alert.Agent, 7);
                return;
            case "waiting":
                AgentMascot.SetMood(Mood.Curious);
                Chime(SystemSounds.Question);
                ShowAlert(Alert.Agent, 6);
                return;
            case "done":
                _agentWorking = false;
                AgentActivity.Text = "Done";
                AgentMascot.SetMood(Mood.Happy);
                Chime(SystemSounds.Asterisk);
                ShowAlert(Alert.Agent, 6);
                return;
        }
        Refresh();
    }

    private void UpdateApproval()
    {
        var r = _approvals.FirstOrDefault();
        if (r == null) return;
        ApproveProject.Text = "Claude Code · " + r.Project + (_approvals.Count > 1 ? $"  ({_approvals.Count} waiting)" : "");
        ApproveTitle.Text = r.Title;
        ApproveDetail.Text = r.Detail.Length > 0 ? r.Detail : "(no details)";
        AllowText.Text = r.AllowLabel;
        DenyText.Text = r.DenyLabel;
        ApproveMascot.SetMood(Mood.Curious);
    }

    private static void StartPulse(UIElement dot) =>
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

    // ---- Ask about a window ----

    private enum AskStage { None, Key, Question, Thinking, Answer }
    private AskStage _ask = AskStage.None;
    private byte[]? _askImage;
    private CancellationTokenSource? _askCancel;
    private bool Asking => _ask != AskStage.None;

    /// <summary>Snapshot the window first (before the island grows over it), then open the question box.</summary>
    private void BeginAsk(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        try { _askImage = AskClaude.Capture(target); }
        catch (Exception) { return; }

        string title = AskClaude.Title(target);
        AskTarget.Text = title.Length > 0 ? title : "Selected window";
        AskMascot.SetMood(Mood.Curious);
        SetAskStage(AskClaude.CanAsk ? AskStage.Question : AskStage.Key);
        _expand = Expand.None;
        Refresh();
        TakeKeyboard(true);
    }

    /// <summary>Opens the island's question box about a given window (used by the home window).</summary>
    public void AskAbout(IntPtr window) => BeginAsk(window);

    private void SetAskStage(AskStage stage)
    {
        _ask = stage;
        AskInput.IsEnabled = stage is AskStage.Key or AskStage.Question or AskStage.Answer;
        AskInputBox.Visibility = stage == AskStage.Thinking ? Visibility.Collapsed : Visibility.Visible;
        AskAnswerScroll.Visibility = stage is AskStage.Thinking or AskStage.Answer ? Visibility.Visible : Visibility.Collapsed;
        AskHeading.Text = stage switch
        {
            AskStage.Key => "Add your Anthropic API key",
            AskStage.Thinking => "Looking at your window…",
            AskStage.Answer => "Here's what I see",
            _ => "Ask about this window",
        };
        AskPlaceholder.Text = stage == AskStage.Key
            ? "Paste key (sk-ant-…), press Enter. Stored encrypted on this PC."
            : stage == AskStage.Answer ? "Ask a follow-up about the same window…" : "What do you want to know? Press Enter";
        if (stage == AskStage.Thinking) { AskAnswer.Text = "Thinking…"; AskMascot.SetMood(Mood.Focused); }
        AskInput.Text = "";
        Refresh();
        if (AskInput.IsEnabled) AskInput.Focus();
    }

    private async void AskInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndAsk(); e.Handled = true; return; }
        if (e.Key != Key.Enter || AskInput.Text.Trim().Length == 0) return;
        e.Handled = true;
        string text = AskInput.Text.Trim();

        if (_ask == AskStage.Key)
        {
            AskClaude.SaveKey(text);
            SetAskStage(AskStage.Question);
            return;
        }

        SetAskStage(AskStage.Thinking);
        _askCancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            AskAnswer.Text = await AskClaude.AskAsync(_askImage!, text, _askCancel.Token);
            AskMascot.SetMood(Mood.Happy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AskAnswer.Text = ex.Message.Contains("401") || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                ? "That API key didn't work. Right-click the island → Forget Anthropic API key, then try again."
                : "Couldn't reach Claude: " + ex.Message;
            AskMascot.SetMood(Mood.Dizzy);
        }
        if (_ask == AskStage.Thinking) SetAskStage(AskStage.Answer);
    }

    private void EndAsk()
    {
        _askCancel?.Cancel();
        _ask = AskStage.None;
        _askImage = null;
        AskAnswer.Text = "";
        TakeKeyboard(false);
        Refresh();
    }

    private void ForgetKey_Click(object sender, RoutedEventArgs e) => AskClaude.ForgetKey();

    /// <summary>The island never takes focus, except while you type a question.</summary>
    private void TakeKeyboard(bool take)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, take ? style & ~WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE);
        if (take)
        {
            SetForegroundWindow(hwnd);
            Activate();
            AskInput.Focus();
            Keyboard.Focus(AskInput);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    // ---- drag the mascot onto any window ----

    private Window? _dragGhost;
    private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _dragStart;
    private bool _dragging;
    private IntPtr _foregroundAtPress;

    private void SetupHomeMascotDrag()
    {
        HomeMascotHost.MouseLeftButtonDown += (_, e) => { e.Handled = true; StartMascotDrag(HomeMascot, askOnClick: true); };
        _dragTimer.Tick += (_, _) => DragTick();
    }

    private Mascot? _dragSource;
    private bool _askOnClick;

    /// <summary>Press on a robot: drag it onto a window to ask about that window; a plain click pokes it.</summary>
    private void StartMascotDrag(Mascot source, bool askOnClick)
    {
        _dragSource = source;
        _askOnClick = askOnClick;
        _foregroundAtPress = AskClaude.ForegroundWindow(); // the app you were using (the island never takes focus)
        _dragStart = CursorDip();
        _dragging = false;
        _dragTimer.Start();
    }

    private void DragTick()
    {
        var p = CursorDip();
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        if (!_dragging && down && (Math.Abs(p.X - _dragStart.X) > 8 || Math.Abs(p.Y - _dragStart.Y) > 8))
        {
            _dragging = true;
            _dragGhost ??= new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Width = 56, Height = 56, IsHitTestVisible = false,
                Content = new System.Windows.Controls.Viewbox { Child = new Mascot() },
            };
            ((Mascot)((System.Windows.Controls.Viewbox)_dragGhost.Content).Child).SetMood(Mood.Curious);
            _dragGhost.Show();
            _expand = Expand.None;
            Refresh();
        }

        if (_dragging && _dragGhost != null)
        {
            _dragGhost.Left = p.X - 28;
            _dragGhost.Top = p.Y - 28;
        }

        if (down) return;
        _dragTimer.Stop();

        if (_dragging)
        {
            _dragGhost?.Hide();
            var ignore = new List<IntPtr> { new WindowInteropHelper(this).Handle };
            if (_dragGhost != null) ignore.Add(new WindowInteropHelper(_dragGhost).Handle);
            BeginAsk(AskClaude.WindowUnderCursor(ignore));
        }
        else
        {
            var m = _dragSource ?? HomeMascot;
            m.Poke(); // a click
            if (_askOnClick && m.Mood != Mood.Dizzy) BeginAsk(_foregroundAtPress);
        }
    }

    /// <summary>Cursor position in WPF device-independent units.</summary>
    private Point CursorDip()
    {
        GetCursorPosRaw(out var raw);
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return m.Transform(new Point(raw.X, raw.Y));
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool GetCursorPosRaw(out RawPoint p);
}

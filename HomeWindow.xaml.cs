using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DynamicIsland;

/// <summary>The app's home: status, approvals queue, history, Claude Code setup and settings.</summary>
public partial class HomeWindow : Window
{
    private readonly MainWindow _island;
    private bool _loading;

    public HomeWindow(MainWindow island)
    {
        _island = island;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/island.ico"))!.Stream);
        VersionText.Text = "Version " + (typeof(HomeWindow).Assembly.GetName().Version?.ToString(3) ?? "1.1.0");

        PendingList.ItemsSource = AppState.Pending;
        HistoryList.ItemsSource = AppState.History;
        AppState.Pending.CollectionChanged += OnListsChanged;
        AppState.History.CollectionChanged += OnListsChanged;
        Closed += (_, _) =>
        {
            AppState.Pending.CollectionChanged -= OnListsChanged;
            AppState.History.CollectionChanged -= OnListsChanged;
        };

        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += async (_, _) => { LoadSettings(); OnListsChanged(null, null); await RefreshStatus(); };
        Activated += async (_, _) => await RefreshStatus();
    }

    // ---- navigation ----

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return; // during InitializeComponent
        PageHome.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageClaude.Visibility = NavClaude.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAsk.Visibility = NavAsk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageIsland.Visibility = NavIsland.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowPage(string page)
    {
        (page switch { "claude" => NavClaude, "ask" => NavAsk, "island" => NavIsland, _ => NavHome }).IsChecked = true;
    }

    // ---- home ----

    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        PendingEmpty.Visibility = AppState.Pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = AppState.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var today = AppState.History.Where(h => h.Time.Date == DateTime.Today).ToList();
        StatApprovals.Text = today.Count(h => h.Kind is "allow" or "deny").ToString();
        StatApprovalsNote.Text = $"{today.Count(h => h.Kind == "allow")} allowed · {today.Count(h => h.Kind == "deny")} denied";
        int hour = DateTime.Now.Hour;
        Greeting.Text = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        LogoMascot.SetMood(AppState.Pending.Count > 0 ? Mood.Curious : Mood.Idle);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => ((sender as Button)?.Tag as ApprovalRequest)?.Allow();
    private void Deny_Click(object sender, RoutedEventArgs e) => ((sender as Button)?.Tag as ApprovalRequest)?.Deny();

    private async Task RefreshStatus()
    {
        bool cli = AskClaude.ClaudeCodePath() != null;
        bool hooks = ClaudeHooks.IsInstalled();
        StatClaude.Text = !cli ? "Not installed" : hooks ? "Connected" : "Not connected";
        StatClaude.Foreground = hooks ? (System.Windows.Media.Brush)FindResource("Green") : System.Windows.Media.Brushes.White;
        StatClaudeNote.Text = !cli ? "Install Claude Code to follow it here." : hooks ? "Activity and approvals come to the island." : "Turn it on under Claude Code.";
        CliStatus.Text = cli
            ? "Claude Code is installed. Sign in once with your Claude account; the island then works with your subscription."
            : "Claude Code isn't installed yet. Get it first, then sign in.";
        SignInButton.IsEnabled = cli;

        _loading = true;
        HooksSwitch.IsChecked = hooks;
        HooksSwitch.IsEnabled = true;
        McpSwitch.IsEnabled = cli;
        bool mcp = cli && await ClaudeMcp.IsAdded();
        McpSwitch.IsChecked = mcp;
        StatMcp.Text = mcp ? "Added" : "Not added";
        StatMcp.Foreground = mcp ? (System.Windows.Media.Brush)FindResource("Green") : System.Windows.Media.Brushes.White;
        _loading = false;

        AskMode.Text = AskClaude.HasKey ? "Using your Anthropic API key" : cli ? "Using your Claude subscription via Claude Code" : "Not set up: install Claude Code or add an API key";
        KeyStatus.Text = AskClaude.HasKey ? "A key is saved." : "No key saved.";
    }

    // ---- Claude Code ----

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var exe = AskClaude.ClaudeCodePath();
        if (exe == null) return;
        // A visible terminal running Claude Code; the user types /login there.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k title Claude Code sign-in && echo Type /login and press Enter to sign in with your Claude account. && \"{exe}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    private void GetClaudeCode_Click(object sender, RoutedEventArgs e) => OpenUrl("https://code.claude.com/docs/en/setup");
    private void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/AyushDas4890/Dynamic_Island_for_Windows");
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async void HooksSwitch_Click(object sender, RoutedEventArgs e)
    {
        ConnectMessage.Text = "";
        try
        {
            if (HooksSwitch.IsChecked == true) ClaudeHooks.Install(MainWindow.HookToken());
            else ClaudeHooks.Uninstall();
        }
        catch (Exception ex) { ConnectMessage.Text = "Couldn't update ~/.claude/settings.json: " + ex.Message; }
        await RefreshStatus();
    }

    private async void McpSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConnectMessage.Text = "";
        McpSwitch.IsEnabled = false;
        var (ok, output) = McpSwitch.IsChecked == true ? await ClaudeMcp.Add(MainWindow.HookToken()) : await ClaudeMcp.Remove();
        if (!ok) ConnectMessage.Text = output;
        await RefreshStatus();
    }

    // ---- ask ----

    private void TryAsk_Click(object sender, RoutedEventArgs e) => _island.AskAbout(new WindowInteropHelper(this).Handle);

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Trim().Length == 0) return;
        AskClaude.SaveKey(KeyBox.Password);
        KeyBox.Clear();
        _ = RefreshStatus();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e) { AskClaude.ForgetKey(); _ = RefreshStatus(); }

    // ---- settings ----

    private void LoadSettings()
    {
        _loading = true;
        var s = AppSettings.Current;
        ShowActivitySwitch.IsChecked = s.ShowClaudeActivity;
        AutoAllowSwitch.IsChecked = s.AutoAllowReadOnly;
        TimeoutSlider.Value = s.ApprovalTimeoutSeconds;
        SoundsSwitch.IsChecked = s.Sounds;
        MusicSwitch.IsChecked = s.ShowMusic;
        NotificationsSwitch.IsChecked = s.ShowNotifications;
        ClipboardSwitch.IsChecked = s.ShowClipboard;
        BluetoothSwitch.IsChecked = s.ShowBluetooth;
        ChargingSwitch.IsChecked = s.ShowCharging;
        PrivacySwitch.IsChecked = s.ShowPrivacyDots;
        StartupSwitch.IsChecked = App.IsStartupEnabled();
        UpdateTimeoutText();
        _loading = false;
    }

    private void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.ShowClaudeActivity = ShowActivitySwitch.IsChecked == true;
        s.AutoAllowReadOnly = AutoAllowSwitch.IsChecked == true;
        s.Sounds = SoundsSwitch.IsChecked == true;
        s.ShowMusic = MusicSwitch.IsChecked == true;
        s.ShowNotifications = NotificationsSwitch.IsChecked == true;
        s.ShowClipboard = ClipboardSwitch.IsChecked == true;
        s.ShowBluetooth = BluetoothSwitch.IsChecked == true;
        s.ShowCharging = ChargingSwitch.IsChecked == true;
        s.ShowPrivacyDots = PrivacySwitch.IsChecked == true;
        s.Save();
    }

    private void TimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TimeoutText == null) return;
        UpdateTimeoutText();
        if (_loading) return;
        AppSettings.Current.ApprovalTimeoutSeconds = (int)TimeoutSlider.Value;
        AppSettings.Current.Save();
    }

    private void UpdateTimeoutText() =>
        TimeoutText.Text = $"{(int)TimeoutSlider.Value} seconds, then Claude Code asks in its own window instead";

    private void StartupSwitch_Click(object sender, RoutedEventArgs e) => App.SetStartup(StartupSwitch.IsChecked == true);

    // ---- chrome ----

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void DarkTitleBar()
    {
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }
}

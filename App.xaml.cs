using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

/// <summary>
/// App shell: one instance only, a tray icon to hide/show the island, start with Windows, and quit.
/// </summary>
public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicIsland";

    private Mutex? _single;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _toggleItem;
    private MainWindow? _island;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--write-hooks") // writes the hook config to a given settings file (testing)
        {
            ClaudeHooks.SettingsPath = e.Args[1];
            ClaudeHooks.Install(DynamicIsland.MainWindow.HookToken());
            Shutdown();
            return;
        }

        if (e.Args.Length == 2 && e.Args[0] == "--screenshots")
        {
            var w = new MainWindow { ShowInTaskbar = false, Left = -4000 };
            w.Loaded += (_, _) => Dispatcher.InvokeAsync(() => { w.RenderDemo(e.Args[1]); Shutdown(); },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            w.Show();
            return;
        }

        // One copy only. Launching it again (Start Menu, desktop) just opens the running copy's home window.
        _single = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DynamicIsland.ShowHome");
        if (!first) { _showSignal.Set(); Shutdown(); return; }
        _ = Task.Run(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.InvokeAsync(OpenHome);
        });

        _island = new MainWindow();
        _island.Show();

        // First launch: start with Windows by default, and show the home window once.
        bool launchedAtSignIn = e.Args.Contains("--startup");
        if (!AppSettings.Current.FirstRunDone)
        {
            SetStartup(true);
            AppSettings.Current.FirstRunDone = true;
            AppSettings.Current.Save();
            OpenHome();
        }
        else
        {
            if (IsStartupEnabled()) SetStartup(true); // keep the sign-in entry pointing at this exe, wherever it moved
            if (!launchedAtSignIn) OpenHome();
        }

        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Dynamic Island", null, (_, _) => OpenHome()) { Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        _toggleItem = new Forms.ToolStripMenuItem("Hide island", null, (_, _) => ToggleIsland());
        menu.Items.Add(open);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Dynamic Island", null, (_, _) => Shutdown()));

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/island.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, new System.Drawing.Size(32, 32)),
            Text = "Dynamic Island",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, m) => { if (m.Button == Forms.MouseButtons.Left) OpenHome(); };
    }

    private EventWaitHandle? _showSignal;
    private HomeWindow? _home;

    public void OpenHome() => OpenHome("home");

    public void OpenHome(string page)
    {
        if (_island == null) return;
        if (_home == null) { _home = new HomeWindow(_island); _home.Closed += (_, _) => _home = null; }
        _home.ShowPage(page);
        _home.Show();
        if (_home.WindowState == WindowState.Minimized) _home.WindowState = WindowState.Normal;
        _home.Activate();
    }

    private void ToggleIsland()
    {
        if (_island == null || _toggleItem == null) return;
        if (_island.IsVisible) { _island.Hide(); _toggleItem.Text = "Show island"; }
        else { _island.Show(); _toggleItem.Text = "Hide island"; }
    }

    internal static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    internal static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _single?.Dispose();
        base.OnExit(e);
    }
}

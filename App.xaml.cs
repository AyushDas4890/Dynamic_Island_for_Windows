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

        if (e.Args.Length == 2 && e.Args[0] == "--screenshots")
        {
            var w = new MainWindow { ShowInTaskbar = false, Left = -4000 };
            w.Loaded += (_, _) => Dispatcher.InvokeAsync(() => { w.RenderDemo(e.Args[1]); Shutdown(); },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            w.Show();
            return;
        }

        _single = new Mutex(true, @"Local\DynamicIsland.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; } // already running: its tray icon is the way in

        _island = new MainWindow();
        _island.Show();

        var menu = new Forms.ContextMenuStrip();
        _toggleItem = new Forms.ToolStripMenuItem("Hide island", null, (_, _) => ToggleIsland());
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(startup);
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
        _tray.MouseClick += (_, m) => { if (m.Button == Forms.MouseButtons.Left) ToggleIsland(); };
    }

    private void ToggleIsland()
    {
        if (_island == null || _toggleItem == null) return;
        if (_island.IsVisible) { _island.Hide(); _toggleItem.Text = "Show island"; }
        else { _island.Show(); _toggleItem.Text = "Hide island"; }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _single?.Dispose();
        base.OnExit(e);
    }
}

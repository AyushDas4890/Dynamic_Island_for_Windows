using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland;

/// <summary>
/// Reads new Windows toast notifications.
/// The NotificationChanged event throws for unpackaged apps (microsoft/windowsappsdk#6172),
/// so this polls GetNotificationsAsync and reports ids it has not seen before.
/// Needs Settings > Privacy &amp; security > Notifications > "Let apps access your notifications".
/// </summary>
public sealed class NotificationWatcher
{
    public sealed record Notice(string App, string Title, string Body, BitmapSource? Icon);

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly HashSet<uint> _seen = new();
    private UserNotificationListener? _listener;
    private bool _busy;

    public event Action<Notice>? Arrived;
    public string Status { get; private set; } = "not started";

    public NotificationWatcher(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        _listener = UserNotificationListener.Current;
        var access = await _listener.RequestAccessAsync();
        Status = access.ToString();
        if (access != UserNotificationListenerAccessStatus.Allowed) return;

        // Existing notifications are history, not news.
        foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);

        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
    }

    private async Task PollAsync()
    {
        if (_busy || _listener == null) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();
            foreach (var n in list)
            {
                current.Add(n.Id);
                if (!_seen.Add(n.Id)) continue;
                var notice = await ReadAsync(n);
                if (notice != null) Arrived?.Invoke(notice);
            }
            _seen.IntersectWith(current); // forget dismissed ones
        }
        catch (Exception)
        {
            // Listener can fail transiently (e.g. access revoked); try again next tick.
        }
        finally { _busy = false; }
    }

    private static async Task<Notice?> ReadAsync(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding == null) return null;
        var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts.Count == 0) return null;

        string app = "Notification";
        BitmapSource? icon = null;
        try
        {
            app = n.AppInfo.DisplayInfo.DisplayName;
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var win = await logo.OpenReadAsync();
            using var s = win.AsStreamForRead();
            var mem = new MemoryStream();
            await s.CopyToAsync(mem);
            mem.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();
            icon = bmp;
        }
        catch (Exception)
        {
            // Some apps have no logo; show a glyph instead.
        }

        return new Notice(app, texts[0], string.Join(" ", texts.Skip(1)), icon);
    }
}

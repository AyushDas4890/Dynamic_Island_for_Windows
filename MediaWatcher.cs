using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace DynamicIsland;

/// <summary>
/// Follows the current Windows media session (Spotify, browsers, Media Player...)
/// through GlobalSystemMediaTransportControls. All state changes happen on the UI thread.
/// </summary>
public sealed class MediaWatcher
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private TimeSpan _position;
    private DateTimeOffset _positionUpdated;

    public event Action? Changed;

    public bool HasSession => _session != null;
    public bool IsPlaying { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public Color Accent { get; private set; } = Colors.White;
    public TimeSpan Duration { get; private set; }

    /// <summary>Current position, extrapolated while playing (apps only report it occasionally).</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying && _positionUpdated != default) p += DateTimeOffset.Now - _positionUpdated;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p;
        }
    }

    public MediaWatcher(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(Attach);
        Attach();
    }

    public async Task TogglePlayPauseAsync() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    public async Task NextAsync() { if (_session != null) await _session.TrySkipNextAsync(); }
    public async Task PreviousAsync() { if (_session != null) await _session.TrySkipPreviousAsync(); }

    private void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionEvent;
            _session.PlaybackInfoChanged -= OnSessionEvent;
            _session.TimelinePropertiesChanged -= OnSessionEvent;
        }

        _session = _manager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionEvent;
            _session.PlaybackInfoChanged += OnSessionEvent;
            _session.TimelinePropertiesChanged += OnSessionEvent;
        }

        _ = RefreshAsync();
    }

    private void OnSessionEvent(GlobalSystemMediaTransportControlsSession sender, object args)
        => _ui.InvokeAsync(RefreshAsync);

    private string _artKey = "";

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null)
        {
            IsPlaying = false;
            Title = Artist = _artKey = "";
            Art = null;
            Accent = Colors.White;
            Duration = TimeSpan.Zero;
            Changed?.Invoke();
            return;
        }

        try
        {
            IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var timeline = session.GetTimelineProperties();
            Duration = timeline.EndTime - timeline.StartTime;
            _position = timeline.Position;
            _positionUpdated = timeline.LastUpdatedTime;

            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session) return; // session switched while awaiting

            Title = props.Title ?? "";
            Artist = props.Artist ?? "";

            // Only reload artwork when the track changes.
            string key = Title + "\u0001" + Artist;
            if (key != _artKey)
            {
                Art = props.Thumbnail != null ? await LoadImageAsync(props.Thumbnail) : null;
                Accent = Art != null ? AccentFrom(Art) : Colors.White;
                if (Art != null) _artKey = key; // no art yet (browsers send it late): retry next event
            }
        }
        catch (Exception)
        {
            // Apps sometimes close their session mid-read; keep last known state.
        }

        Changed?.Invoke();
    }

    private static async Task<BitmapSource?> LoadImageAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        using var winStream = await reference.OpenReadAsync();
        using var stream = winStream.AsStreamForRead();
        var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        if (memory.Length == 0) return null;
        memory.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 160;
        bmp.StreamSource = memory;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Average artwork colour, lifted so it reads on black (Apple tints the waveform with it).</summary>
    private static Color AccentFrom(BitmapSource art)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(art,
            new ScaleTransform(8.0 / art.PixelWidth, 8.0 / art.PixelHeight)), PixelFormats.Bgra32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);

        double r = 0, g = 0, b = 0;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
        int n = w * h;
        r /= n; g /= n; b /= n;

        // Push the brightest channel up to ~235 so dark covers still give a visible tint.
        double max = Math.Max(r, Math.Max(g, b));
        if (max < 1) return Colors.White;
        double k = 235 / max;
        return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
    }
}

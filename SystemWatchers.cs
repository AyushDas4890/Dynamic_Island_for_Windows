using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland;

/// <summary>Raises an event when the clipboard changes (AddClipboardFormatListener).</summary>
public sealed class ClipboardWatcher
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>Short description of what was copied, e.g. text preview or "3 files".</summary>
    public event Action<string>? Copied;

    public void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(Hook);
        AddClipboardFormatListener(source.Handle);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // Let the source app finish writing before reading.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Read);
        }
        return IntPtr.Zero;
    }

    private void Read()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;

            // Password managers mark secrets with these formats; never show them.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("Clipboard Viewer Ignore")) return;

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                Copied?.Invoke(files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} files");
            else if (data.GetDataPresent(DataFormats.Bitmap))
                Copied?.Invoke("Image");
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
                Copied?.Invoke(OneLine(text));
        }
        catch (COMException)
        {
            // Another app holds the clipboard open; skip this update.
        }
    }

    private static string OneLine(string s)
    {
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}

/// <summary>Reports Bluetooth devices connecting and disconnecting (classic and LE).</summary>
public sealed class BluetoothWatcher
{
    private readonly Dispatcher _ui;
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly Dictionary<string, string> _connected = new();
    private int _enumerated;

    /// <summary>(device name, connected)</summary>
    public event Action<string, bool>? Changed;

    public BluetoothWatcher(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                     BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                 })
        {
            var w = DeviceInformation.CreateWatcher(selector);
            w.Added += (_, d) => _ui.InvokeAsync(() => OnAdded(d.Id, d.Name));
            w.Removed += (_, d) => _ui.InvokeAsync(() => OnRemoved(d.Id));
            w.Updated += (_, _) => { };
            w.EnumerationCompleted += (_, _) => _ui.InvokeAsync(() => _enumerated++);
            _watchers.Add(w);
            w.Start();
        }
    }

    private bool Ready => _enumerated >= _watchers.Count; // ignore devices already connected at startup

    private void OnAdded(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _connected[id] = name;
        if (Ready) Changed?.Invoke(name, true);
    }

    private void OnRemoved(string id)
    {
        if (!_connected.Remove(id, out var name)) return;
        if (Ready) Changed?.Invoke(name, false);
    }
}

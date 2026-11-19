using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public enum IslandDock { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>
/// Drag the island anywhere; on release it springs to the nearest of six dock spots
/// (top or bottom, left / centre / right). At the bottom it sits on the taskbar and grows upwards.
/// </summary>
public partial class MainWindow
{
    private const double EdgeMargin = 14;
    private readonly System.Windows.Threading.DispatcherTimer _dragPoll = new() { Interval = TimeSpan.FromMilliseconds(12) };
    private POINT _islandDragStart;
    private double _dragWinLeft, _dragWinTop;
    private bool _dragArmed, _islandDragging;

    private static IslandDock Dock => AppSettings.Current.Dock;
    private static bool AtBottom => Dock >= IslandDock.BottomLeft;
    private static int Column => (int)Dock % 3; // 0 left, 1 centre, 2 right

    /// <summary>Window position for the current dock (the island is laid out inside it by ApplyGeometry).</summary>
    private void PlaceWindow(bool animate = false)
    {
        var wa = SystemParameters.WorkArea;
        double left = Column switch
        {
            0 => wa.Left,
            2 => wa.Right - Width,
            _ => (SystemParameters.PrimaryScreenWidth - Width) / 2,
        };
        double top = AtBottom ? wa.Bottom - Height : 0;
        if (animate)
        {
            Slide(LeftProperty, left);
            Slide(TopProperty, top);
        }
        else { Left = left; Top = top; }
        s_growFromBottom = AtBottom;
        ApplyGeometry();
    }

    private void Slide(DependencyProperty p, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => SetValue(p, to);
        BeginAnimation(p, a);
    }

    /// <summary>Island x within the window for the current dock.</summary>
    private double IslandLeft(double total)
    {
        double W = Root.ActualWidth > 0 ? Root.ActualWidth : Width;
        return Column switch { 0 => EdgeMargin, 2 => W - total - EdgeMargin, _ => W / 2 - total / 2 };
    }

    /// <summary>Island y within the window: hugs the top edge, or the bottom of the window when docked below.</summary>
    private double IslandY(double h, double offset)
    {
        double H = Root.ActualHeight > 0 ? Root.ActualHeight : Height;
        return AtBottom ? H - h - offset : offset;
    }

    // ---- drag ----

    private void ArmIslandDrag()
    {
        GetCursorPos(out _islandDragStart);
        _dragWinLeft = Left; _dragWinTop = Top;
        _dragArmed = true;
        _islandDragging = false;
        _dragPoll.Tick -= DragPollTick;
        _dragPoll.Tick += DragPollTick;
        _dragPoll.Start();
    }

    private void DragPollTick(object? s, EventArgs e)
    {
        GetCursorPos(out var p);
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (!down) { _dragPoll.Stop(); _dragArmed = false; if (_islandDragging) EndIslandDrag(p); return; }

        double dx = p.X - _islandDragStart.X, dy = p.Y - _islandDragStart.Y;
        if (!_islandDragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 10 || _approvals.Count > 0 || Asking) return;
            _islandDragging = true;
            _expand = Expand.None;          // fold to the sliver while carrying it
            _hoverIntent.Stop();
            Refresh();
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragWinLeft + dx / dpi.DpiScaleX;
        Top = _dragWinTop + dy / dpi.DpiScaleY;
    }

    private void CancelIslandDrag() { _dragPoll.Stop(); _dragArmed = false; }

    private void FinishIslandDrag()
    {
        CancelIslandDrag();
        GetCursorPos(out var p);
        EndIslandDrag(p);
    }

    private void EndIslandDrag(POINT p)
    {
        _islandDragging = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        double x = p.X / dpi.DpiScaleX, y = p.Y / dpi.DpiScaleY;
        var wa = SystemParameters.WorkArea;
        int col = x < wa.Left + wa.Width / 3 ? 0 : x > wa.Left + wa.Width * 2 / 3 ? 2 : 1;
        bool bottom = y > wa.Top + wa.Height / 2;
        AppSettings.Current.Dock = (IslandDock)((bottom ? 3 : 0) + col);
        AppSettings.Current.Save();
        _scale.Target = 1;
        PlaceWindow(animate: true);
        StartFrames();
    }

    /// <summary>Called from the home window to put the island back on top centre.</summary>
    public void ResetDock()
    {
        AppSettings.Current.Dock = IslandDock.TopCenter;
        AppSettings.Current.Save();
        PlaceWindow(animate: true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}

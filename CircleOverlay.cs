using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DynamicIsland;

/// <summary>
/// Circle-to-ask: freezes the screen, lets you draw around anything, then asks for your question
/// by voice (or typing). Raises Completed with the cropped PNG and the question.
/// </summary>
public sealed class CircleOverlay : Window
{
    private readonly System.Drawing.Bitmap _shot;
    private readonly double _scaleX, _scaleY;
    private readonly Canvas _canvas = new();
    private readonly Polyline _stroke;
    private readonly Border _panel;
    private readonly TextBlock _status = new();
    private readonly TextBox _input = new();
    private readonly Mascot _mascot = new();
    private readonly CancellationTokenSource _listenCancel = new();
    private bool _drawing, _circled, _done;
    private Rect _region;

    public event Action<byte[], string>? Completed;

    public CircleOverlay()
    {
        // Freeze the primary screen before the overlay appears, so the crop never includes it.
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        _shot = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(_shot))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _scaleX = bounds.Width / Width;
        _scaleY = bounds.Height / Height;
        Cursor = Cursors.Pen;
        Background = Brushes.Black;

        var frozen = new Image { Source = ToBitmapSource(_shot), Stretch = Stretch.Fill, Width = Width, Height = Height };
        var dim = new Rectangle { Width = Width, Height = Height, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x05, 0x08, 0x14)) };

        _stroke = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0xE4, 0xFF)),
            StrokeThickness = 3.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x3C, 0xB4, 0xFF), BlurRadius = 16, ShadowDepth = 0, Opacity = 1 },
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0C, 0x10)),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 9, 18, 9),
            Child = new TextBlock
            {
                Text = "Circle anything to ask Claude about it  ·  Esc to cancel",
                Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            },
        };
        hint.Loaded += (_, _) => { Canvas.SetLeft(hint, (Width - hint.ActualWidth) / 2); Canvas.SetTop(hint, 56); };

        _panel = BuildPanel();
        _panel.Visibility = Visibility.Collapsed;

        _canvas.Children.Add(frozen);
        _canvas.Children.Add(dim);
        _canvas.Children.Add(_stroke);
        _canvas.Children.Add(hint);
        _canvas.Children.Add(_panel);
        Content = _canvas;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
        Closed += (_, _) => { _listenCancel.Cancel(); _shot.Dispose(); };
    }

    private Border BuildPanel()
    {
        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _status.Foreground = Brushes.White; _status.FontSize = 13.5; _status.FontWeight = FontWeights.SemiBold; _status.FontFamily = font;
        _status.TextWrapping = TextWrapping.Wrap;
        _input.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        _input.Foreground = Brushes.White; _input.CaretBrush = Brushes.White; _input.BorderThickness = new Thickness(0);
        _input.FontSize = 13; _input.FontFamily = font; _input.Padding = new Thickness(10, 7, 10, 7);
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _input.Text.Trim().Length > 0) Finish(_input.Text.Trim());
            if (e.Key == Key.Escape) Cancel();
        };
        _input.TextChanged += (_, _) => { if (_input.IsFocused && _input.Text.Length > 0) _listenCancel.Cancel(); }; // typing beats listening

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Viewbox { Width = 40, Height = 40, Child = _mascot });
        row.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 260, Children = { _status } });

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true, Child = _input });

        return new Border
        {
            Width = 340,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x0C, 0x10)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x3C, 0xB4, 0xFF)), BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(16),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0x3C, 0xB4, 0xFF), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 },
            Child = stack,
        };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_circled) return;
        _drawing = true;
        _stroke.Points.Clear();
        _stroke.Points.Add(e.GetPosition(_canvas));
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drawing) _stroke.Points.Add(e.GetPosition(_canvas));
    }

    private async void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        ReleaseMouseCapture();

        var pts = _stroke.Points;
        if (pts.Count < 3) { _stroke.Points.Clear(); return; }
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        if (maxX - minX < 16 || maxY - minY < 16) { _stroke.Points.Clear(); return; }

        _stroke.Points.Add(pts[0]); // close the loop
        _region = new Rect(new Point(minX, minY), new Point(maxX, maxY));
        _circled = true;
        Cursor = Cursors.Arrow;

        // Put the question panel beside the circle, kept on screen.
        _panel.Visibility = Visibility.Visible;
        _panel.UpdateLayout();
        double x = maxX + 16 + 340 < Width ? maxX + 16 : Math.Max(16, minX - 16 - 340);
        double y = Math.Clamp(minY, 16, Height - _panel.ActualHeight - 16);
        Canvas.SetLeft(_panel, x);
        Canvas.SetTop(_panel, y);
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        await Listen();
    }

    private async Task Listen()
    {
        _mascot.SetMood(Mood.Curious);
        _status.Text = "Listening… ask your question";
        Keyboard.Focus(this);
        try
        {
            var text = await Voice.ListenAsync(_listenCancel.Token);
            if (_done) return;
            if (text != null) { _input.Text = text; _status.Text = "“" + text + "”"; Finish(text); return; }
            _status.Text = "I didn't catch that. Type your question:";
        }
        catch (OperationCanceledException) { if (!_done) _status.Text = "Type your question:"; }
        catch (Exception ex) { if (!_done) _status.Text = ex.Message + "  Or type it:"; }
        _mascot.SetMood(Mood.Idle);
        _input.Focus();
    }

    private void Finish(string question)
    {
        if (_done) return;
        _done = true;
        _listenCancel.Cancel();
        byte[] png = Crop();
        Close();
        Completed?.Invoke(png, question);
    }

    private void Cancel()
    {
        _done = true;
        Close();
    }

    private byte[] Crop()
    {
        const int pad = 12;
        int x = (int)Math.Max(0, _region.X * _scaleX - pad), y = (int)Math.Max(0, _region.Y * _scaleY - pad);
        int w = (int)Math.Min(_shot.Width - x, _region.Width * _scaleX + 2 * pad), h = (int)Math.Min(_shot.Height - y, _region.Height * _scaleY + 2 * pad);
        using var crop = _shot.Clone(new System.Drawing.Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)), _shot.PixelFormat);
        using var ms = new MemoryStream();
        crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }
}

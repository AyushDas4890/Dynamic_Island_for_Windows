using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland;

public enum Mood { Idle, Focused, Curious, Happy, Dizzy, Love }

/// <summary>
/// Robot companion: round white head, black visor with soft glowing eyes, antenna and ear pods.
/// Moods change the eyes and head motion; the antenna tip takes the island's state colour.
/// Poking it five times quickly makes it dizzy.
/// </summary>
public sealed class Mascot : Canvas
{
    private const double S = 40;
    private static readonly Color EyeColor = Color.FromRgb(0xE4, 0xFF, 0xD6);

    private readonly Path _leftEye = new(), _rightEye = new();
    private readonly Ellipse _antennaTip = new();
    private readonly ScaleTransform _eyeScale = new(1, 1, 20, 22.5);
    private readonly RotateTransform _tilt = new(0, S / 2, S);
    private readonly TranslateTransform _bob = new();
    private readonly System.Windows.Threading.DispatcherTimer _blink = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly System.Windows.Threading.DispatcherTimer _recover = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<DateTime> _pokes = new();
    private Mood _mood = Mood.Idle, _beforeDizzy = Mood.Idle;

    public Mascot()
    {
        Width = S; Height = S;
        RenderTransform = new TransformGroup { Children = { _tilt, _bob } };

        var white = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xCF, 0xD6, 0xDE), 90);
        var ear = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xE4));

        // Antenna: slim curved stem and a round tip.
        Add(new Path { Data = Geometry.Parse("M20,11 C20,8.5 19.4,7.5 20,5.8"), Stroke = ear, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round });
        _antennaTip.Width = _antennaTip.Height = 4.6;
        _antennaTip.Fill = Brushes.White;
        Place(_antennaTip, 17.7, 1.6);

        // Ears.
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = ear }, 1.6, 18);
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = ear }, 34, 18);

        // Head.
        Place(new Ellipse { Width = 31, Height = 27, Fill = white }, 4.5, 10.5);

        // Visor with a soft reflection bar.
        Place(new Rectangle
        {
            Width = 23, Height = 13.5, RadiusX = 6.75, RadiusY = 6.75,
            Fill = new LinearGradientBrush(Color.FromRgb(0x23, 0x27, 0x2E), Color.FromRgb(0x0A, 0x0B, 0x0E), 90),
        }, 8.5, 16);
        Place(new Rectangle { Width = 9, Height = 1.8, RadiusX = 0.9, RadiusY = 0.9, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x6A, 0x70, 0x7A)) }, 11, 17.4);

        foreach (var eye in new[] { _leftEye, _rightEye })
        {
            eye.StrokeStartLineCap = eye.StrokeEndLineCap = PenLineCap.Round;
            eye.StrokeLineJoin = PenLineJoin.Round;
            eye.RenderTransform = _eyeScale;
            Add(eye);
        }

        _blink.Tick += (_, _) => Blink();
        _recover.Tick += (_, _) => { _recover.Stop(); _mood = Mood.Idle; SetMood(_beforeDizzy); };
        Loaded += (_, _) => { _blink.Start(); Apply(); };
        Unloaded += (_, _) => _blink.Stop();
    }

    public Mood Mood => _mood;

    /// <summary>The antenna tip glows in the island's current state colour.</summary>
    public void SetGlow(Color c) => _antennaTip.Fill = new SolidColorBrush(c);

    public void SetMood(Mood mood)
    {
        if (mood == _mood || _recover.IsEnabled) return;
        _mood = mood;
        Apply();
    }

    /// <summary>Five pokes within three seconds: dizzy for a moment.</summary>
    public void Poke()
    {
        var now = DateTime.Now;
        _pokes.RemoveAll(t => now - t > TimeSpan.FromSeconds(3));
        _pokes.Add(now);
        Squish();
        if (_pokes.Count >= 5 && _mood != Mood.Dizzy)
        {
            _beforeDizzy = _mood;
            _pokes.Clear();
            _mood = Mood.Dizzy;
            Apply();
            _recover.Start();
        }
    }

    private void Apply()
    {
        _tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        _bob.BeginAnimation(TranslateTransform.YProperty, null);
        _tilt.Angle = 0; _bob.Y = 0;

        switch (_mood)
        {
            case Mood.Idle: // soft pill eyes
                Filled(Pill(16, 22.6, 1.9, 2.8), Pill(24, 22.6, 1.9, 2.8));
                break;
            case Mood.Focused: // wide, low eyes; gentle work bob
                Filled(Pill(16, 23.2, 2.3, 1.7), Pill(24, 23.2, 2.3, 1.7));
                Loop(_bob, TranslateTransform.YProperty, 0, -1.2, 0.6);
                break;
            case Mood.Curious: // one eye bigger, head tilt
                Filled(Pill(16, 23, 1.7, 2.4), Pill(24, 22.4, 2.3, 3.2));
                _tilt.Angle = -8;
                break;
            case Mood.Happy: // ^ ^
                Stroked("M13.6,23.8 Q16,20.6 18.4,23.8", "M21.6,23.8 Q24,20.6 26.4,23.8", 1.5);
                Loop(_bob, TranslateTransform.YProperty, 0, -2.6, 0.3);
                break;
            case Mood.Love: // pink hearts
                Filled(Heart(16, 22.8), Heart(24, 22.8), Color.FromRgb(0xFF, 0x5C, 0x93));
                Loop(_bob, TranslateTransform.YProperty, 0, -1.6, 0.45);
                break;
            case Mood.Dizzy: // spirals and a wobble
                Stroked(Spiral(16, 22.8), Spiral(24, 22.8), 1.1);
                Loop(_tilt, RotateTransform.AngleProperty, -10, 10, 0.24);
                break;
        }
    }

    private static string Pill(double cx, double cy, double rx, double ry) =>
        FormattableString.Invariant($"M{cx - rx},{cy} a{rx},{ry} 0 1,0 {2 * rx},0 a{rx},{ry} 0 1,0 {-2 * rx},0 Z");

    private static string Heart(double cx, double cy) => FormattableString.Invariant(
        $"M{cx},{cy + 2.4} C{cx - 3.6},{cy - 0.2} {cx - 2.2},{cy - 3.2} {cx},{cy - 1.3} C{cx + 2.2},{cy - 3.2} {cx + 3.6},{cy - 0.2} {cx},{cy + 2.4} Z");

    private static string Spiral(double cx, double cy) => FormattableString.Invariant(
        $"M{cx},{cy} a0.7,0.7 0 1,1 0.7,0.7 a1.5,1.5 0 1,1 -1.6,-1.7 a2.3,2.3 0 1,1 2.6,2.4");

    private void Filled(string left, string right, Color? color = null)
    {
        var brush = new SolidColorBrush(color ?? EyeColor);
        foreach (var (eye, data) in new[] { (_leftEye, left), (_rightEye, right) })
        {
            eye.Data = Geometry.Parse(data);
            eye.Fill = brush;
            eye.Stroke = null;
        }
    }

    private void Stroked(string left, string right, double thickness)
    {
        var brush = new SolidColorBrush(EyeColor);
        foreach (var (eye, data) in new[] { (_leftEye, left), (_rightEye, right) })
        {
            eye.Data = Geometry.Parse(data);
            eye.Fill = null;
            eye.Stroke = brush;
            eye.StrokeThickness = thickness;
        }
    }

    private void Blink()
    {
        if (_mood is not (Mood.Idle or Mood.Curious or Mood.Focused)) return;
        _eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true });
        _blink.Interval = TimeSpan.FromSeconds(2.8 + Random.Shared.NextDouble() * 2.5);
    }

    private void Squish()
    {
        var st = new ScaleTransform(1, 1, S / 2, S);
        var group = (TransformGroup)RenderTransform;
        if (group.Children.Count > 2) group.Children.RemoveAt(2);
        group.Children.Add(st);
        var ease = new ElasticEase { Oscillations = 2, Springiness = 5 };
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.1, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    private void Add(UIElement e) => Children.Add(e);
    private void Place(UIElement e, double x, double y) { SetLeft(e, x); SetTop(e, y); Children.Add(e); }

    private static void Loop(IAnimatable target, DependencyProperty prop, double from, double to, double seconds)
        => target.BeginAnimation(prop, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
}

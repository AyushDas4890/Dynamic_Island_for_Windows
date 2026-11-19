using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland;

public enum Mood { Idle, Focused, Curious, Happy, Dizzy, Love }

/// <summary>Which companion is drawn. Chosen in Island settings; every mascot in the app follows it.</summary>
public enum MascotKind { Robo, SpiderBot, Spider }

/// <summary>
/// The companion: Robo (white head, black visor), Spider-Bot (red web-masked head, white lenses) or
/// Spider (a little black spider hanging on its thread). All share the same mood eyes:
/// moods change the eyes and head motion; the antenna/thread tip takes the island's state colour.
/// Poking it five times quickly makes it dizzy.
/// </summary>
public sealed class Mascot : Canvas
{
    private const double S = 40;
    private static readonly Color RoboEye = Color.FromRgb(0xE4, 0xFF, 0xD6);

    private readonly Path _leftEye = new(), _rightEye = new();
    private readonly Ellipse _antennaTip = new();
    private readonly ScaleTransform _eyeScale = new(1, 1, 20, 22.5);
    private readonly ScaleTransform _kindScale = new(1, 1, 20, 22.6);
    private readonly RotateTransform _tilt = new(0, S / 2, S);
    private readonly TranslateTransform _bob = new();
    private readonly System.Windows.Threading.DispatcherTimer _blink = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly System.Windows.Threading.DispatcherTimer _recover = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<DateTime> _pokes = new();
    private Mood _mood = Mood.Idle, _beforeDizzy = Mood.Idle;
    private MascotKind? _fixedKind;
    private MascotKind _built = (MascotKind)(-1);
    private Color _eyeFill = RoboEye;
    private Brush? _eyeStroke;

    public Mascot()
    {
        Width = S; Height = S;
        RenderTransform = new TransformGroup { Children = { _tilt, _bob } };
        foreach (var eye in new[] { _leftEye, _rightEye })
        {
            eye.StrokeStartLineCap = eye.StrokeEndLineCap = PenLineCap.Round;
            eye.StrokeLineJoin = PenLineJoin.Round;
            eye.RenderTransform = new TransformGroup { Children = { _kindScale, _eyeScale } };
        }
        Build();

        _blink.Tick += (_, _) => Blink();
        _recover.Tick += (_, _) => { _recover.Stop(); _mood = Mood.Idle; SetMood(_beforeDizzy); };
        Loaded += (_, _) => { _blink.Start(); AppSettings.Changed += OnSettings; Build(); Apply(); };
        Unloaded += (_, _) => { _blink.Stop(); AppSettings.Changed -= OnSettings; };
    }

    /// <summary>Draw a specific kind regardless of the setting (used by the picker previews).</summary>
    public MascotKind? FixedKind { get => _fixedKind; set { _fixedKind = value; Build(); Apply(); } }

    private MascotKind Kind => _fixedKind ?? AppSettings.Current.Mascot;

    private void OnSettings() { if (Kind != _built) { Build(); Apply(); } }

    private void Build()
    {
        var kind = Kind;
        if (kind == _built) return;
        _built = kind;
        var glow = _antennaTip.Fill;
        Children.Clear();
        _kindScale.ScaleX = _kindScale.ScaleY = 1;
        _eyeStroke = null;
        switch (kind)
        {
            case MascotKind.SpiderBot: BuildSpiderBot(); break;
            case MascotKind.Spider: BuildSpider(); break;
            default: BuildRobo(); break;
        }
        _antennaTip.Fill = glow ?? Brushes.White;
        Add(_leftEye); Add(_rightEye);
    }

    private void BuildRobo()
    {
        var white = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xCF, 0xD6, 0xDE), 90);
        var ear = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xE4));
        Antenna(ear);
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = ear }, 1.6, 18);
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = ear }, 34, 18);
        Place(new Ellipse { Width = 31, Height = 27, Fill = white }, 4.5, 10.5);
        Place(new Rectangle
        {
            Width = 23, Height = 13.5, RadiusX = 6.75, RadiusY = 6.75,
            Fill = new LinearGradientBrush(Color.FromRgb(0x23, 0x27, 0x2E), Color.FromRgb(0x0A, 0x0B, 0x0E), 90),
        }, 8.5, 16);
        Place(new Rectangle { Width = 9, Height = 1.8, RadiusX = 0.9, RadiusY = 0.9, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x6A, 0x70, 0x7A)) }, 11, 17.4);
        _eyeFill = RoboEye;
    }

    /// <summary>Red web-masked robot head with big white lenses and blue ear pods.</summary>
    private void BuildSpiderBot()
    {
        var blue = new SolidColorBrush(Color.FromRgb(0x1F, 0x4F, 0xB8));
        Antenna(blue);
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = blue }, 1.6, 18);
        Place(new Rectangle { Width = 4.4, Height = 9, RadiusX = 2, RadiusY = 2, Fill = blue }, 34, 18);
        Place(new Ellipse
        {
            Width = 31, Height = 27,
            Fill = new LinearGradientBrush(Color.FromRgb(0xF0, 0x3A, 0x42), Color.FromRgb(0xA3, 0x15, 0x1C), 90),
        }, 4.5, 10.5);
        // Web: spokes from the face centre plus two curved rings, clipped to the head.
        var web = new Path
        {
            Data = Geometry.Parse(
                "M20,23 L20,10 M20,23 L9,13 M20,23 L31,13 M20,23 L4,22 M20,23 L36,22 M20,23 L7,33 M20,23 L33,33 M20,23 L20,38 " +
                "M14,16 Q20,18 26,16 M10,23 Q14,20 15,15 M30,23 Q26,20 25,15 M11,30 Q16,28 20,31 Q24,28 29,30 " +
                "M9,17 Q20,22 31,17 M6,26 Q12,24 12,18 M34,26 Q28,24 28,18"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x3A, 0x05, 0x08)), StrokeThickness = 0.55,
            Clip = new EllipseGeometry(new Point(20, 24), 15.5, 13.5),
        };
        Add(web);
        _kindScale.ScaleX = _kindScale.ScaleY = 1.75; // big lenses
        _eyeFill = Colors.White;
        _eyeStroke = Brushes.Black;
    }

    /// <summary>The template's little black spider hanging from its thread.</summary>
    private void BuildSpider()
    {
        // Black ink on light pages (like the template); light ink on the black island so it stays visible.
        var ink = new SolidColorBrush(OnLight ? Color.FromRgb(0x0E, 0x0E, 0x10) : Color.FromRgb(0xEC, 0xEC, 0xEE));
        Add(new Line { X1 = 20, Y1 = 0, X2 = 20, Y2 = 14, Stroke = ink, StrokeThickness = 0.7 });
        _antennaTip.Width = _antennaTip.Height = 3.4;
        Place(_antennaTip, 18.3, -1);
        var legs = "M14.5,19 L9,12 L8,5 M13.5,22 L5.5,18.5 L3,11 M13.5,25.5 L5.5,28 L3.5,35 M15,28.5 L10.5,33 L10,39 " +
                   "M25.5,19 L31,12 L32,5 M26.5,22 L34.5,18.5 L37,11 M26.5,25.5 L34.5,28 L36.5,35 M25,28.5 L29.5,33 L30,39";
        Add(new Path { Data = Geometry.Parse(legs), Stroke = ink, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Triangle });
        Place(new Ellipse { Width = 15, Height = 17, Fill = ink }, 12.5, 14.5);
        Add(new Path { Data = Geometry.Parse("M18.3,31 L18.3,33.4 M21.7,31 L21.7,33.4"), Stroke = ink, StrokeThickness = 1.1, StrokeEndLineCap = PenLineCap.Round });
        _kindScale.ScaleX = _kindScale.ScaleY = 0.85;
        _eyeFill = OnLight ? Colors.White : Color.FromRgb(0x11, 0x11, 0x13);
    }

    /// <summary>True when drawn on a light background (home window); the Spider then uses black ink.</summary>
    public bool OnLight { get => _onLight; set { _onLight = value; _built = (MascotKind)(-1); Build(); Apply(); } }
    private bool _onLight;

    private void Antenna(Brush stem)
    {
        Add(new Path { Data = Geometry.Parse("M20,11 C20,8.5 19.4,7.5 20,5.8"), Stroke = stem, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round });
        _antennaTip.Width = _antennaTip.Height = 4.6;
        Place(_antennaTip, 17.7, 1.6);
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
        var brush = new SolidColorBrush(color ?? _eyeFill);
        foreach (var (eye, data) in new[] { (_leftEye, left), (_rightEye, right) })
        {
            eye.Data = Geometry.Parse(data);
            eye.Fill = brush;
            eye.Stroke = _eyeStroke;
            eye.StrokeThickness = 0.45;
        }
    }

    private void Stroked(string left, string right, double thickness)
    {
        // On the light Spider-Bot lenses, line eyes are drawn in black.
        var brush = new SolidColorBrush(_eyeStroke != null ? Colors.Black : _eyeFill);
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

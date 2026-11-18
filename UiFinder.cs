using System.Runtime.InteropServices;
using System.Windows.Automation;
using Rect = System.Drawing.Rectangle;

namespace DynamicIsland;

/// <summary>
/// Snaps Claude's rough pointing guess to a real control: searches the taskbar, the window under the
/// guess and the foreground window via UI Automation for an element whose name matches, and returns
/// the closest one's exact bounds (physical pixels).
/// </summary>
public static class UiFinder
{
    public static Rect? Find(string name, int x, int y)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var search = Task.Run(() => Search(name, x, y));
        return search.Wait(TimeSpan.FromSeconds(2.5)) ? search.Result : null; // big UI trees can be slow
    }

    private static Rect? Search(string name, int x, int y)
    {
        var roots = new List<AutomationElement>();
        void Add(IntPtr h) { if (h != IntPtr.Zero) try { roots.Add(AutomationElement.FromHandle(h)); } catch (ElementNotAvailableException) { } }
        Add(FindWindow("Shell_TrayWnd", null));
        Add(GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2));
        Add(GetForegroundWindow());

        Rect? best = null;
        double bestScore = double.MaxValue;
        var seen = new HashSet<Rect>();
        foreach (var root in roots)
        {
            AutomationElementCollection found;
            try { found = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
            catch (ElementNotAvailableException) { continue; }
            foreach (AutomationElement el in found)
            {
                string n;
                System.Windows.Rect b;
                try { n = el.Current.Name ?? ""; b = el.Current.BoundingRectangle; }
                catch (ElementNotAvailableException) { continue; }
                if (n.Length == 0 || b.IsEmpty || b.Width < 4 || b.Height < 4 || b.Width > 900 || b.Height > 500) continue;
                bool exact = n.Equals(name, StringComparison.OrdinalIgnoreCase);
                if (!exact && !n.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                var r = new Rect((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
                if (!seen.Add(r)) continue;
                double dx = b.X + b.Width / 2 - x, dy = b.Y + b.Height / 2 - y;
                // Exact name matches always beat partial ones; distance only breaks ties within a class.
                double score = Math.Sqrt(dx * dx + dy * dy) + (exact ? 0 : 1_000_000);
                if (score < bestScore) { bestScore = score; best = r; }
            }
        }
        MainWindow.JLog($"uifinder '{name}' near {x},{y} → {(best?.ToString() ?? "none")}");
        return best;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DynamicIsland;

/// <summary>Small, safe PC actions exposed to Claude over MCP (open things, media keys).</summary>
public static class PcControl
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calculator"] = "calc", ["file explorer"] = "explorer", ["files"] = "explorer",
        ["settings"] = "ms-settings:", ["browser"] = "https://www.bing.com", ["edge"] = "msedge",
        ["paint"] = "mspaint", ["terminal"] = "wt", ["task manager"] = "taskmgr", ["word"] = "winword",
        ["spotify"] = "spotify:", ["camera"] = "microsoft.windows.camera:", ["mail"] = "outlookmail:",
    };

    public static string Open(string target)
    {
        target = target.Trim().Trim('"');
        if (target.Length == 0) return "Nothing to open.";
        var what = Aliases.TryGetValue(target, out var a) ? a : target;
        try
        {
            Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
            return "Opened " + target + ".";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            // Not on PATH: try a Start-menu shortcut with that name.
            var lnk = FindShortcut(target);
            if (lnk != null) { Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true }); return "Opened " + target + "."; }
            return "Couldn't find " + target + " on this PC.";
        }
    }

    /// <summary>Speakable app names: aliases plus Start-menu shortcut names (lower-case, e.g. "google chrome").</summary>
    public static IEnumerable<string> AppNames()
    {
        var names = new HashSet<string>(Aliases.Keys.Select(k => k.ToLowerInvariant()))
        { "chrome", "notepad", "calculator", "spotify", "settings", "file explorer", "task manager" };
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                var n = new string(Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
                if (n.Length > 1 && n.Length < 40 && !n.Contains("uninstall")) names.Add(n);
            }
        }
        return names;
    }

    /// <summary>Handles a spoken command directly (no Claude round trip). Returns the reply, or null if it isn't a simple command.</summary>
    public static string? TryCommand(string said)
    {
        var s = said.Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
        foreach (var verb in new[] { "open ", "launch ", "start ", "run " })
            if (s.StartsWith(verb))
            {
                var app = s[verb.Length..].Trim();
                var r = Open(app);
                return r.StartsWith("Opened") ? "Opening " + app + "." : null; // unknown: let Claude figure it out
            }
        string? action = s switch
        {
            "play" or "pause" or "play music" or "pause music" or "resume music" => "play_pause",
            "next song" or "next track" or "skip" => "next",
            "previous song" or "previous track" => "previous",
            "volume up" or "turn it up" => "volume_up",
            "volume down" or "turn it down" => "volume_down",
            "mute" => "mute",
            _ => null,
        };
        if (action == null) return null;
        Media(action, action.StartsWith("volume") ? 5 : 1);
        return "Done.";
    }

    private static string? FindShortcut(string name)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (!Directory.Exists(root)) continue;
            var hit = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string Media(string action, int times)
    {
        byte vk = action switch
        {
            "play_pause" => 0xB3, "next" => 0xB0, "previous" => 0xB1,
            "volume_up" => 0xAF, "volume_down" => 0xAE, "mute" => 0xAD, _ => 0,
        };
        if (vk == 0) return "Unknown action " + action;
        for (int i = 0; i < Math.Clamp(times, 1, 50); i++)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2 /* KEYUP */, UIntPtr.Zero);
        }
        return "Done: " + action + ".";
    }
}

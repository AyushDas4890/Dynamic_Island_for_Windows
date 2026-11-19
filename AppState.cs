using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>User settings, saved as JSON in %APPDATA%\DynamicIsland\settings.json.</summary>
public sealed class AppSettings
{
    // Island features
    public bool ShowMusic { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowBluetooth { get; set; } = true;
    public bool ShowCharging { get; set; } = true;
    public bool ShowPrivacyDots { get; set; } = true;
    public bool Sounds { get; set; } = true;

    // Claude Code
    public bool ShowClaudeActivity { get; set; } = true;
    public bool AutoAllowReadOnly { get; set; } = false;
    public int ApprovalTimeoutSeconds { get; set; } = 120;

    // Circle to ask (Ctrl+Shift+Space) and voice
    public bool CircleHotkey { get; set; } = true;
    public bool SpeakAnswers { get; set; } = true;

    // Jarvis: "Hey Jarvis" wake word, Claude Code + MCP as the brain. Ctrl+Shift+J turns it all on/off.
    public bool JarvisEnabled { get; set; } = true;

    // Look and place
    public MascotKind Mascot { get; set; } = MascotKind.Robo;
    /// <summary>Where the island docks: top/bottom + left/center/right. Drag the island to change it.</summary>
    public IslandDock Dock { get; set; } = IslandDock.TopCenter;

    public bool FirstRunDone { get; set; }

    private static string PathOnDisk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "settings.json");

    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    private static AppSettings Load()
    {
        try { return File.Exists(PathOnDisk) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathOnDisk)) ?? new() : new(); }
        catch (Exception) { return new(); } // unreadable settings: start from defaults
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke();
    }
}

/// <summary>One line of Claude Code history shown in the home window.</summary>
public sealed record HistoryEntry(DateTime Time, string Project, string Text, string Kind)
{
    public string TimeText => Time.ToString("t");
}

/// <summary>State shared by the island and the home window (UI thread only).</summary>
public static class AppState
{
    public static ObservableCollection<ApprovalRequest> Pending { get; } = new();
    public static ObservableCollection<HistoryEntry> History { get; } = new();

    public static void Log(string project, string text, string kind)
    {
        History.Insert(0, new HistoryEntry(DateTime.Now, project, text, kind));
        while (History.Count > 200) History.RemoveAt(History.Count - 1);
    }
}

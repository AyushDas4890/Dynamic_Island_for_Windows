using System.Media;
using System.Text.Json.Nodes;

namespace DynamicIsland;

/// <summary>
/// Jarvis, HeyClicky style. engine\jarvis.py hears ("hey jarvis" via openWakeWord, faster-whisper) and
/// speaks (Kokoro); the island is the brain (Claude Code + MCP) and the cursor buddy is the face.
/// On wake it screenshots the screen, so Claude can see what you see and point at it.
/// Ctrl+Shift+J (or the tray) kills or revives all of it.
/// </summary>
public partial class MainWindow
{
    private const int JarvisHotkeyId = 0x4A41; // "JA"
    private JarvisEngine? _engine;
    private BuddyWindow? _buddy;
    private CancellationTokenSource? _jarvisCancel;
    private (int W, int H)? _jarvisScreen;
    private bool _jarvisBusy;

    public bool JarvisOn => _engine?.Running == true;

    public void ToggleJarvis()
    {
        bool on = !JarvisOn;
        AppSettings.Current.JarvisEnabled = on;
        AppSettings.Current.Save();
        if (on) StartJarvis(announce: true); else StopJarvis(announce: true);
    }

    private void StartJarvis(bool announce)
    {
        if (JarvisOn || !AppSettings.Current.JarvisEnabled) return;
        _buddy ??= new BuddyWindow();
        _engine?.Dispose();
        _engine = new JarvisEngine(Dispatcher);
        _engine.Event += (ev, msg) => OnEngine(ev, msg, announce);
        try { _engine.Start(); JLog("engine starting"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            JLog("engine failed: " + ex.Message);
            OnAgentActivity(new AgentEvent("notify", "Jarvis", "Jarvis can't start", ex.Message));
        }
    }

    private void StopJarvis(bool announce)
    {
        _jarvisCancel?.Cancel();
        _engine?.Dispose();
        _engine = null;
        _buddy?.HideNow();
        _jarvisBusy = false;
        if (announce) OnAgentActivity(new AgentEvent("notify", "Jarvis", "Jarvis offline", "Mic released. Ctrl+Shift+J to bring me back."));
    }

    private void OnPointAt(int x, int y, string label, System.Drawing.Rectangle? element)
    {
        JLog($"point_at {x},{y} '{label}' snapped={element}");
        (_buddy ??= new BuddyWindow()).PointAt(x, y, label, element);
    }

    private void OnEngine(string ev, JsonObject msg, bool announce)
    {
        switch (ev)
        {
            case "ready":
                if (announce) { OnAgentActivity(new AgentEvent("notify", "Jarvis", "Jarvis online", "Say “Hey Jarvis”. Ctrl+Shift+J turns me off.")); _engine?.Say("Online and ready, sir."); }
                break;
            case "wake":
                if (_jarvisBusy) { _jarvisCancel?.Cancel(); _jarvisBusy = false; } // a new question interrupts the old one
                _engine?.StopSpeaking();
                Chime(SystemSounds.Asterisk);
                try { _jarvisScreen = AskClaude.CaptureScreenForJarvis(); } // what the user sees right now
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.IO.IOException) { _jarvisScreen = null; }
                _buddy?.Listening();
                OnAgentActivity(new AgentEvent("working", "Jarvis", "Listening…", "Go ahead, I'm listening."));
                break;
            case "level":
                _buddy?.Level((double?)msg["value"] ?? 0);
                break;
            case "nothing":
                _buddy?.Saying("didn't catch that");
                _buddy?.Done(2);
                OnAgentActivity(new AgentEvent("done", "Jarvis", "Done", "Didn't catch that."));
                break;
            case "heard":
                HandleRequest((string?)msg["text"] ?? "");
                break;
            case "spoken":
                _buddy?.Done();
                break;
            case "error":
                JLog("engine error: " + msg["message"]);
                break;
            case "exited":
                if (_engine != null && AppSettings.Current.JarvisEnabled)
                {
                    JLog("engine exited unexpectedly");
                    _engine = null;
                    _buddy?.HideNow();
                    OnAgentActivity(new AgentEvent("notify", "Jarvis", "Jarvis stopped", "The voice engine closed. Ctrl+Shift+J to restart it."));
                }
                break;
        }
    }

    private async void HandleRequest(string request)
    {
        JLog("request: '" + request + "'");
        if (string.IsNullOrWhiteSpace(request)) return;
        _jarvisBusy = true;
        _jarvisCancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = _jarvisCancel.Token;
        try
        {
            // Simple commands run instantly; everything else goes to Claude with the screenshot.
            if (PcControl.TryCommand(request) is { } quick) { Say("“" + request + "”", quick); return; }
            _buddy?.Thinking(request);
            OnAgentActivity(new AgentEvent("working", "Jarvis", "Thinking…", "“" + request + "”"));
            var answer = await AskClaude.AskJarvis(request, ct, _jarvisScreen);
            if (!ct.IsCancellationRequested) Say("“" + request + "”", answer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            JLog("error: " + ex);
            Say("Jarvis", ex.Message);
        }
        finally { if (!ct.IsCancellationRequested) _jarvisBusy = false; }
    }

    internal static void JLog(string line)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "jarvis.log"), $"{DateTime.Now:HH:mm:ss} {line}\n"); }
        catch (System.IO.IOException) { }
    }

    private void Say(string title, string text)
    {
        JLog("say: " + text);
        OnAgentActivity(new AgentEvent("notify", "Jarvis", title, text.Length > 200 ? text[..197] + "…" : text));
        _buddy?.Saying(text);
        if (AppSettings.Current.SpeakAnswers && _engine?.Running == true) _engine.Say(text);
        else _buddy?.Done(6);
    }
}

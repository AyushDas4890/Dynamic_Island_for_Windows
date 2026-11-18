using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Runs engine\jarvis.py (openWakeWord + faster-whisper + Kokoro) as a hidden child process and
/// relays its JSON-line events to the UI thread. The island is the brain; the engine hears and speaks.
/// </summary>
public sealed class JarvisEngine : IDisposable
{
    private readonly Dispatcher _ui;
    private Process? _proc;
    public event Action<string, JsonObject>? Event;
    public bool Running => _proc is { HasExited: false };

    public JarvisEngine(Dispatcher ui) => _ui = ui;

    /// <summary>engine\ next to the exe, or in a parent folder (dev builds run from dist\ or bin\).</summary>
    public static string? EngineDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var e = Path.Combine(d.FullName, "engine");
            if (File.Exists(Path.Combine(e, "jarvis.py")) && File.Exists(Path.Combine(e, ".venv", "Scripts", "python.exe"))) return e;
        }
        return null;
    }

    public void Start()
    {
        if (Running) return;
        var dir = EngineDir() ?? throw new InvalidOperationException("Jarvis engine not found. Run engine\\setup.ps1 once.");
        var psi = new ProcessStartInfo(Path.Combine(dir, ".venv", "Scripts", "python.exe"))
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("jarvis.py");
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["HF_HUB_OFFLINE"] = "1";
        _proc = Process.Start(psi)!;
        _proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            JsonObject? msg;
            try { msg = JsonNode.Parse(e.Data) as JsonObject; } catch (System.Text.Json.JsonException) { return; }
            if (msg == null) return;
            var ev = (string?)msg["event"] ?? "";
            if (ev != "level") MainWindow.JLog("engine: " + e.Data);
            _ui.InvokeAsync(() => Event?.Invoke(ev, msg));
        };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) MainWindow.JLog("engine› " + e.Data); };
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => _ui.InvokeAsync(() => Event?.Invoke("exited", new JsonObject()));
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    private void Send(JsonObject cmd)
    {
        if (!Running) return;
        try { _proc!.StandardInput.WriteLine(cmd.ToJsonString()); _proc.StandardInput.Flush(); }
        catch (IOException) { }
    }

    public void Say(string text) => Send(new JsonObject { ["cmd"] = "say", ["text"] = text });
    public void StopSpeaking() => Send(new JsonObject { ["cmd"] = "stop" });
    public void Listen() => Send(new JsonObject { ["cmd"] = "listen" });

    public void Dispose()
    {
        if (_proc == null) return;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch (InvalidOperationException) { }
        _proc.Dispose();
        _proc = null;
    }
}

using Windows.Media.Core;
using Windows.Media.Playback;

using Windows.Media.SpeechSynthesis;

namespace DynamicIsland;

/// <summary>Jarvis-style voice: Windows speech recognition in, Windows neural/OneCore voices out.</summary>
public static class Voice
{
    private static MediaPlayer? _player;

    private static System.Speech.Recognition.SpeechRecognitionEngine? _listener;
    private static readonly object ListenerLock = new();

    /// <summary>Builds the listening engine in the background so it's ready the instant the wake word fires.</summary>
    public static void Warm() => Task.Run(() => { lock (ListenerLock) GetListener(); });

    /// <summary>Offline engine only: the WinRT online recognizer returns "Unknown" with no text for
    /// unpackaged apps, and waiting on it swallowed the first seconds of speech.</summary>
    public static Task<string?> ListenAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            lock (ListenerLock)
            {
                var engine = GetListener();
                using var reg = ct.Register(() => engine.RecognizeAsyncCancel());
                var result = engine.Recognize(TimeSpan.FromSeconds(20));
                ct.ThrowIfCancellationRequested();
                MainWindow.JLog($"heard: '{result?.Text}' {result?.Confidence:0.00} via {result?.Grammar?.Name}");
                if (result == null) return null;
                // An exact command among the alternates beats noisy dictation.
                var command = result.Alternates.FirstOrDefault(a => a.Grammar?.Name == "command" && a.Confidence >= 0.1f);
                if (command != null) return command.Text;
                return result.Text is { Length: > 0 } text ? text : null;
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("No speech recognizer is installed. Add English speech in Settings \u2192 Time & language \u2192 Speech.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No microphone found.");
        }
    }, ct);

    private static System.Speech.Recognition.SpeechRecognitionEngine GetListener()
    {
        if (_listener != null) return _listener;
        var engine = new System.Speech.Recognition.SpeechRecognitionEngine(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar { Name = "dictation", Weight = 0.4f });
        engine.LoadGrammar(new System.Speech.Recognition.Grammar(new System.Speech.Recognition.GrammarBuilder(CommandChoices())) { Name = "command", Weight = 1f, Priority = 1 });
        engine.SetInputToDefaultAudioDevice();
        engine.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
        engine.EndSilenceTimeout = TimeSpan.FromSeconds(1.0);
        return _listener = engine;
    }

    /// <summary>Exact phrases ("open google chrome", "next song", ...) built from Start-menu app names, so commands are heard reliably.</summary>
    public static System.Speech.Recognition.Choices CommandChoices()
    {
        var apps = new System.Speech.Recognition.Choices();
        foreach (var name in PcControl.AppNames()) apps.Add(name);
        var open = new System.Speech.Recognition.GrammarBuilder(new System.Speech.Recognition.Choices("open", "launch", "start", "run"));
        open.Append(apps);
        var media = new System.Speech.Recognition.Choices(
            "play", "pause", "play music", "pause music", "resume music", "next song", "next track", "skip",
            "previous song", "previous track", "volume up", "volume down", "turn it up", "turn it down", "mute");
        return new System.Speech.Recognition.Choices(open, new System.Speech.Recognition.GrammarBuilder(media));
    }

    /// <summary>Speaks text with the nicest installed voice. Stops anything already speaking.</summary>
    public static async Task SpeakAsync(string text)
    {
        Stop();
        using var synth = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices
            .OrderByDescending(v => v.DisplayName.Contains("Natural") ? 2 : v.DisplayName.Contains("Guy") || v.DisplayName.Contains("Aria") || v.DisplayName.Contains("Mark") ? 1 : 0)
            .FirstOrDefault(v => v.Language.StartsWith("en"));
        if (voice != null) synth.Voice = voice;
        synth.Options.SpeakingRate = 1.05;
        var stream = await synth.SynthesizeTextToStreamAsync(text);
        _player = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, stream.ContentType) };
        _player.Play();
    }

    public static void Stop()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
    }
}

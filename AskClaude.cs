using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace DynamicIsland;

/// <summary>Captures a window and asks Claude about it.</summary>
public static class AskClaude
{
    private const string Model = "claude-opus-5-5";
    private const string SystemPrompt =
        "You are a calm, quick-witted desktop assistant in the spirit of J.A.R.V.I.S., living in a Dynamic Island on the user's Windows screen. " +
        "The user shared a screenshot (a window, or an area they circled) and asked a question about it, often by voice. " +
        "Your answer may be read aloud: reply in natural spoken plain text (no markdown, no lists, no code blocks), " +
        "usually under 80 words, and refer to what is visible in the screenshot. Give practical advice when it helps.";

    // ---- API key: ANTHROPIC_API_KEY, else a key the user pasted, encrypted with Windows DPAPI ----

    private static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "anthropic.key");

    public static bool HasKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } || File.Exists(KeyPath);

    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static void ForgetKey() { if (File.Exists(KeyPath)) File.Delete(KeyPath); }

    private static string? LoadKey()
    {
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(env)) return env;
        if (!File.Exists(KeyPath)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser));
    }

    // ---- Claude Code (uses the user's existing Claude subscription, no API key) ----

    /// <summary>Path to the Claude Code CLI, or null if it isn't installed.</summary>
    public static string? ClaudeCodePath()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public static bool CanAsk => ClaudeCodePath() != null || HasKey;

    /// <summary>
    /// Runs `claude -p` headless in a private temp folder holding only the screenshot, with only the
    /// Read tool allowed, so Claude can look at the image and nothing else.
    /// </summary>
    private static async Task<string> AskViaClaudeCode(string exe, byte[] png, string question, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DynamicIsland", "ask-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "window.png"), png);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(SystemPrompt + "\n\nThe screenshot is the file window.png in the current folder. Read it, then answer this question:\n" + question);
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add("Read");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("text");

            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }

            string answer = (await stdout).Trim(), err = (await stderr).Trim();
            if (answer.Length > 0 && proc.ExitCode == 0) return answer;
            string why = answer.Length > 0 ? answer : err;
            if (why.Contains("auth", StringComparison.OrdinalIgnoreCase) || why.Contains("log in", StringComparison.OrdinalIgnoreCase) || why.Contains("login", StringComparison.OrdinalIgnoreCase))
                return "Claude Code isn't signed in. Open the Dynamic Island home → Claude Code → Sign in, then try again.";
            return "Claude Code couldn't answer: " + (why.Length > 0 ? why : "exit code " + proc.ExitCode);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ---- Jarvis: spoken assistant, Claude Code is the brain, the island's MCP tools are its hands ----

    private const string JarvisPrompt =
        "You are J.A.R.V.I.S., the user's voice assistant on their Windows PC, speaking through a Dynamic Island. " +
        "The user spoke the request below. Act on it with your tools when it asks for an action: pc_open opens apps, folders, files or URLs; " +
        "pc_media controls music and volume; pc_search_web searches; point_at shows the user where to click or look on their screen. " +
        "Then reply as Jarvis would: one to three short spoken sentences, plain text, no markdown, lightly witty, address the user as 'sir' sparingly.\n\nRequest: ";

    private static string JarvisDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "jarvis");

    /// <summary>Physical-pixel origin and scale of the last screen.png, so point_at maps back to the real screen.</summary>
    public static double ScreenScale { get; private set; } = 1;
    public static int ScreenLeft { get; private set; }
    public static int ScreenTop { get; private set; }

    /// <summary>Screenshot of the monitor under the mouse, saved as screen.png for Jarvis (≤1568 px on the long side).</summary>
    public static (int W, int H) CaptureScreenForJarvis()
    {
        GetCursorPos(out var p);
        var b = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).Bounds;
        using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
        double scale = Math.Min(1.0, 1568.0 / Math.Max(b.Width, b.Height));
        int w = (int)(b.Width * scale), h = (int)(b.Height * scale);
        using var small = new System.Drawing.Bitmap(bmp, w, h);
        Directory.CreateDirectory(JarvisDir);
        small.Save(Path.Combine(JarvisDir, "screen.png"), System.Drawing.Imaging.ImageFormat.Png);
        (ScreenScale, ScreenLeft, ScreenTop) = (1 / scale, b.Left, b.Top);
        return (w, h);
    }

    /// <summary>
    /// If Claude Code ever failed to reach the island (e.g. while it restarted) it caches the server as
    /// "needs auth" and silently skips it, leaving Jarvis without hands. Drop just our entry.
    /// </summary>
    private static void ForgetCachedNeedsAuth()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "mcp-needs-auth-cache.json");
        try
        {
            if (!File.Exists(path) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject cache || !cache.Remove("dynamic-island")) return;
            File.WriteAllText(path, cache.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
    }

    /// <summary>Runs one spoken request through `claude -p`, continuing the previous Jarvis conversation for memory.</summary>
    public static async Task<string> AskJarvis(string request, CancellationToken ct, (int W, int H)? screen = null)
    {
        if (screen is { } s)
            request = $"[screen.png in the current folder is a {s.W}x{s.H} screenshot of the user's screen taken as they spoke. " +
                      "If the request could be about what they see, FIRST Read screen.png and look carefully; never answer from how Windows or an app usually looks, describe only what is actually in the image. " +
                      "Whenever your answer refers to where something is on screen (where to click, look or type), you MUST call point_at with that spot's pixel coordinates in screen.png " +
                      "AND `element` set to the exact visible name or text of that control (e.g. 'Start', 'Export'), so the highlight snaps onto it; say 'here' or 'right there' in your reply.]\n" + request;
        var exe = ClaudeCodePath() ?? throw new InvalidOperationException("Claude Code isn't installed, so Jarvis has no brain yet.");
        ForgetCachedNeedsAuth();
        Directory.CreateDirectory(JarvisDir);
        var marker = Path.Combine(JarvisDir, ".started");
        bool resume = File.Exists(marker) && File.GetLastWriteTime(marker) > DateTime.Now.AddHours(-6);

        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            WorkingDirectory = JarvisDir,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        if (resume) psi.ArgumentList.Add("-c");
        foreach (var a in new[] { "-p", JarvisPrompt + request, "--allowedTools", "mcp__dynamic-island", "Read", "WebSearch", "WebFetch", "--output-format", "text" })
            psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }

        string answer = (await stdout).Trim(), err = (await stderr).Trim();
        if (answer.Length > 0 && proc.ExitCode == 0) { File.WriteAllText(marker, ""); return answer; }
        if (resume) { File.Delete(marker); return await AskJarvis(request, ct); } // request already carries the screen note // stale conversation: start fresh
        return "I couldn't reach my brain: " + (answer.Length > 0 ? answer : err.Length > 0 ? err : "exit code " + proc.ExitCode);
    }

    // ---- request ----

    /// <summary>Prefers the user's Claude Code subscription; falls back to an API key if one was added.</summary>
    public static async Task<string> AskAsync(byte[] png, string question, CancellationToken ct)
    {
        if (ClaudeCodePath() is { } exe && !HasKey) return await AskViaClaudeCode(exe, png, question, ct);

        var key = LoadKey() ?? throw new InvalidOperationException("No API key");
        AnthropicClient client = new() { ApiKey = key };

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = SystemPrompt,
            // Server-side refusal fallback: a declined request is re-served by a fallback model.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng } },
                        new BetaTextBlockParam { Text = question },
                    },
                },
            ],
        }, cancellationToken: ct);

        if (response.StopReason == "refusal")
            return "Sorry, I can't help with that one.";
        var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
        return text.Length > 0 ? text : "(No answer)";
    }

    // ---- window capture ----

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>The top-level window under the mouse pointer (null for the desktop / our own windows).</summary>
    public static IntPtr WindowUnderCursor(IEnumerable<IntPtr> ignore)
    {
        GetCursorPos(out var p);
        var hwnd = GetAncestor(WindowFromPoint(p), 2 /* GA_ROOT */);
        return ignore.Contains(hwnd) ? IntPtr.Zero : hwnd;
    }

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Screenshot of a window's visible bounds as PNG, downscaled to at most 1568 px on the long side.</summary>
    public static byte[] Capture(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));

        double scale = Math.Min(1.0, 1568.0 / Math.Max(w, h));
        using var outBmp = scale < 1 ? new System.Drawing.Bitmap(bmp, (int)(w * scale), (int)(h * scale)) : (System.Drawing.Bitmap)bmp.Clone();
        using var ms = new MemoryStream();
        outBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}

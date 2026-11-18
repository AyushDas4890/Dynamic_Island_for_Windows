<div align="center">

<img src="docs/screenshots/now-playing.png" width="460" alt="Dynamic Island showing a song with album art, waveform, progress and controls" />

# Dynamic Island for Windows

**A living, morphing pill at the top of your screen — inspired by the iPhone's Dynamic Island, built natively for Windows 11.**

It stays out of your way as a thin sliver, then springs open for music, notifications, timers, and system events.

[![Download](https://img.shields.io/badge/Download-DynamicIsland.exe-4B1F4D?style=for-the-badge&logo=windows)](https://github.com/AyushDas4890/Dynamic_Island_for_Windows/releases/latest)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0F1115?style=for-the-badge)
![.NET](https://img.shields.io/badge/.NET-8%20WPF-512BD4?style=for-the-badge)
![License](https://img.shields.io/badge/License-MIT-30D158?style=for-the-badge)

</div>

---

## Features

### 🎙️ Jarvis: talk to your PC (new in 1.2)
Say **"Hey Jarvis"** and ask anything out loud. Jarvis sees your screen, answers in a natural voice, and a little **cursor buddy flies over and highlights exactly where to click**, HeyClicky style.

| Part | What runs it |
|---|---|
| Wake word | [openWakeWord](https://github.com/dscripka/openWakeWord) "hey jarvis", on-device |
| Speech to text | [faster-whisper](https://github.com/SYSTRAN/faster-whisper) `base.en`, on-device |
| Brain | **Your Claude subscription via Claude Code**, with the island's MCP tools as hands |
| Voice | [Kokoro](https://github.com/thewh1teagle/kokoro-onnx) (free, ElevenLabs-like), on-device |

- **"Where do I click to export?"** Claude reads a screenshot taken the moment you spoke, then the buddy outlines the real button (snapped with Windows UI Automation, so it lands exactly on the control)
- **"Open Google Chrome"**, **"next song"**, **"volume up"**: simple commands run instantly; anything else goes to Claude
- New MCP tools: `point_at`, `pc_open` (apps, folders, URLs), `pc_media` (play/pause, tracks, volume), `pc_search_web`
- Remembers the conversation for follow-ups
- **Ctrl+Shift+J** turns the whole system off (mic released) and on again; also in the tray menu
- **Ctrl+Shift+Space**: freeze the screen, circle anything with a pen and ask about it

**Setup (once):** install Python 3.10+, then run `powershell -ExecutionPolicy Bypass -File engine\setup.ps1` from the source folder (downloads ~260 MB of models). Put the `engine` folder next to `DynamicIsland.exe`. Audio never leaves your PC; only your question and screenshot go to Claude.

### 🤖 Claude Code companion (new in 1.1)
Hover the notch and the island opens into a panel that follows **Claude Code** live: which file it is editing (with a red/green diff), which command it runs, and when it's done.

<img src="docs/screenshots/dashboard.png" width="720" alt="Island panel with Claude Code card, music, focus timer, clock and settings tiles" />

- **Allow / Deny** Claude's permission requests right in the island, or from the home window
- A **robot companion** whose mood follows the work: focused, curious when it needs you, happy when done, dizzy if you poke it, in love on double-click
- The notch **glows** orange while Claude works, blue when it needs you, green when it's done
- **Ask about your screen**: drag the robot onto any window and ask a question. Uses your existing **Claude subscription through Claude Code** (no API key needed)
- **Island MCP server**: Claude Code can notify you, show progress and ask you quick yes/no questions in the island
- **Home window** with an approvals queue, activity history, Claude Code sign-in and all settings
- Starts with Windows by default (turn off in settings)

**Setup:** install [Claude Code](https://code.claude.com/docs/en/setup), open Dynamic Island → **Claude Code** → **Open Claude Code to sign in** (type `/login`), then turn on **Follow Claude Code** and **Island MCP server**.

### 🎵 Now Playing
Works with **any** app that shows up in Windows' media controls: Spotify, YouTube in any browser, Windows Media Player and more.

<img src="docs/screenshots/now-playing.png" width="420" alt="Now playing card" />

- Album art, title and artist
- Waveform tinted with the **album's own colour**
- Live progress bar with elapsed / remaining time
- Previous · Play/Pause · Next

### 🔔 Notifications
Windows notifications appear inside the island with the app's icon, sender and message.

<img src="docs/screenshots/notification.png" width="430" alt="Notification card" />

### ⏱️ Timer
Hover the empty island for a clock and one-tap **1 / 5 / 25 min** timers (great for Pomodoro). Pause, resume or cancel; it chimes when done.

<img src="docs/screenshots/home.png" width="400" alt="Home card with clock and timer presets" /><br/>
<img src="docs/screenshots/timer.png" width="400" alt="Running timer" />

If music and a timer run together, the timer **splits off into its own bubble** with a progress ring — just like the iPhone.

### 📋 Clipboard · 🎧 Bluetooth · 🔋 Charging
Quick pills for everyday moments.

<img src="docs/screenshots/clipboard.png" width="340" alt="Copied pill" /><br/>
<img src="docs/screenshots/bluetooth.png" width="340" alt="Bluetooth connected pill" /><br/>
<img src="docs/screenshots/charging.png" width="340" alt="Charging pill" />

- **Copied** with a preview of what you copied (secrets from password managers are never shown)
- **Connected / Disconnected** for headphones, mice, keyboards
- **Charging** with battery level when you plug in

### 🟢🟠 Privacy indicators
The resting sliver shows a **green dot when your camera is on** and an **orange dot when your microphone is in use** — so nothing records without you noticing.

| Music playing | Camera on | Microphone on |
|:---:|:---:|:---:|
| <img src="docs/screenshots/sliver-music.png" width="150" /> | <img src="docs/screenshots/sliver-camera.png" width="150" /> | <img src="docs/screenshots/sliver-mic.png" width="150" /> |

---

## Designed to feel alive

The motion follows Apple's own fluid-interface principles (WWDC *Designing Fluid Interfaces*, *Design dynamic Live Activities*):

- **Spring physics, not fixed-time animations** — every change can be interrupted mid-flight and re-targeted from its current position and velocity.
- **Instant response** — the sliver nudges the moment your cursor arrives; buttons shrink on press and act on release.
- **Content arrives with the shape** — views fade and scale in just after the island begins to grow.
- **Concentric geometry** — corner radii nest evenly around the art and buttons inside.
- **Stays out of the way** — rests as a tiny sliver, never steals focus, never appears in Alt-Tab, and does **no per-frame work when idle** (near-zero CPU).
- Respects Windows' *"Animation effects"* setting (reduced motion).

---

## Install

1. Download **`DynamicIsland.exe`** from the [latest release](https://github.com/AyushDas4890/Dynamic_Island_for_Windows/releases/latest).
2. Double-click it. That's it — no installer, no .NET needed (it's bundled).

> Windows SmartScreen may warn because the app isn't code-signed. Click **More info → Run anyway**.

### Turning it on and off

Everything lives in the **tray icon** (bottom-right; it may be under the `^` arrow — drag it onto the taskbar to keep it visible).

| Action | How |
|---|---|
| Hide / show the island | Left-click the tray icon, or **Hide island** |
| Launch at sign-in | **Start with Windows** (off by default) |
| Quit completely | **Quit Dynamic Island** (also on the island's right-click menu) |

### Using it

| Gesture | Result |
|---|---|
| Move the mouse to the top-centre | Island nudges, then opens |
| Click the island | Open / close instantly |
| Move away | Collapses back to the sliver |
| Click an alert | Dismiss it |

### Notifications access
Reading notifications needs Windows' notification access. If the island's right-click menu says notifications are off, enable **Settings → Privacy & security → Notifications → Let apps access your notifications**.

---

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 2004+ / Windows 11.

```bash
git clone https://github.com/AyushDas4890/Dynamic_Island_for_Windows.git
cd Dynamic_Island_for_Windows
dotnet run
```

Create the standalone single-file exe:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

Regenerate the README images (rendered with demo content):

```bash
dist\DynamicIsland.exe --screenshots docs\screenshots
```

## How it works

| Feature | Windows API |
|---|---|
| Overlay window | WPF layered window, `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE` (no focus stealing, not in Alt-Tab) |
| Now playing | `GlobalSystemMediaTransportControlsSessionManager` (WinRT) |
| Notifications | `UserNotificationListener`, polled (its change event is unsupported for unpackaged apps) |
| Camera in use | Media Foundation `MFCreateSensorActivityMonitor` |
| Microphone in use | Core Audio session state on capture endpoints |
| Bluetooth | `DeviceWatcher` for connected Bluetooth / BLE devices |
| Clipboard | `AddClipboardFormatListener` |
| Charging | `SystemEvents.PowerModeChanged` + `GetSystemPowerStatus` |

Native COM bindings are generated by [CsWin32](https://github.com/microsoft/CsWin32) from Microsoft's official metadata. Design notes and research live in [`docs/RESEARCH-AND-DESIGN.md`](docs/RESEARCH-AND-DESIGN.md).

## Privacy

Everything runs **locally**. Dynamic Island makes no network requests and stores nothing; notifications, clipboard text and media info are only displayed on your screen.

## License

[MIT](LICENSE) © Ayush Das

<sub>Not affiliated with Apple. "Dynamic Island" is used descriptively for the design concept.</sub>

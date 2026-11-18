# Dynamic Island for Windows — Research & Design

Date: 2026-11-18

## 1. Research: what Apple's Dynamic Island is

Sources: Apple HIG "Live Activities", ActivityKit docs, WWDC23 "Design dynamic Live Activities",
WWDC26 "Live Activities essentials", Apple Support "View Live Activities in the Dynamic Island".

### Purpose
"A unified home for the various alerts and indicators of activity" (WWDC23). It floats on a layer
above all apps. Apps must not draw UI that points at or interacts with it.

### Presentations (size classes)
| Presentation | When | iPhone size (pt) |
|---|---|---|
| Compact (leading + trailing) | One activity running | each side 52–62 x 36.67 |
| Minimal | Two+ activities; one shown as small circle | 36.67–45 x 36.67 |
| Expanded | Long-press, or an alerting update | full width (371–408) x 84–160 |
| Limited width | Landscape / crowded | fit 28 x 28 circle |

Expanded layout has regions: leading, center, trailing, bottom.

### Interaction
- Tap: opens the owning app.
- Touch and hold: expands.
- Swipe up on expanded: collapses.
- Swipe across: switches between activities (newer devices hold up to 3).

### Design rules (from WWDC23)
- Concentric layout: content nests inside the pill with even margins; round shapes inside round shapes.
- No wasted space: encapsulation as narrow as possible, content snug to the center.
- Thick, rounded shapes; large heavy-weight text; color to show identity of each activity.
- Motion "inspired by biological form": elastic, like a living organism. Spring animations.
- Use numeric transitions for counting numbers; content-replace transitions for swapping content.
- Avoid a pure logo in minimal; keep conveying information.
- Prefer expanding the island over a separate notification for in-session alerts.
- Expanded heights: pick clearly tall or clearly short, not in between.

### Typical uses on iPhone
Now playing (art, waveform, controls), timers/stopwatch, calls/FaceTime, Maps turn-by-turn,
third-party Live Activities (delivery, rides, scores, flights), system alerts (Face ID, AirPods
connect + battery, charging, silent mode, AirDrop, Apple Pay), screen recording / hotspot,
camera and mic privacy indicators.

### Prior art on Windows (GitHub)
D-Island (WPF, media via GSMTC), BrimDeck (WPF), PILLAR (hover to peek, click to expand),
NotchX, GhostyNav. Common pattern: C# + WPF, top-center always-on-top overlay, media via
`GlobalSystemMediaTransportControlsSessionManager` (GSMTC).

## 2. Mapping to Windows

| iPhone feature | Windows source | Difficulty |
|---|---|---|
| Now playing + controls | GSMTC WinRT API | Easy |
| Timer | Self-contained | Easy |
| Charging / battery | `SystemParameters` / `PowerStatus` | Easy |
| Volume change | Core Audio endpoint callback | Medium |
| Bluetooth device connect | WinRT DeviceWatcher | Medium |
| Camera/mic in use | Registry ConsentStore polling | Medium |
| Other apps' notifications | UserNotificationListener (needs package identity) | Hard |
| Calls, navigation | No source | Not possible |

## 3. Design (v1)

### Goal
A top-center, always-on-top black pill that behaves like the Dynamic Island for the activities
Windows can supply easily, with the elastic spring motion that defines the feel.

### Non-goals (v1)
Notifications from other apps, calls, settings UI, multi-monitor, installer, auto-start.

### Anti-goals
- Steals focus or appears in Alt-Tab / taskbar.
- Blocks clicks on the area under it when idle (only the pill itself is hit-testable).
- Stiff linear animation (would fail the core "feel").
- Noticeable CPU use when idle.

### v1 activities
1. Now playing: compact = album art left + animated bars right; expanded = art, title, artist,
   prev / play-pause / next.
2. Charging: plugging in AC expands briefly with battery % and a green bar, then collapses.
3. Timer: start from the expanded idle view (5 min presets); compact shows countdown.

### States
- Idle: small pill (~120 x 32 px at 100% scale).
- Compact: widens to ~260 x 32 when an activity runs.
- Expanded: ~400 x 140 on hover/click; collapses when mouse leaves (short delay).
- Alert: auto-expand for ~3 s (charging), then back.

### Motion
Damped spring (stiffness ~ 300, damping ratio ~ 0.7) driving width, height and corner radius
every frame via `CompositionTarget.Rendering`. Content cross-fades with scale 0.9 to 1.

### Approaches considered
| Approach | Trade-off |
|---|---|
| C# + WPF (.NET 8 SDK) | Native, light, direct GSMTC access. Needs SDK install (~250 MB). **Recommended.** |
| C# + WPF on built-in .NET Framework 4.8 `csc.exe` | Zero install. Old C# 5, WinRT interop fiddly, no NuGet. |
| Electron (Node present) | Easiest CSS animation; ~150 MB RAM always on; media API needs native module. |

### v2 redesign (apple-design principles)
- Springs use Apple's parameters (response + damping ratio), are retargeted from the live value and
  velocity (interruptible), and fall back to critically damped when Windows animations are off.
- Response on press: the pill squishes (0.95) on mouse-down, buttons scale to 0.86 on press and
  commit on release. Idle pill grows slightly on hover before the 320 ms hover-intent expand.
- Content "materializes" (fade + scale from 0.92 anchored at the top + blur 10 to 0), starting
  70 ms after the shape begins to grow; it leaves faster (110 ms) than it arrives.
- Concentric geometry: 92 px cards use radius 46 so 46 px circles inset 23 px nest evenly.
- Views: Home (clock + 1/5/25 min presets), Media compact (art, title, album-tinted waveform),
  Media expanded (art, title/artist, waveform, progress with elapsed/remaining, prev/play/next),
  Timer compact, Timer expanded (pause/resume, cancel), Charging alert (iOS-style compact),
  Timer done alert, and the detached minimal bubble with a progress ring when music and a timer run
  together (hover the bubble to expand the timer).
- Buttons are Borders with press/release handlers, not WPF Buttons: Button mouse capture was
  unreliable in the WS_EX_NOACTIVATE window (v1 bug: timer taps ignored).

### v3 resting sliver (user request)
- Resting state is a thin sliver at the very top: 72x6 when nothing runs, 96x10 with activity dots
  (album-colored dot = music playing, orange dot = timer running).
- Hover ("peek") grows it to the compact pill at once; dwelling 320 ms expands fully.
- A 110x12 near-transparent hit strip at the top edge lets a mouse flicked to the top find it.
- Alerts (charging, timer done) still pop out on their own.
- Waveform animation runs only while visible, so the sliver does no per-frame work.

### v4 smoothness, green timer, more activities
- Open is one motion: hover nudges the sliver (instant feedback), a 240 ms dwell opens the card
  directly. The intermediate compact pill was removed (the two-step open read as a stutter).
- Content transition is fade + scale with a temporary BitmapCache; blur removed (effects in a
  layered window are costly). Size springs damping 0.88. Text uses Ideal formatting (no shimmer).
  Window shrunk to 480x210 to cut per-frame layered-window copies. `DI_PERF=1` logs frame pacing.
- Timer accent is green (#30D158).
- Notifications: UserNotificationListener works unpackaged for reading, but NotificationChanged
  throws (microsoft/windowsappsdk#6172), so new toasts are found by polling every 1.5 s.
  Right-click menu shows access status.
- Clipboard "Copied" pill with preview; skips data flagged
  ExcludeClipboardContentFromMonitorProcessing (password managers).
- Bluetooth connect/disconnect pill (classic + LE), ignoring devices already connected at start.
- Camera / microphone privacy dots in the sliver (green / orange, iPhone convention).
  Camera: Media Foundation sensor activity monitor (MFCreateSensorActivityMonitor, push-based).
  Microphone: any active Core Audio session on a capture endpoint, checked every 2 s.
  The CapabilityAccessManager registry was tried first but is not updated live on this
  Windows 11 build (verified by registry diff while the Camera app streamed).
  COM bindings are generated by Microsoft.Windows.CsWin32 (NativeMethods.txt), not hand-written.
- Not built: volume HUD (needs Core Audio COM interop), notification actions/reply.

### v5 standalone app + sleeker sizing
- Self-contained single-file `dist\DynamicIsland.exe` (.NET 8 runtime bundled, ~74 MB). No install
  or DOTNET_ROOT needed. Start Menu shortcut "Dynamic Island".
- Tray icon: left-click or "Hide island / Show island" toggles; "Start with Windows" writes/removes
  HKCU\...\Run\DynamicIsland; "Quit Dynamic Island" exits. Single instance via a named mutex.
- Smaller cards: media 360x140 (art 46, title 13.5, progress 3 px, vector transport icons),
  home/timer 352x76, notification 372x72, pills 300x32.

### Build
`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist`
(quit the running app first: the exe is locked while it runs). Icon: `tools\make-icon.ps1`.

### Run
Start Menu > Dynamic Island, or `dist\DynamicIsland.exe`.

### Done = verifiable
1. Pill visible top-center, not in taskbar/Alt-Tab, click-through outside pill.
2. Play music in Spotify/browser: compact shows art; hover expands; buttons control playback.
3. Plug in charger: alert shows %.
4. Start timer: countdown visible in compact; reaches 0 and alerts.
5. Idle CPU near 0%.

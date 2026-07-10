# Smash the Peak Recorder

Automatically records your [Smash the Peak](https://www.smashthepeak.eu) ladder matches with OBS and names the file after your opponent.

Two parts:

- **`extension/`** — Chrome extension (Manifest V3). Detects when you're on an active match page and reports match start/end + the opponent's name.
- **`app/`** — `PeakRecorder`, a .NET 8 Windows tray app. Receives events from the extension on `http://127.0.0.1:8123`, launches OBS if needed, starts/stops recording via obs-websocket, and renames the finished file to e.g. `2026-07-07_19-32_vs_OpponentName.mkv`.

## Setup

### 1. Companion app

```
cd app
dotnet build -c Release
```

Run `app\bin\Release\net8.0-windows\PeakRecorder.exe`. It sits in the system tray.

To start it with Windows: press `Win+R`, run `shell:startup`, and drop a shortcut to `PeakRecorder.exe` there.

Config lives at `%APPDATA%\PeakRecorder\config.json` (tray menu → *Open config*):

| Key | Default | Meaning |
|---|---|---|
| `BridgePort` | `8123` | Local port the extension talks to |
| `ObsExePath` | `C:\Program Files\obs-studio\bin\64bit\obs64.exe` | OBS location |
| `ObsLaunchArgs` | `--disable-shutdown-check --minimize-to-tray` | Args when launching OBS |
| `ObsWsPort` / `ObsWsPassword` | `null` | Overrides; by default read from OBS's own websocket config |
| `FilenameTemplate` | `{date}_{time}_vs_{opponent}` | Also supports `{matchId}` |
| `RecordingsFolder` | `null` | Move finished recordings here (created if missing; `%VARS%` expanded; other drives OK). `null` = leave them in OBS's output folder |
| `DeleteOriginalAfterRemux` | `true` | With OBS auto-remux (mkv → mp4): delete the `.mkv` once the renamed `.mp4` looks complete (≥90% of the original's size) |
| `DiscardUnplayedMatches` | `true` | Delete the recording when the match ends without a single completed game (cancel / no-show) |
| `DiscardUnplayedMaxMinutes` | `10` | Never auto-discard a recording longer than this, even if the match looks unplayed |

A log is written to `%APPDATA%\PeakRecorder\log.txt` (tray menu → *Open log*).

### 2. OBS

Nothing to do. If OBS is closed, the app enables the WebSocket server in OBS's config (reusing your existing password) and launches OBS itself. Make sure your OBS scene captures your Switch feed and that recording output settings are how you want them.

### 3. Chrome extension

1. Open `chrome://extensions`, enable **Developer mode**.
2. **Load unpacked** → select the `extension/` folder.
3. Click the extension icon: it should say *Companion app connected* when `PeakRecorder.exe` is running.

## How it works

1. You get matched on smashthepeak.eu and open the match page (`/match/<id>`).
2. The content script reads the two player names from the page, works out which one isn't you (your name is auto-detected from the site header, or set it manually in the popup), and sends `match_started`.
3. PeakRecorder launches/connects to OBS and starts recording.
4. When the page shows the match as finished — or you navigate away, if *Stop on leave* is enabled (default) — it sends `match_ended`.
5. PeakRecorder stops the recording, waits for OBS to release the file, and renames it to `2026-07-07_19-32_vs_Opponent.mkv`.

The extension popup also has manual **Start/Stop recording** buttons as a fallback, and the tray menu has the same.

### Connection status

While on smashthepeak.eu with the companion app unreachable, the extension shows a highlighted **banner on the page** ("The recorder app is not running…") with a **Start recorder app** button; it turns green and disappears once the app is up. Dismissing it silences the warning until the app has reconnected once. Checked every 15 seconds.

The toolbar icon also reflects the state (gray + `!` = unreachable, green = connected, red + `REC` = recording), but some Chromium forks (Arc) don't render dynamically drawn icons — the banner is the reliable signal there.

Both the banner button and the popup's **Start companion app** button use Chrome native messaging: the app registers itself as the host (`eu.smashthepeak.peakrecorder`, written to `%APPDATA%\PeakRecorder\nativehost.json` + `HKCU\Software\Google\Chrome\NativeMessagingHosts`) the first time it runs while the extension is installed — so run `PeakRecorder.exe` manually once before relying on the buttons.

## Detection details

Detection was tuned against real page dumps (2026-07-07):

- **You** are identified by player *ID*: the "Active Player" sidebar contains an avatar-only link to `/en/player/<id>` next to the `/en/settings/user` link.
- **Match players**: the match page has exactly two named `/en/player/<id>` links; the one that isn't you is the opponent. Matches you spectate (where your ID isn't a participant) are never recorded.
- **Recording start**: not at match creation, but once characters and the game-1 stage are picked — the score panel's status flips to `Select the winner.` (participant view; spectators see `Players picking winner...`, but spectated matches are never recorded anyway). Backup signals: exactly one stage splash image on the page (the striking grid shows all 9; the locked-in stage shows 1), or a completed game (in case the page is opened mid-game).
- **Match end**: the score panel's `<name> won this match.` line, or server chat lines (`Server: … won the Match`, `Server: Match concluded`). Patterns are anchored so typed chat messages can't trigger them.

To debug, keep **Debug logging** on in the popup and watch the DevTools console for `[PeakRecorder]` lines. If the site's markup changes, click **Dump page for debugging** in the popup — it saves the page structure to `%APPDATA%\PeakRecorder\dumps\` for re-tuning `extension/content.js`.

## Notes

- One recording per set; per-game splitting isn't implemented.
- Recording never starts for already-finished match pages (browsing history is safe).
- If OBS was already recording when a match starts, the app adopts that recording and will rename it when the match ends.

# Smash the Peak Recorder

Automatically records your [Smash the Peak](https://www.smashthepeak.eu) ladder matches with OBS, names the file after your opponent, and keeps a match history with per-opponent notes and game plans.

Two parts:

- **`extension/`** — Chrome extension (Manifest V3). Detects when you're on an active match page and reports match found/start/end, the opponent's name, and per-game stage, characters and score.
- **`app/`** — `PeakRecorder`, a .NET 8 Windows tray app. Receives events from the extension on `http://127.0.0.1:8123`, launches OBS if needed, starts/stops recording via obs-websocket, renames the finished file to e.g. `2026-07-07_19-32_vs_OpponentName.mkv`, and stores match results, notes and game plans in a local SQLite database.

## Setup

### 1. Companion app

```
cd app
dotnet build -c Release
```

Run `app\bin\Release\net8.0-windows\PeakRecorder.exe`. It sits in the system tray; double-click the tray icon (or *Open PeakRecorder* in the tray menu) for the main window.

The main window and the pre-match briefing are rendered with WebView2, so the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) must be installed. It ships with Windows 11 and with most Windows 10 installs that have Edge.

To start it with Windows: press `Win+R`, run `shell:startup`, and drop a shortcut to `PeakRecorder.exe` there.

Config lives at `%APPDATA%\PeakRecorder\config.json` (tray menu → *Open config*). The common keys can also be edited in the main window's *Settings* page:

| Key | Default | Meaning |
|---|---|---|
| `BridgePort` | `8123` | Local port the extension talks to |
| `ObsExePath` | `C:\Program Files\obs-studio\bin\64bit\obs64.exe` | OBS location |
| `ObsLaunchArgs` | `--disable-shutdown-check --minimize-to-tray` | Args when launching OBS |
| `ObsWsPort` / `ObsWsPassword` | `null` | Overrides; by default read from OBS's own websocket config |
| `FilenameTemplate` | `{date}_{time}_vs_{opponent}` | Also supports `{matchId}` |
| `RecordingsFolder` | `null` | Move finished recordings here (created if missing; `%VARS%` expanded; other drives OK). `null` = leave them in OBS's output folder |
| `DeleteOriginalAfterRemux` | `true` | With OBS auto-remux (mkv → mp4): delete the `.mkv` once the renamed `.mp4` looks complete (≥90% of the original's size) |
| `BriefingStyle` | `card` | How the pre-match briefing appears: `card` (small always-on-top card), `full` (full window) or `off`. Also switchable from the tray menu |

Other files under `%APPDATA%\PeakRecorder\`:

- `log.txt` — app log (tray menu → *Open log*)
- `data.db` — SQLite database with matches, notes, game plans and the focus goal
- `icons\` — cached character stock icons (see below)
- `dumps\` — page dumps for debugging detection
- `nativehost.json` — Chrome native messaging host manifest

### 2. OBS

Nothing to do. If OBS is closed, the app enables the WebSocket server in OBS's config (reusing your existing password) and launches OBS itself. Make sure your OBS scene captures your Switch feed and that recording output settings are how you want them.

### 3. Chrome extension

1. Open `chrome://extensions`, enable **Developer mode**.
2. **Load unpacked** → select the `extension/` folder.
3. Click the extension icon: it should say *Companion app connected* when `PeakRecorder.exe` is running.

## How it works

1. You get matched on smashthepeak.eu and open the match page (`/match/<id>`).
2. The content script reads the two player names from the page, works out which one isn't you (your name is auto-detected from the site header, or set it manually in the popup), and sends `match_found`. PeakRecorder creates a match record and shows the **pre-match briefing** for that opponent.
3. Once characters and the game-1 stage are locked in, the extension sends `match_started`. PeakRecorder launches/connects to OBS and starts recording.
4. During the set the extension sends `match_update` events with the current stage, both characters and the live score, which feed the live match pane.
5. When the page shows the match as finished, it sends `match_ended` together with the result (W/L and game score). PeakRecorder stops the recording, waits for OBS to release the file, renames it to `2026-07-07_19-32_vs_Opponent.mkv`, and stores the result on the match record.

If you leave the match page mid-set and *Stop on leave* is enabled (default), the recording keeps going for a 5 minute grace period so you can browse the rest of the site. Returning to the same match page cancels the timer; staying away for the whole period stops the recording. Closing the tab or browser stops it immediately.

If you leave a match page before recording started (for example a match that never got played), a `match_dismissed` event closes the briefing and the empty match record is deleted.

The extension popup also has manual **Start/Stop recording** buttons as a fallback, and the tray menu has the same (*Start/Stop recording now*).

### Connection status

While on smashthepeak.eu with the companion app unreachable, the extension shows a highlighted **banner on the page** ("The recorder app is not running…") with a **Start recorder app** button; it turns green and disappears once the app is up. Dismissing it silences the warning until the app has reconnected once. Checked every 15 seconds.

The toolbar icon also reflects the state (gray + `!` = unreachable, green = connected, red + `REC` = recording). Icons are static PNGs, but some Chromium forks (Arc) ignore `chrome.action.setIcon`, so they always show the default "ready" icon there — the banner is the reliable signal.

Both the banner button and the popup's **Start companion app** button use Chrome native messaging: the app registers itself as the host (`eu.smashthepeak.peakrecorder`, written to `%APPDATA%\PeakRecorder\nativehost.json` + `HKCU\Software\Google\Chrome\NativeMessagingHosts`) the first time it runs while the extension is installed — so run `PeakRecorder.exe` manually once before relying on the buttons.

## Main window

Open it from the tray icon. It has four pages:

- **Library** — every recorded match, newest first, with result, score, stages played and both characters. Filter by opponent or note text, by stage, or by character. Selecting a match shows its notes, lets you edit result/stages/characters by hand, open the video file, or delete the match.
- **Live** — shown while a match is in progress: the opponent, live score, current stage and characters, your game plan for that opponent, and a note composer. Notes taken here are timestamped relative to the recording, so you can jump to the moment in the VOD later.
- **Player** — per-opponent view: your record against them, every set played, and the free-form **game plan** that is shown in the pre-match briefing next time you meet them.
- **Settings** — filename template, recordings folder, OBS path and the remux-delete toggle, plus shortcuts to the raw config and the log.

### Notes

- Notes can be added live (timestamped) or after the fact on any match in the library ("VOD" notes, optionally attached to a specific game of the set).
- Type `#tag` anywhere in the note text to tag it (`#habit jumps from ledge`). Tags show as chips; the built-in ones (`#habit`, `#adapt`, `#work-on`, `#tech`) have quick-toggle buttons next to the composer, and a game number (G1–G5) can be picked the same way.
- Notes can be edited and deleted from the match view.
- A **weekly focus** goal can be set at the top of the library and is also shown in the briefing.

### Pre-match briefing

When a match is found (before it starts), a briefing pops up with the opponent's name, your history against them, your game plan and this week's focus. `BriefingStyle` picks between a small always-on-top card, a full window (which hides itself when game 1 starts), or none. *Preview briefing* in the tray menu shows it with a test opponent.

### Character icons

The site serves stock icons at `/images/characters/icons/<Character>`, but rate-limits requests from outside the browser. The extension therefore fetches any icons the app is missing (`GET /icons/needed`) same-origin from the page and uploads them through the bridge (`POST /icons`). They are cached in `%APPDATA%\PeakRecorder\icons\` so past matches show icons even offline.

## Detection details

Detection was tuned against real page dumps (2026-07-07):

- **You** are identified by player *ID*: the "Active Player" sidebar contains an avatar-only link to `/en/player/<id>` next to the `/en/settings/user` link.
- **Match players**: the match page has exactly two named `/en/player/<id>` links; the one that isn't you is the opponent. Matches you spectate (where your ID isn't a participant) are never recorded.
- **Recording start**: not at match creation, but once characters and the game-1 stage are picked — the score panel's status flips to `Select the winner.` (participant view; spectators see `Players picking winner...`, but spectated matches are never recorded anyway). Backup signals: exactly one stage splash image on the page (the striking grid shows all 9; the locked-in stage shows 1), or a completed game (in case the page is opened mid-game).
- **Per-game info**: the locked-in stage splash and the two character portraits give stage and characters for the current game; completed game rows give the running score. Swapping stage or character before a game starts is reported again so the last value wins.
- **Match end**: the score panel's `<name> won this match.` line, or server chat lines (`Server: … won the Match`, `Server: Match concluded`). Patterns are anchored so typed chat messages can't trigger them. The winner name and the final game count are compared against your own name to produce the W/L result and score.

To debug, keep **Debug logging** on in the popup and watch the DevTools console for `[PeakRecorder]` lines. If the site's markup changes, click **Dump page for debugging** in the popup — it saves the page structure to `%APPDATA%\PeakRecorder\dumps\` for re-tuning `extension/content.js`.

## Bridge API

The extension talks to the app over plain HTTP on `127.0.0.1:<BridgePort>`:

| Endpoint | Purpose |
|---|---|
| `GET /status` | `{ recording, opponent, matchId }` |
| `POST /event` | `{ type, matchId, opponent, players, stage, myCharacter, opponentCharacter, gamesWon, gamesLost, gameInProgress, result }`. Types: `match_found`, `match_started`, `match_update`, `match_ended`, `match_dismissed`, `manual_start`, `manual_stop` |
| `GET /icons/needed` | Character names the app has no cached icon for |
| `POST /icons` | `{ name, contentType, data (base64) }` |
| `POST /register` | `{ extensionId }` — registers the native messaging host for that extension |
| `POST /dump` | Saves the request body to `%APPDATA%\PeakRecorder\dumps\` |

## Notes

- One recording per set; per-game splitting isn't implemented.
- Recording never starts for already-finished match pages (browsing history is safe).
- If OBS was already recording when a match starts, the app adopts that recording and will rename it when the match ends.
- The app never deletes video files on its own (apart from the `.mkv` original after a successful remux, if enabled). Deleting a match from the library removes only the database record.

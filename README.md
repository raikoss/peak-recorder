# Smash the Peak Recorder

<p align="center">
  <img src="https://github.com/user-attachments/assets/e6e90e43-655c-4889-b04f-bc840c4569a3" 
  height="440px" width="auto" alt="Banner">
</p>

Play your [Smash the Peak](https://www.smashthepeak.eu) ladder sets as usual. This tool records every set with OBS for you, names the video after your opponent, and keeps a match history where you can write notes and game plans for the players you keep running into.

## What it does

- **Records automatically.** When you're in a match on smashthepeak.eu, OBS starts recording as soon as game 1 is about to begin and stops when the set is over. No hotkeys, no forgetting to hit record.
- **Names your videos.** Files come out like `2026-07-07_19-32_vs_OpponentName.mkv` instead of `2026-07-07 19-32-11.mkv`. Optionally moves them into a folder of your choice.
- **Keeps a match history.** Every set is saved with the result, score, stages played and the characters both of you used.
- **Lets you take notes.** Write notes mid-set ("rolls to ledge after every shield") or afterward while reviewing the VOD. Live notes are timestamped so you can jump to that moment in the video.
- **Remembers your game plan per opponent.** Before a set starts, a small card pops up with your record against that player and the game plan you wrote last time.

It is made of two pieces that talk to each other:

1. **PeakRecorder** – a small Windows program that lives in your system tray. It controls OBS and stores your history.
2. **A Chrome extension** – it watches the smashthepeak.eu match page and tells PeakRecorder when a set starts and ends, who you're playing, and what's being picked.

## What you need

- Windows 10 or 11
- [OBS Studio](https://obsproject.com/) set up to capture your Switch, with recording settings the way you like them
- Google Chrome (or another Chromium browser like Brave, Edge or Arc)
- The free [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), only if you want to build the app yourself instead of downloading it (see below)

## Getting started

This will hopefully get easier with time, but for now the installation steps are pretty manual. It doesn't take much longer than 10 minutes to set up the first time even if you have to don't have all prerequisites yet.

### Simple way - Downloading the built app

1. Go to https://github.com/raikoss/peak-recorder/releases and download the newest release. There are two zips to choose from:
   - **PeakRecorder-x.y.z.zip** – everything included, no extra installs needed. Pick this one if unsure.
   - **PeakRecorder-x.y.z-requires-dotnet8.zip** – much smaller, but you need the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed first.
2. Extract the zip somewhere you'll keep it, for example `C:\SmashRecorder`
3. Run `app\PeakRecorder.exe` to start the app
4. See Chrome extension steps below, using the `extension` folder from the zip

### Build the app yourself

#### Step 1 – Get the files

Click the green **Code** button at the top of this page and choose **Download ZIP**, then unzip it somewhere you'll keep it, for example `C:\SmashRecorder`. (If you use git, cloning works too.)

#### Step 2 – Build PeakRecorder

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) if you haven't already (pick the **SDK**, not just the runtime).
2. Open the unzipped folder, go into the `app` folder, click the address bar at the top of the Explorer window, type `cmd` and press Enter. A black command window opens in that folder.
3. Paste this and press Enter:

   ```
   dotnet build -c Release
   ```

   This will build the app based on the source code. It takes a minute the first time. When it says **Build succeeded**, you're done.

4. Your program is now at `app\bin\Release\net8.0-windows\PeakRecorder.exe`. Double-click it. A new icon appears in the system tray (bottom-right, next to the clock; you may need to click the little `^` arrow to see it).

**Tip:** to have it start with Windows, press `Win+R`, type `shell:startup`, press Enter, and drop a shortcut to `PeakRecorder.exe` in the folder that opens. The Chrome extension will warn you if the app is not running though, so this is preference.

### Run the PeakRecorder app

This needs to run once to register with the extension, so that the extension can start the app if the app isn't running. 

### Installing the Chrome extension

1. In Chrome, open `chrome://extensions` (paste it in the address bar).
2. Turn on **Developer mode** (toggle in the top right).
3. Click **Load unpacked** and choose the `extension` folder from the files you unzipped.
4. Click the puzzle-piece icon in Chrome's toolbar and pin **Smash the Peak Recorder** so you can see its icon.

Click the icon: it should say **Companion app connected**. If it says the app isn't running, make sure you started `PeakRecorder.exe` in step 2.

### Playing

That's it. Go to smashthepeak.eu and play a set. You don't need to open OBS yourself; PeakRecorder will launch it (minimized to the tray) the first time a set starts.

Here's what you'll see during a set:

- **Match found** – as soon as you open the match page, a small card pops up with the opponent's name, your record against them, and your game plan if you've written one.
- **Game 1 stage locked in** – recording starts. The extension icon turns red with **REC**, and the tray icon changes too.
- **Set over** – recording stops and the file is renamed after your opponent. The result and score are saved to your history.

If you want to check something on the site mid-set, go ahead. The recording keeps going for 5 minutes while you're off the match page, and stopping happens right away if you close the tab.

## The main window

Double-click the tray icon (or right-click it and choose **Open PeakRecorder**) to open the main window. The tray menu itself is deliberately small: open the app, start or stop a recording by hand, jump to Settings, or exit.

- **Library** – all your recorded sets, newest first. Search by opponent name or note text, or filter by stage and character. Click a set to see its notes, open the video, or fix the result if it was detected wrong.
- **Live** – while a set is in progress: current score, stage, characters, your game plan, and a box to type notes. Notes typed here are stamped with the time in the recording.
- **Player page** – click an opponent's name to see every set against them and write your **game plan** for next time.
- **Settings** – every option the app has, grouped into Recording (naming, folder), OBS (where it's installed, connection), Pre-match briefing, and App & files (data folder, log, local port). Changes save as you go.

**Notes and tags:** type `#habit`, `#adapt`, `#work-on` or `#tech` anywhere in a note (like `#habit rolls in from ledge`) to tag it, or click the tag buttons. Notes can be attached to a specific game of the set, edited, and deleted. There's also a **weekly focus** box at the top of the library for whatever you're currently working on; it shows up on the pre-match card too.

**Pre-match card:** choose between the small always-on-top card, a full-screen version, or turning it off under **Settings → Pre-match briefing**. The **Preview briefing** button there shows you what it looks like without needing a match.

## If something isn't working

- **Extension says the app isn't running.** Start `PeakRecorder.exe`. When you're on smashthepeak.eu without it running, a banner appears at the top of the page with a **Start recorder app** button; that button works after you've run PeakRecorder manually at least once.
- **Recording didn't start.** Open **Settings → App & files → Open log** and look at the last lines. The extension popup also has manual **Start recording / Stop recording** buttons as a backup, and so does the tray menu.
- **Extension icon doesn't change colour.** Some browsers (Arc in particular) don't show icon changes. The banner on the page and the tray icon are the reliable signals.
- **Log says it couldn't connect to OBS.** PeakRecorder can only turn on OBS's built-in WebSocket server while OBS is closed. If OBS was already open the very first time you used the recorder, close OBS and let PeakRecorder launch it once. After that it works either way.
- **The site changed and detection broke.** Turn on **Debug logging** in the extension popup and click **Dump page for debugging**, then open an issue and attach the dump from `%APPDATA%\PeakRecorder\dumps\`.

## Where your stuff lives

Everything the app saves is in `%APPDATA%\PeakRecorder\` (paste that into the Explorer address bar):

| File          | What it is                                                                                |
| ------------- | ----------------------------------------------------------------------------------------- |
| `config.json` | Settings. Everything in it can be changed from the Settings page in the app             |
| `data.db`     | Your match history, notes and game plans. Back this up if you care about it               |
| `log.txt`     | The log, useful when something goes wrong                                                 |
| `icons\`      | Cached character icons                                                                    |

Your videos are wherever OBS normally saves recordings, unless you set a **Recordings folder** in Settings.

### All settings

All of these are on the Settings page in the app; the names below are what they're called in `config.json` if you ever open it directly.

| Setting                       | Default                                           | What it does                                                                    |
| ----------------------------- | ------------------------------------------------- | ------------------------------------------------------------------------------- |
| `FilenameTemplate`            | `{date}_{time}_vs_{opponent}`                     | How videos are named. `{matchId}` also works                                    |
| `RecordingsFolder`            | empty                                             | Move finished videos here. Empty = leave them in OBS's folder                   |
| `DeleteOriginalAfterRemux`    | on                                                | If OBS is set to convert recordings to mp4, delete the mkv once the mp4 is done |
| `BriefingStyle`               | `card`                                            | Pre-match card style: `card`, `full` or `off`                                   |
| `ObsExePath`                  | `C:\Program Files\obs-studio\bin\64bit\obs64.exe` | Change if OBS is installed somewhere else                                       |
| `ObsLaunchArgs`               | `--disable-shutdown-check --minimize-to-tray`     | Options used when PeakRecorder starts OBS                                       |
| `ObsWsPort` / `ObsWsPassword` | empty                                             | Normally read from OBS automatically; only set these if that fails              |
| `BridgePort`                  | `8123`                                            | Local port the extension uses to talk to the app. Needs an app restart to apply |

## Good to know

- One video per set. It doesn't split into one file per game.
- Opening an old, finished match page never starts a recording, so browsing your history on the site is safe.
- Sets you spectate are never recorded.
- If OBS was already recording when a set starts, that recording is used and renamed when the set ends.
- The app never deletes your videos (except the mkv original after a successful mp4 conversion, if that option is on). Deleting a set from the library only removes it from the history.

## For developers

Technical details for anyone who wants to poke at the code.

- **Extension** (`extension/`): Manifest V3. `content.js` reads the match page and sends events to `http://127.0.0.1:8123`. `background.js` polls the app's status and sets the toolbar icon. Native messaging (`eu.smashthepeak.peakrecorder`, registered by the app under `HKCU\Software\Google\Chrome\NativeMessagingHosts`) is used to launch the app from the browser.
- **App** (`app/`): .NET 8 WinForms tray app. The main window and briefing card are a WebView2 page (`Assets/main.html`). Storage is SQLite via `Microsoft.Data.Sqlite`. OBS is controlled via obs-websocket 5.
- **Bridge API** on `127.0.0.1:<BridgePort>`:

  | Endpoint                            | Purpose                                                                                                                                                                                                                                            |
  | ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
  | `GET /status`                       | `{ recording, opponent, matchId }`                                                                                                                                                                                                                 |
  | `POST /event`                       | `{ type, matchId, opponent, players, stage, myCharacter, opponentCharacter, gamesWon, gamesLost, gameInProgress, result }`. Types: `match_found`, `match_started`, `match_update`, `match_ended`, `match_dismissed`, `manual_start`, `manual_stop` |
  | `GET /icons/needed` / `POST /icons` | Character icon cache; the extension fetches icons same-origin (the site rate-limits the app) and uploads them                                                                                                                                      |
  | `POST /register`                    | `{ extensionId }` – registers the native messaging host                                                                                                                                                                                            |
  | `POST /dump`                        | Saves a page dump to `dumps\`                                                                                                                                                                                                                      |

- **Detection** (tuned against page dumps from 2026-07-07):
  - You are identified by player ID from the "Active Player" sidebar link to `/en/player/<id>`.
  - The match page has exactly two named `/en/player/<id>` links; the one that isn't you is the opponent. If you're not a participant, nothing is recorded.
  - Recording starts when the score panel reads `Select the winner.` (fallbacks: exactly one stage splash image on the page, or an already-completed game).
  - Stage, characters and running score come from the locked-in stage splash, the character portraits and the completed game rows. Re-picks before a game starts are reported again.
  - Match end is the `<name> won this match.` line or the server chat lines `… won the Match` / `Match concluded`; patterns are anchored so typed chat can't trigger them. The result is derived by comparing the winner to your own name.
  - Leaving the match page mid-set starts a 5 minute grace timer before stopping; the `pagehide` event stops immediately via `sendBeacon`.

## Disclaimers

This project is almost entirely vibe coded with Claude. I probably would have solved this differently if made by hand, but it would probably take months of work for something I probably wouldn't even appreciate. By using Claude I was fortunate to get what I wanted quickly, as it didn't really matter to me how the code looked as it was intended for myself, so keep this in mind in case you try to read through the code. It's probably (likely) not perfect, but it's good enough for me :)

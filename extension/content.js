// Smash the Peak match detector.
//
// Watches SPA navigation for /match/<id> pages, works out who the opponent is
// from the player profile links, and tells the PeakRecorder companion app to
// start/stop OBS recording.
//
// Detection is based on real page dumps (2026-07-07):
// - The logged-in user's "Active Player" sidebar contains an avatar-only link
//   to /en/player/<myId> right next to an /en/settings/user link -> "me" is
//   detected by player ID, not by name text.
// - The match page has exactly two named player links: /en/player/<id> with
//   the player's name as text (duplicated for responsive layouts).
// - A finished match shows "<name> won this match." in the score panel and
//   "Server: Match concluded." in the chat.

(() => {
  const TAG = "[PeakRecorder]";
  const MATCH_URL_RE = /\/match\/(\d+)/;
  const PLAYER_LINK_SEL = 'a[href*="/player/"]';
  const PLAYER_ID_RE = /\/player\/(\d+)/;

  const state = {
    debug: true,
    stopOnLeave: true,
    myName: null, // manual override from popup
    myIdAuto: null,
    myNameAuto: null,
    matchId: null, // match page we are currently on
    opponent: null,
    foundSent: false, // told the app the opponent is known (pre-match briefing)
    startedSent: false, // told the app to start recording
    finishedSent: false,
    // Last stage / characters reported to the app for this match.
    lastStage: null,
    myChar: null,
    oppChar: null,
    // Live game score / whether a game is running, as last reported.
    liveGamesWon: -1,
    liveGamesLost: -1,
    liveInProgress: null,
  };

  const log = (...args) => state.debug && console.log(TAG, ...args);

  // Browsing the rest of the site mid-match must not split the recording:
  // when the match page is left while recording, keep the match state and
  // only stop after this grace period without returning. Coming back to the
  // same match page cancels the timer and the recording just continues.
  const AWAY_GRACE_MS = 5 * 60 * 1000;
  let awayTimer = null;

  function clearAwayTimer() {
    if (awayTimer) {
      clearTimeout(awayTimer);
      awayTimer = null;
    }
  }

  function resetMatchState() {
    clearAwayTimer();
    state.matchId = null;
    state.opponent = null;
    state.foundSent = false;
    state.startedSent = false;
    state.finishedSent = false;
    state.lastStage = null;
    state.myChar = null;
    state.oppChar = null;
    state.liveGamesWon = -1;
    state.liveGamesLost = -1;
    state.liveInProgress = null;
  }

  function sendMatchEnded() {
    send("event", {
      type: "match_ended",
      matchId: state.matchId,
      opponent: state.opponent,
    });
  }

  chrome.storage.local.get(["myName", "stopOnLeave", "debug"], (v) => {
    if (typeof v.myName === "string" && v.myName.trim()) state.myName = v.myName.trim();
    if (typeof v.stopOnLeave === "boolean") state.stopOnLeave = v.stopOnLeave;
    if (typeof v.debug === "boolean") state.debug = v.debug;
    evaluate("initial");
  });

  chrome.storage.onChanged.addListener((changes) => {
    if (changes.myName) state.myName = (changes.myName.newValue || "").trim() || null;
    if (changes.stopOnLeave) state.stopOnLeave = changes.stopOnLeave.newValue;
    if (changes.debug) state.debug = changes.debug.newValue;
  });

  function send(path, body) {
    try {
      chrome.runtime.sendMessage({ kind: "bridge", path, body }, (res) => {
        if (chrome.runtime.lastError) {
          log("bridge error:", chrome.runtime.lastError.message);
        } else if (res && !res.ok) {
          log("companion app unreachable:", res.error);
        } else {
          log("sent", path, body, "->", res && res.data);
        }
      });
    } catch (e) {
      log("sendMessage failed:", e);
    }
  }

  // ---- who am I ------------------------------------------------------------

  // The "Active Player" sidebar links to my own profile (avatar-only link)
  // and sits next to the /settings/user link. Walk up from the settings link
  // until a container that also holds a player link; that player is me.
  function detectMyId() {
    if (state.myIdAuto) return state.myIdAuto;
    const settings = document.querySelector('a[href*="/settings/user"]');
    let el = settings ? settings.parentElement : null;
    while (el && el !== document.body) {
      const link = el.querySelector(PLAYER_LINK_SEL);
      const m = link && (link.getAttribute("href") || "").match(PLAYER_ID_RE);
      if (m) {
        state.myIdAuto = m[1];
        chrome.storage.local.set({ myIdAuto: m[1] });
        log("auto-detected my player id:", m[1]);
        return m[1];
      }
      el = el.parentElement;
    }
    return null;
  }

  // ---- match page parsing --------------------------------------------------

  function currentMatchId() {
    const m = location.pathname.match(MATCH_URL_RE);
    return m ? m[1] : null;
  }

  // All named player links on the page, deduped by player id. On a match page
  // that is exactly the two participants (my own sidebar link has no text).
  function getMatchPlayers() {
    const byId = new Map();
    for (const a of document.querySelectorAll(PLAYER_LINK_SEL)) {
      const m = (a.getAttribute("href") || "").match(PLAYER_ID_RE);
      if (!m) continue;
      const name = a.textContent.trim();
      if (!name || name.length > 40) continue; // avatar-only or junk
      if (!byId.has(m[1])) byId.set(m[1], name);
    }
    return [...byId.entries()].map(([id, name]) => ({ id, name }));
  }

  function resolveOpponent(players) {
    const myId = detectMyId();
    if (myId && players.some((p) => p.id === myId)) {
      const others = players.filter((p) => p.id !== myId);
      if (others.length === 1) {
        // Remember my own display name for the popup.
        const me = players.find((p) => p.id === myId);
        if (me && state.myNameAuto !== me.name) {
          state.myNameAuto = me.name;
          chrome.storage.local.set({ myNameAuto: me.name });
        }
        return others[0].name;
      }
    }
    // Fallback: manual name from the popup.
    if (state.myName) {
      const others = players.filter((p) => p.name.toLowerCase() !== state.myName.toLowerCase());
      if (others.length === 1 && players.length === 2) return others[0].name;
    }
    return null;
  }

  // Finish signals, anchored so ordinary chat messages can't trigger them:
  // the score panel's "<name> won this match." and the chat's server lines
  // (players can't fake those — their messages are prefixed with their name).
  const FINISHED_PATTERNS = [
    // Score panel line "<name> won this match." — chat lines always have a
    // "Name: " prefix, so requiring no colon keeps chat text from matching.
    /^[^:\n]{1,40} won this match\b/im,
    /^Server: .*won the Match/m,
    /^Server: Match concluded/m,
    /^Server: .*(cancelled|canceled|disqualified)/im,
  ];

  function mainText() {
    const main = document.querySelector("main") || document.body;
    return main.innerText || "";
  }

  // ---- result parsing --------------------------------------------------------

  // A finished match names the winner in the score panel ("<name> won this
  // match.") and in chat ("Server: <name> won the Match."), and the chat logs
  // every completed game ("Server: <name> won Game 2." — dump 2026-07-07,
  // match 265360, including the deciding game). Same anchoring as the finish
  // patterns, so typed chat can't spoof any of it.
  const WINNER_RES = [
    /^([^:\n]{1,40}) won this match\b/im,
    /^Server: (.{1,40}?) won the Match/m,
  ];
  const GAME_LINE_RE = /^Server: (.{1,40}?) won Game \d+/gm;

  function sameName(a, b) {
    return !!a && !!b && a.trim().toLowerCase() === b.trim().toLowerCase();
  }

  // Returns {result, gamesWon, gamesLost} from my perspective, or {} when the
  // match ended without a winner (cancelled / DQ) or the winner text doesn't
  // match a known participant. Anything that isn't the opponent is me — a
  // match page only ever has the two participants as named players.
  function matchResult(text, players) {
    let winner = null;
    for (const re of WINNER_RES) {
      const m = re.exec(text);
      if (m) {
        winner = m[1].trim();
        break;
      }
    }
    if (!winner || !state.opponent) return {};
    const myName = state.myNameAuto || state.myName;
    let result = null;
    if (sameName(winner, state.opponent)) result = "L";
    else if (sameName(winner, myName) || players.some((p) => sameName(p.name, winner))) result = "W";
    if (!result) return {};
    let gamesWon = 0;
    let gamesLost = 0;
    for (const m of text.matchAll(GAME_LINE_RE)) {
      if (sameName(m[1], state.opponent)) gamesLost++;
      else gamesWon++;
    }
    return { result, gamesWon, gamesLost };
  }

  // Returns a string describing WHY the page looks finished, or null.
  function finishedReason(text) {
    for (const re of FINISHED_PATTERNS) {
      const m = re.exec(text);
      if (m) return `text "${m[0].slice(0, 60)}"`;
    }
    return null;
  }

  // The server chat announces every completed game — proof the match is well
  // past the pick phase, in case the page is opened mid-set.
  const GAME_WON_RE = /^Server: [^\n]* won Game \d+/m;

  // The match is "set" once characters and the game-1 stage are picked: the
  // score panel's status flips to "Select the winner." while the game runs
  // (participant view — spectators see "Players picking winner...", but we
  // only ever record matches we play in). Line-anchored, so chat
  // ("Name: ...") can't fake it. Earlier statuses: "Pick your character.",
  // "Opponent banning stages..." (dumps 2026-07-10).
  const READY_RE = /^Select the winner/m;

  // Once a game finishes, the page grows a permanent "Match Summary" recap
  // list with a splash image (stage + characters) for every completed game
  // — it stays on the page for the rest of the set. Its container is the
  // element right after the "Match Summary" <h3> (dump 2026-07-07,
  // match 265360). Exclude it from live pick detection, or game 2+'s
  // count/ownership never resolves once game 1's recap card appears.
  function matchSummaryEl(main) {
    for (const h of main.querySelectorAll("h3")) {
      if (h.textContent.trim() === "Match Summary") return h.nextElementSibling;
    }
    return null;
  }

  // Each game gets its own "Game N" tab, all rendered into the DOM at once —
  // only the active tab's panel is visible, the rest sit under a
  // display:none ancestor (dumps 2026-07-22, match 286282: Game 1's panel
  // carried class="hidden" once Game 2 became active). offsetParent is null
  // for anything under display:none, so this is a cheap visibility check
  // without needing to know the site's exact class name for it. Without this,
  // a still-in-the-DOM Game 1 panel is read as the current game forever,
  // since it comes first in document order.
  function isHidden(el) {
    return el.offsetParent === null;
  }

  // Backup signal: during stage striking the page shows the whole stage grid
  // (9 splash images); once the stage is locked in, exactly one remains —
  // its alt text is the display name ("Small Battlefield", dump 2026-07-10).
  function stageInfo() {
    const main = document.querySelector("main") || document.body;
    const summary = matchSummaryEl(main);
    const bySrc = new Map();
    for (const img of main.querySelectorAll('img[src*="/images/stages/"]')) {
      if (summary && summary.contains(img)) continue;
      if (isHidden(img)) continue;
      const src = img.getAttribute("src");
      if (!bySrc.has(src)) bySrc.set(src, (img.getAttribute("alt") || "").trim());
    }
    return { count: bySrc.size, name: bySrc.size === 1 ? [...bySrc.values()][0] || null : null };
  }

  function stagePicked() {
    return stageInfo().count === 1;
  }

  // Each score-panel side shows the player's name and their character splash
  // (img alt = character name) in the same container: walk up from the splash
  // to the first ancestor whose text names exactly one player, and that
  // player owns the character (dump 2026-07-10, match 269435).
  function detectCharacters(players) {
    const main = document.querySelector("main") || document.body;
    const summary = matchSummaryEl(main);
    const byPlayer = new Map();
    for (const img of main.querySelectorAll('img[src*="/images/characters/splashes/"]')) {
      if (summary && summary.contains(img)) continue;
      if (isHidden(img)) continue;
      const alt = (img.getAttribute("alt") || "").trim();
      if (!alt) continue;
      let el = img.parentElement;
      while (el && el !== main.parentElement) {
        const text = el.innerText || "";
        const named = players.filter((p) => text.includes(p.name));
        if (named.length === 1) {
          if (!byPlayer.has(named[0].id)) byPlayer.set(named[0].id, alt);
          break;
        }
        if (named.length > 1) break;
        el = el.parentElement;
      }
    }
    return byPlayer;
  }

  // ---- state machine ---------------------------------------------------------

  function evaluate(reason) {
    const id = currentMatchId();

    if (!id) {
      detectMyId(); // sidebar exists on every page; cache my id early
      if (state.matchId) {
        log("left match page", state.matchId, `(${reason})`);
        if (state.stopOnLeave && state.startedSent && !state.finishedSent) {
          // Mid-match navigation elsewhere on the site: keep recording and
          // keep the match state so coming back continues the same file.
          // Only stop if the user stays away for the whole grace period.
          if (!awayTimer) {
            log(`recording continues; stopping in ${AWAY_GRACE_MS / 60000} min unless we return`);
            awayTimer = setTimeout(() => {
              log("grace period expired away from match", state.matchId, "- stopping recording");
              sendMatchEnded();
              resetMatchState();
            }, AWAY_GRACE_MS);
          }
        } else if (state.foundSent && !state.startedSent) {
          send("event", { type: "match_dismissed", matchId: state.matchId });
          resetMatchState();
        } else {
          resetMatchState();
        }
      }
      return;
    }

    // Back on a match page: if it's the match we were recording, cancel the
    // pending away-stop and carry on. A different match id means the old one
    // is over — stop its recording now so the new match starts clean.
    if (awayTimer) {
      if (state.matchId === id) {
        log("returned to match", id, "within grace period; recording continues");
        clearAwayTimer();
      } else {
        log("navigated to a different match", id, "- stopping recording of", state.matchId);
        sendMatchEnded();
        resetMatchState();
      }
    }

    const players = getMatchPlayers();
    const opponent = resolveOpponent(players);
    const myId = detectMyId();
    const iAmPlaying = !!(
      (myId && players.some((p) => p.id === myId)) ||
      (state.myName && players.some((p) => p.name.toLowerCase() === state.myName.toLowerCase()))
    );
    const text = mainText();
    const finishedWhy = finishedReason(text);
    const finished = finishedWhy !== null;
    // "Set" = stage + characters picked (game running); a completed game also
    // proves it, in case the page was opened mid-set.
    const stage = stagePicked();
    const ready = stage || READY_RE.test(text) || GAME_WON_RE.test(text);
    log(`evaluate(${reason})`, {
      id,
      players: players.map((p) => `${p.name}#${p.id}`),
      myId,
      iAmPlaying,
      opponent,
      stagePicked: stage,
      ready,
      finished,
      finishedWhy,
    });

    if (state.matchId !== id) {
      // Jumped straight from one match page to another while recording:
      // close out the old match before adopting the new one.
      if (state.matchId && state.startedSent && !state.finishedSent) {
        log("switched match pages", state.matchId, "->", id, "- stopping old recording");
        sendMatchEnded();
      }
      // New match page: reset. If it's already finished (browsing history),
      // mark it done so we never start recording for it.
      state.matchId = id;
      state.opponent = opponent;
      state.foundSent = false;
      state.startedSent = false;
      state.finishedSent = finished;
      state.lastStage = null;
      state.myChar = null;
      state.oppChar = null;
      state.liveGamesWon = -1;
      state.liveGamesLost = -1;
      state.liveInProgress = null;
      if (finished) log("match page is already finished, not recording");
    }

    // Tell the app the opponent is known (stage-striking phase, before the
    // match is "ready") so it can show the pre-match briefing.
    if (!state.foundSent && !state.startedSent && !state.finishedSent && !ready && iAmPlaying && opponent) {
      state.foundSent = true;
      send("event", {
        type: "match_found",
        matchId: id,
        opponent,
        players: players.map((p) => p.name),
      });
    }

    // Start only when the match is set (characters + stage picked), and only
    // for matches I'm actually playing in — not ones I spectate.
    if (!state.startedSent && !state.finishedSent && ready) {
      if (!iAmPlaying) {
        log("not a participant in this match, not recording");
      } else {
        state.startedSent = true;
        state.opponent = opponent;
        send("event", {
          type: "match_started",
          matchId: id,
          opponent,
          players: players.map((p) => p.name),
        });
      }
    }

    // Keep opponent info fresh, watch for the end.
    if (state.startedSent && opponent && opponent !== state.opponent) {
      state.opponent = opponent;
      send("event", { type: "match_update", matchId: id, opponent });
    }

    // Report the locked stage and both characters as they appear / change, so
    // the app can attach them to the match record (stage list grows per game).
    if (iAmPlaying && !state.finishedSent && (state.foundSent || state.startedSent)) {
      const info = {};
      const si = stageInfo();
      if (si.count === 1 && si.name && si.name !== state.lastStage) {
        info.stage = si.name;
        state.lastStage = si.name;
      }
      const chars = detectCharacters(players);
      const myChar = myId ? chars.get(myId) || null : null;
      const oppPlayer = myId ? players.find((p) => p.id !== myId) : null;
      const oppChar = oppPlayer ? chars.get(oppPlayer.id) || null : null;
      if (myChar && myChar !== state.myChar) {
        info.myCharacter = myChar;
        state.myChar = myChar;
      }
      if (oppChar && oppChar !== state.oppChar) {
        info.opponentCharacter = oppChar;
        state.oppChar = oppChar;
      }
      // Live game score from the server chat ("Server: <name> won Game N")
      // plus whether a game is currently running, so the app can attach notes
      // to the right game of the set. Same lines matchResult counts at the end.
      let gw = 0;
      let gl = 0;
      for (const g of text.matchAll(GAME_LINE_RE)) {
        if (sameName(g[1], state.opponent)) gl++;
        else gw++;
      }
      const inProgress = stage || READY_RE.test(text);
      if (gw !== state.liveGamesWon || gl !== state.liveGamesLost || inProgress !== state.liveInProgress) {
        state.liveGamesWon = gw;
        state.liveGamesLost = gl;
        state.liveInProgress = inProgress;
        info.gamesWon = gw;
        info.gamesLost = gl;
        info.gameInProgress = inProgress;
      }
      if (Object.keys(info).length) {
        send("event", { type: "match_update", matchId: id, opponent: state.opponent, ...info });
        // A newly reported character may need its icon fetched; give the app
        // a moment to store the update, then sync (hoisted from below).
        if (info.myCharacter || info.opponentCharacter) setTimeout(syncCharacterIcons, 3000);
      }
    }

    if (finished && !state.finishedSent) {
      state.finishedSent = true;
      if (state.startedSent) {
        send("event", {
          type: "match_ended",
          matchId: id,
          opponent: state.opponent,
          ...matchResult(text, players),
        });
      }
    }
  }

  // ---- companion-app banner --------------------------------------------------
  // Arc doesn't render dynamic toolbar icons, so warn in the page itself when
  // the companion app is down. A fixed overlay on <body> survives the site's
  // React re-renders (unlike nodes injected into the chat).

  let banner = null;
  let bannerDismissed = false;

  function removeBanner() {
    banner?.remove();
    banner = null;
  }

  function showBanner() {
    if (banner || bannerDismissed) return;
    banner = document.createElement("div");
    banner.style.cssText =
      "position:fixed;bottom:16px;right:16px;z-index:2147483647;max-width:340px;" +
      "background:#1f2937;color:#f9fafb;border:2px solid #f59e0b;border-radius:10px;" +
      "padding:12px 14px;font:13px/1.4 system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.5)";
    banner.innerHTML =
      '<div style="display:flex;align-items:baseline;gap:8px">' +
      '<b style="color:#f59e0b">Smash the Peak Recorder</b>' +
      '<span data-stp="close" style="margin-left:auto;cursor:pointer;color:#9ca3af;font-size:15px">&#10005;</span>' +
      "</div>" +
      '<div data-stp="text" style="margin:6px 0 10px">The recorder app is not running &mdash; your matches will NOT be recorded.</div>' +
      '<button data-stp="start" style="background:#f59e0b;color:#111;border:0;border-radius:6px;padding:6px 12px;font-weight:600;cursor:pointer">Start recorder app</button>';
    document.body.appendChild(banner);

    banner.querySelector('[data-stp="close"]').addEventListener("click", () => {
      bannerDismissed = true; // until it reconnects once
      removeBanner();
    });
    const el = banner;
    banner.querySelector('[data-stp="start"]').addEventListener("click", () => {
      const text = el.querySelector('[data-stp="text"]');
      const btn = el.querySelector('[data-stp="start"]');
      btn.disabled = true;
      text.textContent = "Starting the recorder app…";
      chrome.runtime.sendMessage({ kind: "launchApp" }, (res) => {
        if (chrome.runtime.lastError || !res || !res.ok) {
          text.textContent =
            "Could not launch automatically — start PeakRecorder.exe manually once, then this button will work.";
          btn.disabled = false;
          return;
        }
        // Poll until the bridge answers, then confirm and fade out.
        let tries = 0;
        const poll = setInterval(() => {
          chrome.runtime.sendMessage({ kind: "bridge", path: "status", method: "GET" }, (r) => {
            if (!el.isConnected) {
              clearInterval(poll);
              return;
            }
            if (r && r.ok) {
              clearInterval(poll);
              el.style.borderColor = "#22c55e";
              text.innerHTML = '<span style="color:#22c55e">Recorder connected ✓</span>';
              btn.remove();
              setTimeout(removeBanner, 3000);
            } else if (++tries > 10) {
              clearInterval(poll);
              text.textContent = "App launched but not reachable yet — check the system tray.";
              btn.disabled = false;
            }
          });
        }, 1000);
      });
    });
  }

  function checkCompanion() {
    chrome.runtime.sendMessage({ kind: "bridge", path: "status", method: "GET" }, (res) => {
      if (chrome.runtime.lastError || !res || !res.ok) {
        showBanner();
      } else {
        bannerDismissed = false; // re-warn on the next outage
        removeBanner();
      }
    });
  }

  checkCompanion();
  setInterval(checkCompanion, 15000);

  // ---- character icon sync ---------------------------------------------------
  // The app shows a stock icon next to each character name, but it can't
  // download them itself — the site answers requests from outside the browser
  // with 429s. From this content script the fetch is same-origin, rides the
  // browser's cookies and HTTP cache, and looks like normal page traffic, so
  // the app hands us its missing list and we deliver the images over the
  // bridge as base64.

  function bridgeRequest(path, method, body) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage({ kind: "bridge", path, method, body }, (res) => {
          if (chrome.runtime.lastError || !res || !res.ok) resolve(null);
          else resolve(res.data);
        });
      } catch {
        resolve(null);
      }
    });
  }

  function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
      const fr = new FileReader();
      fr.onload = () => resolve(String(fr.result).split(",")[1] || "");
      fr.onerror = () => reject(fr.error);
      fr.readAsDataURL(blob);
    });
  }

  // The icon files are named after the character with '&' spelled out as
  // 'And' and every other non-alphanumeric character dropped: "Dr. Mario" ->
  // "DrMario", "Pyra & Mythra" -> "PyraAndMythra".
  function iconSlug(name) {
    return name.replace(/&/g, "And").replace(/[^A-Za-z0-9]/g, "");
  }

  let iconSyncRunning = false;
  async function syncCharacterIcons() {
    if (iconSyncRunning) return;
    iconSyncRunning = true;
    try {
      const needed = await bridgeRequest("icons/needed", "GET");
      const names = (needed && needed.characters) || [];
      for (const name of names) {
        try {
          const r = await fetch(`/images/characters/icons/${iconSlug(name)}.webp`);
          if (!r.ok) {
            log("icon fetch failed:", name, r.status);
            if (r.status === 429) break; // rate-limited even here; next pass retries
            continue;
          }
          const blob = await r.blob();
          const res = await bridgeRequest("icons", "POST", {
            name,
            contentType: blob.type,
            data: await blobToBase64(blob),
          });
          log("icon delivered:", name, res && res.accepted ? "ok" : "rejected");
        } catch (e) {
          log("icon sync error:", name, e);
        }
        // One icon at a time with a pause, to stay under the rate limiter.
        await new Promise((r) => setTimeout(r, 2000));
      }
    } finally {
      iconSyncRunning = false;
    }
  }

  setTimeout(syncCharacterIcons, 5000);
  setInterval(syncCharacterIcons, 3 * 60 * 1000);

  // ---- diagnostics dump ------------------------------------------------------

  // Popup's "Dump page" button: capture the page structure so the detection
  // heuristics can be tuned against real markup.
  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg && msg.kind === "dumpRequest") {
      const anchors = [...document.querySelectorAll("a[href]")].slice(0, 400).map((a) => ({
        href: a.getAttribute("href"),
        text: a.textContent.trim().slice(0, 80),
        inHeaderNav: !!a.closest("header, nav, footer, [class*='navbar' i], [class*='header' i]"),
      }));
      const main = document.querySelector("main") || document.body;
      const dump = {
        url: location.href,
        title: document.title,
        myName: state.myName,
        myIdAuto: state.myIdAuto,
        myNameAuto: state.myNameAuto,
        detectedPlayers: getMatchPlayers(),
        finishedWhy: finishedReason(main.innerText || ""),
        stagePicked: stagePicked(),
        anchors,
        headings: [...document.querySelectorAll("h1,h2,h3,h4")].slice(0, 60).map((h) => ({
          tag: h.tagName,
          class: (h.className || "").toString().slice(0, 120),
          text: h.textContent.trim().slice(0, 120),
        })),
        mainText: (main.innerText || "").slice(0, 6000),
        html: document.documentElement.outerHTML.slice(0, 400000),
      };
      chrome.runtime.sendMessage({ kind: "bridge", path: "dump", body: dump }, (res) => {
        sendResponse(res || { ok: false, error: "no response" });
      });
      return true; // async
    }
  });

  // ---- navigation + DOM watching --------------------------------------------

  let debounceTimer = null;
  function scheduleEvaluate(reason) {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => evaluate(reason), 400);
  }

  // SPA navigations don't reload the page; hook history and watch the DOM.
  const origPush = history.pushState.bind(history);
  history.pushState = (...args) => {
    origPush(...args);
    scheduleEvaluate("pushState");
  };
  const origReplace = history.replaceState.bind(history);
  history.replaceState = (...args) => {
    origReplace(...args);
    scheduleEvaluate("replaceState");
  };
  window.addEventListener("popstate", () => scheduleEvaluate("popstate"));

  new MutationObserver(() => scheduleEvaluate("mutation")).observe(document.documentElement, {
    childList: true,
    subtree: true,
  });

  // If the tab/browser closes mid-match, try to stop the recording.
  window.addEventListener("pagehide", () => {
    if (state.matchId && state.stopOnLeave && state.startedSent && !state.finishedSent) {
      try {
        navigator.sendBeacon(
          "http://127.0.0.1:8123/event",
          JSON.stringify({
            type: "match_ended",
            matchId: state.matchId,
            opponent: state.opponent,
          })
        );
      } catch (_) {}
    }
  });

  log("content script loaded on", location.href);
})();

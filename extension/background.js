// Bridges messages from the content script / popup to the PeakRecorder
// companion app (http://127.0.0.1:8123), keeps the toolbar icon in sync with
// the app's state, and registers this extension's ID with the app so the
// native-messaging launch shortcut works.

const BRIDGE_BASE = "http://127.0.0.1:8123";
const HOST_NAME = "eu.smashthepeak.peakrecorder";

let registeredThisLife = false;

// ---- toolbar icon ----------------------------------------------------------

// "off" = companion app unreachable, "ok" = connected, "rec" = recording.
// Icons are static PNGs (see icons/). Note: Arc ignores chrome.action.setIcon
// entirely (both path and ImageData) but does render the badge, so the
// manifest default is the "ready" mark and the badge alone carries state
// there ("!" = app not running, "REC" = recording). Chrome/Edge show both.
function setIconState(state) {
  const name = state === "off" ? "disconnected" : state === "rec" ? "recording" : "ready";
  chrome.action
    .setIcon({ path: { 16: `icons/${name}-16.png`, 32: `icons/${name}-32.png` } })
    .catch((e) => console.warn("setIcon failed:", e));
  chrome.action.setBadgeText({ text: state === "off" ? "!" : state === "rec" ? "REC" : "" });
  chrome.action.setBadgeBackgroundColor({ color: state === "rec" ? "#ff4d94" : "#ffd166" });
  chrome.action.setTitle({
    title:
      state === "off"
        ? "Smash the Peak Recorder — companion app NOT running"
        : state === "rec"
          ? "Smash the Peak Recorder — recording"
          : "Smash the Peak Recorder — connected",
  });
}

async function checkStatus() {
  try {
    const r = await fetch(`${BRIDGE_BASE}/status`);
    const s = await r.json();
    setIconState(s.recording ? "rec" : "ok");
    if (!registeredThisLife) {
      registeredThisLife = true;
      // Tell the app our extension ID so it can register itself as a native
      // messaging host (enables the "Start companion app" popup button).
      fetch(`${BRIDGE_BASE}/register`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ extensionId: chrome.runtime.id }),
      }).catch(() => {});
    }
    return s;
  } catch {
    setIconState("off");
    return null;
  }
}

chrome.alarms.create("status-poll", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === "status-poll") checkStatus();
});
chrome.runtime.onInstalled.addListener(() => checkStatus());
chrome.runtime.onStartup.addListener(() => checkStatus());
checkStatus(); // also on every service-worker wake-up

// ---- bridge relay ----------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.kind === "launchApp") {
    // Content scripts can't use native messaging; relay for the page banner.
    chrome.runtime.sendNativeMessage(HOST_NAME, { cmd: "start" }, (res) => {
      if (chrome.runtime.lastError) {
        sendResponse({ ok: false, error: chrome.runtime.lastError.message });
      } else {
        sendResponse({ ok: true, data: res });
      }
    });
    return true;
  }
  if (msg && msg.kind === "bridge") {
    const opts = {
      method: msg.method || "POST",
      headers: { "Content-Type": "application/json" },
    };
    if (opts.method !== "GET" && msg.body !== undefined) {
      opts.body = JSON.stringify(msg.body);
    }
    fetch(`${BRIDGE_BASE}/${msg.path}`, opts)
      .then((r) => r.json())
      .then((data) => {
        sendResponse({ ok: true, data });
        checkStatus(); // events change recording state; refresh the icon now
      })
      .catch((e) => {
        setIconState("off");
        sendResponse({ ok: false, error: String(e) });
      });
    return true; // async response
  }
});

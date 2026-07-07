// Bridges messages from the content script / popup to the PeakRecorder
// companion app (http://127.0.0.1:8123), keeps the toolbar icon in sync with
// the app's state, and registers this extension's ID with the app so the
// native-messaging launch shortcut works.

const BRIDGE_BASE = "http://127.0.0.1:8123";

let registeredThisLife = false;

// ---- toolbar icon ----------------------------------------------------------

function drawDot(color, size) {
  const canvas = new OffscreenCanvas(size, size);
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, size, size);
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(size / 2, size / 2, size * 0.42, 0, Math.PI * 2);
  ctx.fill();
  return ctx.getImageData(0, 0, size, size);
}

// "off" = companion app unreachable, "ok" = connected, "rec" = recording
function setIconState(state) {
  const color = state === "off" ? "#9ca3af" : state === "rec" ? "#ef4444" : "#22c55e";
  chrome.action.setIcon({ imageData: { 16: drawDot(color, 16), 32: drawDot(color, 32) } });
  chrome.action.setBadgeText({ text: state === "off" ? "!" : state === "rec" ? "REC" : "" });
  chrome.action.setBadgeBackgroundColor({ color: state === "rec" ? "#ef4444" : "#f59e0b" });
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

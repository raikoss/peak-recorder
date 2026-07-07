const $ = (id) => document.getElementById(id);

function bridge(path, body, method) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage({ kind: "bridge", path, body, method }, (res) => {
      resolve(chrome.runtime.lastError ? { ok: false, error: chrome.runtime.lastError.message } : res);
    });
  });
}

async function refreshStatus() {
  const el = $("status");
  const res = await bridge("status", undefined, "GET");
  if (res && res.ok) {
    const s = res.data;
    el.className = "status ok";
    el.textContent = s.recording
      ? `Recording — vs ${s.opponent || "unknown opponent"}`
      : "Companion app connected, not recording";
  } else {
    el.className = "status bad";
    el.textContent = "Companion app not running (start PeakRecorder.exe)";
  }
}

chrome.storage.local.get(["myName", "myNameAuto", "myIdAuto", "stopOnLeave", "debug"], (v) => {
  $("myName").value = v.myName || "";
  $("autoName").textContent = v.myIdAuto
    ? `Auto-detected: ${v.myNameAuto || "player"} (#${v.myIdAuto})`
    : "";
  $("stopOnLeave").checked = v.stopOnLeave !== false;
  $("debug").checked = v.debug !== false;
});

$("myName").addEventListener("change", () => chrome.storage.local.set({ myName: $("myName").value.trim() }));
$("stopOnLeave").addEventListener("change", () => chrome.storage.local.set({ stopOnLeave: $("stopOnLeave").checked }));
$("debug").addEventListener("change", () => chrome.storage.local.set({ debug: $("debug").checked }));

$("startBtn").addEventListener("click", async () => {
  await bridge("event", { type: "manual_start" });
  refreshStatus();
});
$("stopBtn").addEventListener("click", async () => {
  await bridge("event", { type: "manual_stop" });
  refreshStatus();
});

$("dumpBtn").addEventListener("click", () => {
  const out = $("dumpResult");
  out.textContent = "Dumping…";
  chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
    if (!tabs[0]) return (out.textContent = "No active tab.");
    chrome.tabs.sendMessage(tabs[0].id, { kind: "dumpRequest" }, (res) => {
      if (chrome.runtime.lastError) {
        out.textContent = "Not a smashthepeak.eu tab (or reload the page after updating the extension).";
      } else if (res && res.ok) {
        out.textContent = `Saved: ${res.data.path || "ok"}`;
      } else {
        out.textContent = `Failed: ${res && res.error}`;
      }
    });
  });
});

refreshStatus();
setInterval(refreshStatus, 2000);

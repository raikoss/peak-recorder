// Bridges messages from the content script / popup to the PeakRecorder
// companion app listening on http://127.0.0.1:8123.

const BRIDGE_BASE = "http://127.0.0.1:8123";

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
      .then((data) => sendResponse({ ok: true, data }))
      .catch((e) => sendResponse({ ok: false, error: String(e) }));
    return true; // async response
  }
});

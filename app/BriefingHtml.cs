using System.Text.Encodings.Web;

namespace PeakRecorder;

/// <summary>
/// Renders the pre-match briefing shown while stage-striking (design turn 3).
/// Habits, game plan, and stage/set history are placeholder content — this
/// app doesn't track per-player scouting data yet, so every briefing shows
/// the same illustrative "coach notes" regardless of opponent.
/// </summary>
internal static class BriefingHtml
{
    private const string FontLink =
        "<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">" +
        "<link href=\"https://fonts.googleapis.com/css2?family=Sora:wght@400;600;700;800&display=swap\" rel=\"stylesheet\">";

    private static string Esc(string s) => HtmlEncoder.Default.Encode(s);

    private static string InitialLetter(string opponent) =>
        Esc(string.IsNullOrWhiteSpace(opponent) ? "?" : opponent.Trim()[..1].ToUpperInvariant());

    // Shared closing script: any element with data-action="close" posts a
    // message the host Form handles by closing the window.
    private const string CloseScript =
        "<script>document.addEventListener('click',e=>{const t=e.target.closest('[data-action]');" +
        "if(t&&window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(t.dataset.action);});</script>";

    // Card only: dragging the header moves the (borderless) window, unless
    // the mousedown started on a data-action element like the close button.
    private const string DragScript =
        "<script>document.querySelector('.hdr').addEventListener('mousedown',e=>{" +
        "if(e.target.closest('[data-action]'))return;" +
        "if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('drag');});</script>";

    public static string Full(string opponent)
    {
        var name = Esc(opponent);
        var letter = InitialLetter(opponent);
        return $$"""
<!DOCTYPE html><html><head><meta charset="utf-8">{{FontLink}}
<style>*{box-sizing:border-box}body{margin:0;background:#101426;font-family:Sora,system-ui,sans-serif;color:#e8ebf5;overflow:hidden}
.wrap{width:100vw;height:100vh;display:flex;flex-direction:column;position:relative;overflow:hidden}
.glow{position:absolute;top:-120px;right:-120px;width:420px;height:420px;border-radius:50%;background:radial-gradient(circle,rgba(255,77,148,.14),transparent 65%)}
.hdr{padding:12px 20px;display:flex;align-items:center;gap:10px;border-bottom:1px solid rgba(255,255,255,.07)}
.dot{width:7px;height:7px;border-radius:50%;background:#ffd166;box-shadow:0 0 7px #ffd166;flex:none}
.tag{display:flex;align-items:center;gap:7px;font:700 11px Sora,sans-serif;color:#ffd166;letter-spacing:.1em}
.sub{font:400 11px Sora,sans-serif;color:#6d7694}
.close{margin-left:auto;font:400 11px Sora,sans-serif;color:#6d7694;cursor:pointer}
.close:hover{color:#e8ebf5}
.who{padding:18px 24px 14px;display:flex;align-items:center;gap:16px}
.avatar{width:58px;height:58px;border-radius:16px;background:linear-gradient(135deg,#ff4d94,#8b6cff);display:flex;align-items:center;justify-content:center;font:800 23px Sora,sans-serif;color:#fff;flex:none}
.name{font:800 22px Sora,sans-serif}
.meta{font:400 11.5px Sora,sans-serif;color:#6d7694}
.body{flex:1;display:flex;gap:14px;padding:2px 24px 18px;min-height:0}
.col-main{flex:1.25;display:flex;flex-direction:column;gap:9px;min-width:0}
.hlabel{font:700 11px Sora,sans-serif;letter-spacing:.12em;color:#ff9dc4}
.habit{background:rgba(255,77,148,.08);border:1px solid rgba(255,77,148,.25);border-radius:11px;padding:11px 13px;display:flex;gap:10px;align-items:baseline}
.hnum{font:800 14px Sora,sans-serif;color:#ff4d94;flex:none}
.htext{font:600 12.5px/1.5 Sora,sans-serif;color:#f0e6ee}
.plan{background:rgba(61,220,132,.07);border:1px solid rgba(61,220,132,.25);border-radius:11px;padding:11px 13px;display:flex;flex-direction:column;gap:4px}
.planlabel{font:700 10px Sora,sans-serif;color:#7fe8ad;letter-spacing:.1em}
.plantext{font:400 12px/1.55 Sora,sans-serif;color:#d4daea}
.focus{background:linear-gradient(120deg,rgba(255,77,148,.12),rgba(69,196,255,.08));border:1px solid rgba(255,77,148,.25);border-radius:11px;padding:10px 13px;display:flex;gap:9px;align-items:baseline}
.focuslabel{font:700 10px Sora,sans-serif;color:#ff9dc4;letter-spacing:.1em;flex:none}
.col-side{width:280px;flex:none;display:flex;flex-direction:column;gap:9px}
.slabel{font:700 11px Sora,sans-serif;letter-spacing:.12em;color:#6d7694}
.stages{background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:11px;padding:11px 13px;display:flex;flex-direction:column;gap:8px}
.srow{display:flex;align-items:center;gap:8px;font:400 11px Sora,sans-serif;color:#a9b2cc}
.sname{width:72px;flex:none;font-weight:600}
.sbar{flex:1;height:7px;border-radius:4px;background:rgba(255,255,255,.08);overflow:hidden}
.sfill{height:100%}
.sscore{flex:none;font-weight:700}
.chips{display:flex;gap:7px;margin-top:2px}
.chip{border-radius:6px;padding:3px 9px;font:700 10px Sora,sans-serif}
.ban{background:rgba(255,109,109,.12);border:1px solid rgba(255,109,109,.35);color:#ff8f8f}
.pick{background:rgba(61,220,132,.1);border:1px solid rgba(61,220,132,.3);color:#7fe8ad}
.sets{background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:11px;padding:6px 13px;display:flex;flex-direction:column}
.setrow{display:flex;align-items:center;gap:8px;padding:7px 0;border-bottom:1px solid rgba(255,255,255,.06);font:400 11px Sora,sans-serif;color:#a9b2cc}
.setrow:last-child{border-bottom:none}
.obs{margin-top:auto;display:flex;align-items:center;gap:8px;background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:10px;padding:9px 12px;font:400 10.5px Sora,sans-serif;color:#6d7694}
.reado{width:7px;height:7px;border-radius:50%;background:#3ddc84;flex:none}
</style></head>
<body><div class="wrap"><div class="glow"></div>
<div class="hdr"><span class="tag"><span class="dot"></span>MATCH FOUND &middot; STAGE STRIKING</span><span class="sub">recording starts when the stage locks in</span><span class="close" data-action="close">&#10005; close</span></div>
<div class="who"><div class="avatar">{{letter}}</div><div><div class="name">{{name}}</div><div class="meta">Scouting data isn't tracked yet &mdash; the notes below are illustrative.</div></div></div>
<div class="body">
<div class="col-main">
<div class="hlabel">HABITS TO WATCH FOR</div>
<div class="habit"><span class="hnum">1</span><div class="htext">Watch their ledge options &mdash; note habits here once you start tagging notes as #habit.</div></div>
<div class="habit"><span class="hnum">2</span><div class="htext">Recovery tendencies show up here after a few recorded sets against this opponent.</div></div>
<div class="plan"><div class="planlabel">YOUR GAME PLAN</div><div class="plantext">Add a game plan note during a set and it'll show up here automatically next time you play this opponent.</div></div>
<div class="focus"><span class="focuslabel">FOCUS</span><div class="htext">Set a practice focus and it'll follow you into every match.</div></div>
</div>
<div class="col-side">
<div class="slabel">STAGES</div>
<div class="stages">
<div class="srow"><span class="sname">Battlefield</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
<div class="srow"><span class="sname">Smashville</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
<div class="srow"><span class="sname">PS2</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
<div class="chips"><span class="chip ban">no data yet</span></div>
</div>
<div class="slabel">PAST SETS</div>
<div class="sets"><div class="setrow">No recorded sets against {{name}} yet.</div></div>
<div class="obs"><span class="reado"></span>OBS ready &middot; recording starts automatically</div>
</div>
</div>
</div>{{CloseScript}}
</body></html>
""";
    }

    public static string Card(string opponent)
    {
        var name = Esc(opponent);
        var letter = InitialLetter(opponent);
        return $$"""
<!DOCTYPE html><html><head><meta charset="utf-8">{{FontLink}}
<style>*{box-sizing:border-box}html,body{margin:0;background:transparent}
.card{width:380px;height:560px;display:flex;flex-direction:column;background:#0c1020;border:1px solid rgba(255,255,255,.14);border-radius:14px;font-family:Sora,system-ui,sans-serif;color:#e8ebf5;overflow:hidden}
.hdr{padding:10px 14px;display:flex;align-items:center;gap:8px;background:rgba(255,209,102,.07);border-bottom:1px solid rgba(255,255,255,.07);cursor:move}
.dot{width:7px;height:7px;border-radius:50%;background:#ffd166;box-shadow:0 0 7px #ffd166;flex:none}
.tag{font:700 10px Sora,sans-serif;color:#ffd166;letter-spacing:.1em}
.sub{font:400 10px Sora,sans-serif;color:#6d7694}
.actions{margin-left:auto;display:flex;gap:8px;font:400 12px Sora,sans-serif;color:#6d7694}
.actions span{cursor:pointer}
.actions span:hover{color:#e8ebf5}
.who{padding:12px 14px 10px;display:flex;align-items:center;gap:11px;border-bottom:1px solid rgba(255,255,255,.07)}
.avatar{width:40px;height:40px;border-radius:11px;background:linear-gradient(135deg,#ff4d94,#8b6cff);display:flex;align-items:center;justify-content:center;font:800 16px Sora,sans-serif;color:#fff;flex:none}
.name{font:800 15px Sora,sans-serif}
.meta{font:400 10px Sora,sans-serif;color:#6d7694}
.body{flex:1;padding:12px 14px;display:flex;flex-direction:column;gap:8px;min-height:0}
.hlabel{font:700 10px Sora,sans-serif;letter-spacing:.12em;color:#ff9dc4}
.htext{font:600 11.5px/1.5 Sora,sans-serif;color:#f0e6ee;border-left:2px solid #ff4d94;padding-left:9px}
.plan{background:rgba(61,220,132,.07);border:1px solid rgba(61,220,132,.25);border-radius:9px;padding:8px 11px;display:flex;flex-direction:column;gap:3px}
.planlabel{font:700 9.5px Sora,sans-serif;color:#7fe8ad;letter-spacing:.1em}
.plantext{font:400 11px/1.5 Sora,sans-serif;color:#d4daea}
.setrow{padding:5px 0;font:400 10.5px Sora,sans-serif;color:#a9b2cc}
.foot{padding:8px 14px;display:flex;align-items:center;gap:7px;border-top:1px solid rgba(255,255,255,.07);font:400 10px Sora,sans-serif;color:#6d7694}
.reado{width:6px;height:6px;border-radius:50%;background:#3ddc84;flex:none}
</style></head>
<body><div class="card">
<div class="hdr"><span class="dot"></span><span class="tag">MATCH FOUND</span><span class="sub">auto-hides at game start</span><span class="actions"><span data-action="close">&#10005;</span></span></div>
<div class="who"><div class="avatar">{{letter}}</div><div><div class="name">{{name}}</div><div class="meta">No recorded history yet</div></div></div>
<div class="body">
<div class="hlabel">HABITS</div>
<div class="htext">Tag notes with #habit during sets to build a cheat sheet for next time.</div>
<div class="plan"><div class="planlabel">GAME PLAN</div><div class="plantext">Add a game plan note and it'll show up here on your next match against {{name}}.</div></div>
<div class="setrow">No recorded sets against {{name}} yet.</div>
</div>
<div class="foot"><span class="reado"></span>OBS ready &middot; recording starts with game 1</div>
</div>{{CloseScript}}{{DragScript}}
</body></html>
""";
    }
}

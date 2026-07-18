using System.Text.Encodings.Web;

namespace PeakRecorder;

/// <summary>
/// Renders the pre-match briefing shown while stage-striking (design turn 3),
/// filled with the opponent's recorded history from the Store: #habit notes,
/// the saved game plan, set results and per-stage record. Sections fall back
/// to explanatory placeholder text until there is data for them.
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

    // ---- scouting data ---------------------------------------------------------

    private sealed record Scouting(
        List<MatchRecord> Matches,   // newest first (Snapshot order)
        List<MatchRecord> Finished,  // subset with a W/L result
        int Wins, int Losses,
        List<string> Habits,         // #habit note texts, newest match first
        string? GamePlan,
        string FocusGoal);

    private static Scouting Scout(AppData data, string opponent)
    {
        var key = opponent.Trim().ToLowerInvariant();
        var matches = data.Matches.Where(m => m.Opponent.Trim().ToLowerInvariant() == key).ToList();
        var finished = matches.Where(m => m.Result != null).ToList();
        var habits = matches.SelectMany(m => m.Notes)
            .Where(n => n.Tags.Contains("habit")).Select(n => n.Text).ToList();
        data.GamePlans.TryGetValue(key, out var plan);
        return new Scouting(matches, finished,
            finished.Count(m => m.Result == "W"), finished.Count(m => m.Result == "L"),
            habits, string.IsNullOrWhiteSpace(plan) ? null : plan, data.FocusGoal);
    }

    private static string HistoryLine(Scouting sc) =>
        sc.Matches.Count == 0
            ? "No recorded history yet"
            : $"{sc.Matches.Count} recorded match{(sc.Matches.Count == 1 ? "" : "es")} &middot; {sc.Wins}&ndash;{sc.Losses} sets vs you";

    private static string SetResultSpan(MatchRecord m) =>
        $"<span style=\"color:{(m.Result == "W" ? "#3ddc84" : "#ff6d6d")};font-weight:700\">{m.Result} {m.GamesWon}&ndash;{m.GamesLost}</span>";

    private static string SetDate(MatchRecord m) => Esc(m.StartedAt.ToString("MMM d HH:mm"));

    private static Dictionary<string, (int W, int L)> StageStats(Scouting sc)
    {
        var stats = new Dictionary<string, (int W, int L)>();
        foreach (var m in sc.Finished)
            foreach (var st in m.Stages)
            {
                var s = stats.GetValueOrDefault(st);
                stats[st] = m.Result == "W" ? (s.W + 1, s.L) : (s.W, s.L + 1);
            }
        return stats;
    }

    // ---- pages -----------------------------------------------------------------

    public static string Full(string opponent, AppData data)
    {
        var sc = Scout(data, opponent);
        var name = Esc(opponent);
        var letter = InitialLetter(opponent);

        var habits = sc.Habits.Count == 0
            ? """
              <div class="habit"><span class="hnum">1</span><div class="htext">Watch their ledge options &mdash; note habits here once you start tagging notes as #habit.</div></div>
              <div class="habit"><span class="hnum">2</span><div class="htext">Recovery tendencies show up here after a few recorded sets against this opponent.</div></div>
              """
            : string.Join("", sc.Habits.Take(3).Select((h, i) =>
                $"""<div class="habit"><span class="hnum">{i + 1}</span><div class="htext">{Esc(h)}</div></div>"""));

        var plan = sc.GamePlan != null
            ? Esc(sc.GamePlan)
            : "Add a game plan from the player page and it'll show up here automatically next time you play this opponent.";

        var focus = string.IsNullOrWhiteSpace(sc.FocusGoal)
            ? "Set a practice focus and it'll follow you into every match."
            : Esc(sc.FocusGoal);

        var stageStats = StageStats(sc);
        var stages = stageStats.Count == 0
            ? """
              <div class="srow"><span class="sname">Battlefield</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
              <div class="srow"><span class="sname">Smashville</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
              <div class="srow"><span class="sname">PS2</span><div class="sbar"><div class="sfill" style="width:0%;background:#6d7694"></div></div><span class="sscore">&mdash;</span></div>
              <div class="chips"><span class="chip ban">no data yet</span></div>
              """
            : string.Join("", stageStats.Select(kv =>
              {
                  var (w, l) = kv.Value;
                  var pct = w + l == 0 ? 0 : 100 * w / (w + l);
                  return $"""<div class="srow"><span class="sname">{Esc(kv.Key)}</span><div class="sbar"><div class="sfill" style="width:{pct}%;background:{(pct >= 50 ? "#3ddc84" : "#ff6d6d")}"></div></div><span class="sscore">{w}&ndash;{l}</span></div>""";
              }));

        var sets = sc.Finished.Count == 0
            ? $"""<div class="setrow">No recorded sets against {name} yet.</div>"""
            : string.Join("", sc.Finished.Take(5).Select(m =>
                $"""<div class="setrow"><span>{SetDate(m)}</span><span style="margin-left:auto">{SetResultSpan(m)}</span></div>"""));

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
<div class="who"><div class="avatar">{{letter}}</div><div><div class="name">{{name}}</div><div class="meta">{{HistoryLine(sc)}}</div></div></div>
<div class="body">
<div class="col-main">
<div class="hlabel">HABITS TO WATCH FOR</div>
{{habits}}
<div class="plan"><div class="planlabel">YOUR GAME PLAN</div><div class="plantext">{{plan}}</div></div>
<div class="focus"><span class="focuslabel">FOCUS</span><div class="htext">{{focus}}</div></div>
</div>
<div class="col-side">
<div class="slabel">STAGES</div>
<div class="stages">
{{stages}}
</div>
<div class="slabel">PAST SETS</div>
<div class="sets">{{sets}}</div>
<div class="obs"><span class="reado"></span>OBS ready &middot; recording starts automatically</div>
</div>
</div>
</div>{{CloseScript}}
</body></html>
""";
    }

}

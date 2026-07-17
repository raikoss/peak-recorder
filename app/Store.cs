using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakRecorder;

public sealed class Note
{
    public int TimestampSeconds { get; set; }
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class MatchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? MatchId { get; set; }
    public string Opponent { get; set; } = "unknown";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public string? VideoPath { get; set; }

    // Nothing below this line is auto-detected today — the extension only
    // reports opponent + match id, so result/stages/characters are filled in
    // by hand from the library view after the fact.
    public string? Result { get; set; } // "W" | "L" | null
    public int GamesWon { get; set; }
    public int GamesLost { get; set; }
    public List<string> Stages { get; set; } = [];
    public string? MyCharacter { get; set; }
    public string? OpponentCharacter { get; set; }

    public List<Note> Notes { get; set; } = [];
}

public sealed class AppData
{
    public string FocusGoal { get; set; } = "";
    public Dictionary<string, string> GamePlans { get; set; } = []; // opponent (lowercased) -> free text
    public List<MatchRecord> Matches { get; set; } = [];
}

/// <summary>
/// Local JSON store for matches, notes, focus goal and per-opponent game
/// plans — everything the main window / player page / briefing show that
/// isn't OBS or bridge configuration (that stays in Config).
/// </summary>
public sealed class Store
{
    private readonly object _lock = new();
    private AppData _data;

    public static string FilePath => Path.Combine(Config.Dir, "data.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private Store(AppData data) => _data = data;

    public static Store Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath));
                if (data != null) return new Store(data);
            }
            catch (Exception ex)
            {
                Log.Write($"Failed to read data.json, starting fresh (old file kept): {ex.Message}");
            }
        }
        return new Store(new AppData());
    }

    private void Save()
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOpts));
    }

    public event Action? Changed;

    private void MutateAndSave(Action<AppData> mutate)
    {
        lock (_lock)
        {
            mutate(_data);
            Save();
        }
        Changed?.Invoke();
    }

    public AppData Snapshot()
    {
        lock (_lock)
        {
            // Deep-enough copy via round trip; the store is small so this is cheap
            // and keeps callers from mutating internal state directly.
            return JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(_data, JsonOpts))!;
        }
    }

    public MatchRecord StartMatch(string matchId, string opponent)
    {
        var record = new MatchRecord { MatchId = matchId, Opponent = opponent };
        MutateAndSave(d => d.Matches.Insert(0, record));
        return record;
    }

    public void EndMatch(string recordId, string? videoPath)
    {
        MutateAndSave(d =>
        {
            var m = d.Matches.FirstOrDefault(x => x.Id == recordId);
            if (m == null) return;
            m.EndedAt = DateTime.Now;
            m.VideoPath = videoPath;
        });
    }

    public void AddNote(string matchRecordId, int timestampSeconds, string text, List<string> tags)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        MutateAndSave(d =>
        {
            var m = d.Matches.FirstOrDefault(x => x.Id == matchRecordId);
            m?.Notes.Add(new Note { TimestampSeconds = timestampSeconds, Text = text.Trim(), Tags = tags });
        });
    }

    public void UpdateMatchMeta(string matchRecordId, string? result, int gamesWon, int gamesLost,
        List<string> stages, string? myCharacter, string? opponentCharacter)
    {
        MutateAndSave(d =>
        {
            var m = d.Matches.FirstOrDefault(x => x.Id == matchRecordId);
            if (m == null) return;
            m.Result = result;
            m.GamesWon = gamesWon;
            m.GamesLost = gamesLost;
            m.Stages = stages;
            m.MyCharacter = myCharacter;
            m.OpponentCharacter = opponentCharacter;
        });
    }

    public void SetFocusGoal(string text) => MutateAndSave(d => d.FocusGoal = text.Trim());

    public void SetGamePlan(string opponent, string text) =>
        MutateAndSave(d => d.GamePlans[opponent.Trim().ToLowerInvariant()] = text.Trim());

    public void DeleteMatch(string matchRecordId) =>
        MutateAndSave(d => d.Matches.RemoveAll(x => x.Id == matchRecordId));
}

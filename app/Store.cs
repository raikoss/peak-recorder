using System.Text.Json;
using Microsoft.Data.Sqlite;

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
/// Local SQLite store for matches, notes, focus goal and per-opponent game
/// plans — everything the main window / player page / briefing show that
/// isn't OBS or bridge configuration (that stays in Config).
/// An existing data.json from older versions is imported on first load and
/// kept alongside the database as data.json.imported.
/// </summary>
public sealed class Store
{
    private readonly object _lock = new();
    private readonly string _connectionString;

    public static string FilePath => Path.Combine(Config.Dir, "data.db");
    private static string LegacyJsonPath => Path.Combine(Config.Dir, "data.json");

    private Store(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public static Store Load()
    {
        Directory.CreateDirectory(Config.Dir);
        var store = new Store(new SqliteConnectionStringBuilder { DataSource = FilePath }.ToString());
        store.EnsureSchema();
        store.ImportLegacyJsonIfPresent();
        return store;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS matches (
                id                 TEXT PRIMARY KEY,
                match_id           TEXT,
                opponent           TEXT NOT NULL,
                started_at         TEXT NOT NULL,
                ended_at           TEXT,
                video_path         TEXT,
                result             TEXT,
                games_won          INTEGER NOT NULL DEFAULT 0,
                games_lost         INTEGER NOT NULL DEFAULT 0,
                stages             TEXT NOT NULL DEFAULT '[]',
                my_character       TEXT,
                opponent_character TEXT
            );
            CREATE TABLE IF NOT EXISTS notes (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                match_record_id   TEXT NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                timestamp_seconds INTEGER NOT NULL,
                text              TEXT NOT NULL,
                tags              TEXT NOT NULL DEFAULT '[]',
                created_at        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_notes_match ON notes(match_record_id);
            CREATE TABLE IF NOT EXISTS game_plans (
                opponent TEXT PRIMARY KEY,
                text     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>One-time import of the pre-SQLite data.json. The JSON file is
    /// renamed (not deleted) so nothing is lost if the import needs redoing.</summary>
    private void ImportLegacyJsonIfPresent()
    {
        if (!File.Exists(LegacyJsonPath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(LegacyJsonPath));
            if (data != null)
            {
                lock (_lock)
                {
                    using var conn = Open();
                    using var tx = conn.BeginTransaction();
                    SetSetting(conn, "focus_goal", data.FocusGoal);
                    foreach (var (opponent, text) in data.GamePlans)
                        UpsertGamePlan(conn, opponent, text);
                    foreach (var m in data.Matches)
                    {
                        InsertMatch(conn, m);
                        foreach (var n in m.Notes) InsertNote(conn, m.Id, n);
                    }
                    tx.Commit();
                }
            }
            File.Move(LegacyJsonPath, LegacyJsonPath + ".imported", overwrite: true);
            Log.Write($"Imported data.json into {Path.GetFileName(FilePath)} (original kept as data.json.imported)");
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to import legacy data.json, leaving it in place: {ex.Message}");
        }
    }

    public event Action? Changed;

    private void Mutate(Action<SqliteConnection> work)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            work(conn);
            tx.Commit();
        }
        Changed?.Invoke();
    }

    public AppData Snapshot()
    {
        lock (_lock)
        {
            using var conn = Open();
            var data = new AppData { FocusGoal = GetSetting(conn, "focus_goal") ?? "" };

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT opponent, text FROM game_plans";
                using var r = cmd.ExecuteReader();
                while (r.Read()) data.GamePlans[r.GetString(0)] = r.GetString(1);
            }

            var byId = new Dictionary<string, MatchRecord>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, match_id, opponent, started_at, ended_at, video_path,
                           result, games_won, games_lost, stages, my_character, opponent_character
                    FROM matches ORDER BY started_at DESC
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var m = new MatchRecord
                    {
                        Id = r.GetString(0),
                        MatchId = r.IsDBNull(1) ? null : r.GetString(1),
                        Opponent = r.GetString(2),
                        StartedAt = ParseDate(r.GetString(3)),
                        EndedAt = r.IsDBNull(4) ? null : ParseDate(r.GetString(4)),
                        VideoPath = r.IsDBNull(5) ? null : r.GetString(5),
                        Result = r.IsDBNull(6) ? null : r.GetString(6),
                        GamesWon = r.GetInt32(7),
                        GamesLost = r.GetInt32(8),
                        Stages = ParseStringList(r.GetString(9)),
                        MyCharacter = r.IsDBNull(10) ? null : r.GetString(10),
                        OpponentCharacter = r.IsDBNull(11) ? null : r.GetString(11),
                    };
                    byId[m.Id] = m;
                    data.Matches.Add(m);
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT match_record_id, timestamp_seconds, text, tags, created_at
                    FROM notes ORDER BY id
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!byId.TryGetValue(r.GetString(0), out var m)) continue;
                    m.Notes.Add(new Note
                    {
                        TimestampSeconds = r.GetInt32(1),
                        Text = r.GetString(2),
                        Tags = ParseStringList(r.GetString(3)),
                        CreatedAt = ParseDate(r.GetString(4)),
                    });
                }
            }

            return data;
        }
    }

    public MatchRecord StartMatch(string matchId, string opponent)
    {
        var record = new MatchRecord { MatchId = matchId, Opponent = opponent };
        Mutate(conn => InsertMatch(conn, record));
        return record;
    }

    public void EndMatch(string recordId, string? videoPath)
    {
        Mutate(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE matches SET ended_at = $ended, video_path = $video WHERE id = $id";
            cmd.Parameters.AddWithValue("$ended", FormatDate(DateTime.Now));
            cmd.Parameters.AddWithValue("$video", (object?)videoPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", recordId);
            cmd.ExecuteNonQuery();
        });
    }

    public void AddNote(string matchRecordId, int timestampSeconds, string text, List<string> tags)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Mutate(conn => InsertNote(conn, matchRecordId,
            new Note { TimestampSeconds = timestampSeconds, Text = text.Trim(), Tags = tags }));
    }

    public void UpdateMatchMeta(string matchRecordId, string? result, int gamesWon, int gamesLost,
        List<string> stages, string? myCharacter, string? opponentCharacter)
    {
        Mutate(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE matches SET result = $result, games_won = $won, games_lost = $lost,
                    stages = $stages, my_character = $mine, opponent_character = $theirs
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$won", gamesWon);
            cmd.Parameters.AddWithValue("$lost", gamesLost);
            cmd.Parameters.AddWithValue("$stages", JsonSerializer.Serialize(stages));
            cmd.Parameters.AddWithValue("$mine", (object?)myCharacter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$theirs", (object?)opponentCharacter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", matchRecordId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Auto-detected result reported by the extension when a match
    /// finishes; stages/characters stay manual (UpdateMatchMeta).</summary>
    public void SetMatchResult(string matchRecordId, string result, int gamesWon, int gamesLost)
    {
        Mutate(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE matches SET result = $result, games_won = $won, games_lost = $lost WHERE id = $id";
            cmd.Parameters.AddWithValue("$result", result);
            cmd.Parameters.AddWithValue("$won", gamesWon);
            cmd.Parameters.AddWithValue("$lost", gamesLost);
            cmd.Parameters.AddWithValue("$id", matchRecordId);
            cmd.ExecuteNonQuery();
        });
    }

    public void SetFocusGoal(string text) => Mutate(conn => SetSetting(conn, "focus_goal", text.Trim()));

    public void SetGamePlan(string opponent, string text) =>
        Mutate(conn => UpsertGamePlan(conn, opponent.Trim().ToLowerInvariant(), text.Trim()));

    /// <summary>Removes a pre-match record whose match never started, unless
    /// the user already jotted notes on it — those are worth keeping.</summary>
    public void DeleteMatchIfEmpty(string matchRecordId)
    {
        Mutate(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM matches WHERE id = $id
                  AND NOT EXISTS (SELECT 1 FROM notes WHERE match_record_id = $id)
                """;
            cmd.Parameters.AddWithValue("$id", matchRecordId);
            cmd.ExecuteNonQuery();
        });
    }

    public void DeleteMatch(string matchRecordId)
    {
        Mutate(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM matches WHERE id = $id"; // notes cascade
            cmd.Parameters.AddWithValue("$id", matchRecordId);
            cmd.ExecuteNonQuery();
        });
    }

    private static void InsertMatch(SqliteConnection conn, MatchRecord m)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO matches (id, match_id, opponent, started_at, ended_at, video_path,
                result, games_won, games_lost, stages, my_character, opponent_character)
            VALUES ($id, $matchId, $opponent, $started, $ended, $video,
                $result, $won, $lost, $stages, $mine, $theirs)
            """;
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$matchId", (object?)m.MatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$opponent", m.Opponent);
        cmd.Parameters.AddWithValue("$started", FormatDate(m.StartedAt));
        cmd.Parameters.AddWithValue("$ended", m.EndedAt is { } e ? FormatDate(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$video", (object?)m.VideoPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$result", (object?)m.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$won", m.GamesWon);
        cmd.Parameters.AddWithValue("$lost", m.GamesLost);
        cmd.Parameters.AddWithValue("$stages", JsonSerializer.Serialize(m.Stages));
        cmd.Parameters.AddWithValue("$mine", (object?)m.MyCharacter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$theirs", (object?)m.OpponentCharacter ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void InsertNote(SqliteConnection conn, string matchRecordId, Note n)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (match_record_id, timestamp_seconds, text, tags, created_at)
            VALUES ($match, $ts, $text, $tags, $created)
            """;
        cmd.Parameters.AddWithValue("$match", matchRecordId);
        cmd.Parameters.AddWithValue("$ts", n.TimestampSeconds);
        cmd.Parameters.AddWithValue("$text", n.Text);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(n.Tags));
        cmd.Parameters.AddWithValue("$created", FormatDate(n.CreatedAt));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertGamePlan(SqliteConnection conn, string opponent, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_plans (opponent, text) VALUES ($opponent, $text)
            ON CONFLICT(opponent) DO UPDATE SET text = excluded.text
            """;
        cmd.Parameters.AddWithValue("$opponent", opponent);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    private static void SetSetting(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static string? GetSetting(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    // ISO 8601 round-trip format sorts lexicographically, which the
    // ORDER BY started_at DESC in Snapshot relies on.
    private static string FormatDate(DateTime dt) => dt.ToString("o");

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static List<string> ParseStringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

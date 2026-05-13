using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PhotoWell.PromptHarness;

/// <summary>
/// All SQLite persistence for the prompt harness.
/// DB lives at tools/PhotoWell.PromptHarness/harness.db by default.
/// </summary>
public sealed class HarnessDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public HarnessDb(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
    }

    // ── Schema ─────────────────────────────────────────────────────────────────

    public void EnsureSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS sample_images (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                image_path  TEXT NOT NULL,
                thumb_path  TEXT NOT NULL,
                file_name   TEXT NOT NULL,
                sampled_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ground_truth (
                image_id        INTEGER PRIMARY KEY REFERENCES sample_images(id),
                subject         TEXT NOT NULL DEFAULT '',
                person_count    INTEGER NOT NULL DEFAULT -1,
                setting         TEXT NOT NULL DEFAULT '',
                key_objects     TEXT NOT NULL DEFAULT '[]',
                verified_absent TEXT NOT NULL DEFAULT '[]',
                free_text       TEXT NOT NULL DEFAULT '',
                generated_at    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS prompts (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                version        TEXT NOT NULL UNIQUE,
                text           TEXT NOT NULL,
                parent_version TEXT,
                created_at     TEXT NOT NULL,
                notes          TEXT
            );

            CREATE TABLE IF NOT EXISTS eval_runs (
                run_id            TEXT PRIMARY KEY,
                prompt_version    TEXT NOT NULL,
                run_type          TEXT NOT NULL,
                ollama_model      TEXT NOT NULL,
                images_evaluated  INTEGER NOT NULL DEFAULT 0,
                images_skipped    INTEGER NOT NULL DEFAULT 0,
                pass_count        INTEGER NOT NULL DEFAULT 0,
                fail_count        INTEGER NOT NULL DEFAULT 0,
                avg_score         REAL NOT NULL DEFAULT 0,
                avg_max_score     REAL NOT NULL DEFAULT 0,
                started_at        TEXT NOT NULL,
                completed_at      TEXT
            );

            CREATE TABLE IF NOT EXISTS eval_results (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id              TEXT NOT NULL REFERENCES eval_runs(run_id),
                image_id            INTEGER NOT NULL REFERENCES sample_images(id),
                file_name           TEXT NOT NULL,
                ollama_description  TEXT,
                gate_scores_json    TEXT NOT NULL DEFAULT '{}',
                total_score         INTEGER NOT NULL DEFAULT 0,
                max_score           INTEGER NOT NULL DEFAULT 0,
                passed              INTEGER NOT NULL DEFAULT 0,
                notes               TEXT
            );
        """);
    }

    // ── Sample images ──────────────────────────────────────────────────────────

    public int GetSampleCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sample_images;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void ClearSample()
    {
        Execute("DELETE FROM ground_truth;");
        Execute("DELETE FROM sample_images;");
    }

    public void InsertSampleImage(SampleImage img)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sample_images (image_path, thumb_path, file_name, sampled_at)
            VALUES ($ip, $tp, $fn, $sa);
            """;
        cmd.Parameters.AddWithValue("$ip", img.ImagePath);
        cmd.Parameters.AddWithValue("$tp", img.ThumbPath);
        cmd.Parameters.AddWithValue("$fn", img.FileName);
        cmd.Parameters.AddWithValue("$sa", img.SampledAt);
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> GetExistingFileNames()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT file_name FROM sample_images;";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public List<SampleImage> GetSampleImages()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, image_path, thumb_path, file_name, sampled_at FROM sample_images ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<SampleImage>();
        while (r.Read())
            list.Add(new SampleImage
            {
                Id        = r.GetInt32(0),
                ImagePath = r.GetString(1),
                ThumbPath = r.GetString(2),
                FileName  = r.GetString(3),
                SampledAt = r.GetString(4)
            });
        return list;
    }

    // ── Ground truth ───────────────────────────────────────────────────────────

    public void UpsertGroundTruth(GroundTruth gt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ground_truth
                (image_id, subject, person_count, setting, key_objects, verified_absent, free_text, generated_at)
            VALUES ($id,$sub,$pc,$set,$ko,$va,$ft,$ga)
            ON CONFLICT(image_id) DO UPDATE SET
                subject=excluded.subject,
                person_count=excluded.person_count,
                setting=excluded.setting,
                key_objects=excluded.key_objects,
                verified_absent=excluded.verified_absent,
                free_text=excluded.free_text,
                generated_at=excluded.generated_at;
            """;
        cmd.Parameters.AddWithValue("$id",  gt.ImageId);
        cmd.Parameters.AddWithValue("$sub", gt.Subject);
        cmd.Parameters.AddWithValue("$pc",  gt.PersonCount);
        cmd.Parameters.AddWithValue("$set", gt.Setting);
        cmd.Parameters.AddWithValue("$ko",  JsonSerializer.Serialize(gt.KeyObjects));
        cmd.Parameters.AddWithValue("$va",  JsonSerializer.Serialize(gt.VerifiedAbsent));
        cmd.Parameters.AddWithValue("$ft",  gt.FreeText);
        cmd.Parameters.AddWithValue("$ga",  gt.GeneratedAt);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<int, GroundTruth> GetAllGroundTruth()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT image_id, subject, person_count, setting, key_objects,
                   verified_absent, free_text, generated_at
            FROM ground_truth;
            """;
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<int, GroundTruth>();
        while (r.Read())
        {
            int id = r.GetInt32(0);
            map[id] = new GroundTruth
            {
                ImageId        = id,
                Subject        = r.GetString(1),
                PersonCount    = r.GetInt32(2),
                Setting        = r.GetString(3),
                KeyObjects     = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [],
                VerifiedAbsent = JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],
                FreeText       = r.GetString(6),
                GeneratedAt    = r.GetString(7)
            };
        }
        return map;
    }

    public int GetGroundTruthCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ground_truth;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Prompts ────────────────────────────────────────────────────────────────

    public void InsertPrompt(PromptRecord p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO prompts (version, text, parent_version, created_at, notes)
            VALUES ($v, $t, $pv, $ca, $n);
            """;
        cmd.Parameters.AddWithValue("$v",  p.Version);
        cmd.Parameters.AddWithValue("$t",  p.Text);
        cmd.Parameters.AddWithValue("$pv", (object?)p.ParentVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", p.CreatedAt);
        cmd.Parameters.AddWithValue("$n",  (object?)p.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public PromptRecord? GetPrompt(string version)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, version, text, parent_version, created_at, notes FROM prompts WHERE version=$v;";
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new PromptRecord
        {
            Id            = r.GetInt32(0),
            Version       = r.GetString(1),
            Text          = r.GetString(2),
            ParentVersion = r.IsDBNull(3) ? null : r.GetString(3),
            CreatedAt     = r.GetString(4),
            Notes         = r.IsDBNull(5) ? null : r.GetString(5)
        };
    }

    public List<PromptRecord> GetAllPrompts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, version, text, parent_version, created_at, notes FROM prompts ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<PromptRecord>();
        while (r.Read())
            list.Add(new PromptRecord
            {
                Id            = r.GetInt32(0),
                Version       = r.GetString(1),
                Text          = r.GetString(2),
                ParentVersion = r.IsDBNull(3) ? null : r.GetString(3),
                CreatedAt     = r.GetString(4),
                Notes         = r.IsDBNull(5) ? null : r.GetString(5)
            });
        return list;
    }

    // ── Eval runs ──────────────────────────────────────────────────────────────

    public void InsertRun(EvalRun run)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO eval_runs
                (run_id, prompt_version, run_type, ollama_model, images_evaluated,
                 images_skipped, pass_count, fail_count, avg_score, avg_max_score,
                 started_at, completed_at)
            VALUES ($rid,$pv,$rt,$om,$ie,$is,$pc,$fc,$as,$am,$sa,$ca);
            """;
        cmd.Parameters.AddWithValue("$rid", run.RunId);
        cmd.Parameters.AddWithValue("$pv",  run.PromptVersion);
        cmd.Parameters.AddWithValue("$rt",  run.RunType);
        cmd.Parameters.AddWithValue("$om",  run.OllamaModel);
        cmd.Parameters.AddWithValue("$ie",  run.ImagesEvaluated);
        cmd.Parameters.AddWithValue("$is",  run.ImagesSkipped);
        cmd.Parameters.AddWithValue("$pc",  run.PassCount);
        cmd.Parameters.AddWithValue("$fc",  run.FailCount);
        cmd.Parameters.AddWithValue("$as",  run.AvgScore);
        cmd.Parameters.AddWithValue("$am",  run.AvgMaxScore);
        cmd.Parameters.AddWithValue("$sa",  run.StartedAt);
        cmd.Parameters.AddWithValue("$ca",  (object?)run.CompletedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRunStats(EvalRun run)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE eval_runs SET
                images_evaluated=$ie, images_skipped=$is, pass_count=$pc,
                fail_count=$fc, avg_score=$as, avg_max_score=$am, completed_at=$ca
            WHERE run_id=$rid;
            """;
        cmd.Parameters.AddWithValue("$ie",  run.ImagesEvaluated);
        cmd.Parameters.AddWithValue("$is",  run.ImagesSkipped);
        cmd.Parameters.AddWithValue("$pc",  run.PassCount);
        cmd.Parameters.AddWithValue("$fc",  run.FailCount);
        cmd.Parameters.AddWithValue("$as",  run.AvgScore);
        cmd.Parameters.AddWithValue("$am",  run.AvgMaxScore);
        cmd.Parameters.AddWithValue("$ca",  DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$rid", run.RunId);
        cmd.ExecuteNonQuery();
    }

    public List<EvalRun> GetRunsForPrompt(string promptVersion)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, prompt_version, run_type, ollama_model, images_evaluated,
                   images_skipped, pass_count, fail_count, avg_score, avg_max_score,
                   started_at, completed_at
            FROM eval_runs WHERE prompt_version=$pv ORDER BY started_at;
            """;
        cmd.Parameters.AddWithValue("$pv", promptVersion);
        return ReadRuns(cmd);
    }

    public List<EvalRun> GetAllRuns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, prompt_version, run_type, ollama_model, images_evaluated,
                   images_skipped, pass_count, fail_count, avg_score, avg_max_score,
                   started_at, completed_at
            FROM eval_runs ORDER BY started_at;
            """;
        return ReadRuns(cmd);
    }

    private static List<EvalRun> ReadRuns(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<EvalRun>();
        while (r.Read())
            list.Add(new EvalRun
            {
                RunId           = r.GetString(0),
                PromptVersion   = r.GetString(1),
                RunType         = r.GetString(2),
                OllamaModel     = r.GetString(3),
                ImagesEvaluated = r.GetInt32(4),
                ImagesSkipped   = r.GetInt32(5),
                PassCount       = r.GetInt32(6),
                FailCount       = r.GetInt32(7),
                AvgScore        = r.GetDouble(8),
                AvgMaxScore     = r.GetDouble(9),
                StartedAt       = r.GetString(10),
                CompletedAt     = r.IsDBNull(11) ? null : r.GetString(11)
            });
        return list;
    }

    // ── Eval results ───────────────────────────────────────────────────────────

    public void InsertResult(EvalResult result)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO eval_results
                (run_id, image_id, file_name, ollama_description,
                 gate_scores_json, total_score, max_score, passed, notes)
            VALUES ($rid,$iid,$fn,$od,$gs,$ts,$ms,$p,$n);
            """;
        cmd.Parameters.AddWithValue("$rid", result.RunId);
        cmd.Parameters.AddWithValue("$iid", result.ImageId);
        cmd.Parameters.AddWithValue("$fn",  result.FileName);
        cmd.Parameters.AddWithValue("$od",  (object?)result.OllamaDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gs",  JsonSerializer.Serialize(result.GateScores));
        cmd.Parameters.AddWithValue("$ts",  result.TotalScore);
        cmd.Parameters.AddWithValue("$ms",  result.MaxScore);
        cmd.Parameters.AddWithValue("$p",   result.Passed ? 1 : 0);
        cmd.Parameters.AddWithValue("$n",   (object?)result.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRichScores(string runId, int imageId, Dictionary<string,int> scores,
                                  int total, int max, bool passed, string? notes)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE eval_results SET
                gate_scores_json=$gs, total_score=$ts, max_score=$ms, passed=$p, notes=$n
            WHERE run_id=$rid AND image_id=$iid;
            """;
        cmd.Parameters.AddWithValue("$gs",  JsonSerializer.Serialize(scores));
        cmd.Parameters.AddWithValue("$ts",  total);
        cmd.Parameters.AddWithValue("$ms",  max);
        cmd.Parameters.AddWithValue("$p",   passed ? 1 : 0);
        cmd.Parameters.AddWithValue("$n",   (object?)notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rid", runId);
        cmd.Parameters.AddWithValue("$iid", imageId);
        cmd.ExecuteNonQuery();
    }

    public List<EvalResult> GetResultsForRun(string runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, run_id, image_id, file_name, ollama_description,
                   gate_scores_json, total_score, max_score, passed, notes
            FROM eval_results WHERE run_id=$rid ORDER BY image_id;
            """;
        cmd.Parameters.AddWithValue("$rid", runId);
        using var r = cmd.ExecuteReader();
        var list = new List<EvalResult>();
        while (r.Read())
            list.Add(new EvalResult
            {
                Id                = r.GetInt32(0),
                RunId             = r.GetString(1),
                ImageId           = r.GetInt32(2),
                FileName          = r.GetString(3),
                OllamaDescription = r.IsDBNull(4) ? null : r.GetString(4),
                GateScores        = JsonSerializer.Deserialize<Dictionary<string,int>>(r.GetString(5)) ?? [],
                TotalScore        = r.GetInt32(6),
                MaxScore          = r.GetInt32(7),
                Passed            = r.GetInt32(8) == 1,
                Notes             = r.IsDBNull(9) ? null : r.GetString(9)
            });
        return list;
    }

    // ── Gate pass rates for a run ──────────────────────────────────────────────

    public Dictionary<string, (int passed, int total)> GetGateRates(string runId)
    {
        var results  = GetResultsForRun(runId);
        var rates    = new Dictionary<string, (int, int)>();
        foreach (var r in results)
        {
            foreach (var (gate, score) in r.GateScores)
            {
                rates.TryGetValue(gate, out var cur);
                int passed = score > 0 ? cur.Item1 + 1 : cur.Item1;
                rates[gate] = (passed, cur.Item2 + 1);
            }
        }
        return rates;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

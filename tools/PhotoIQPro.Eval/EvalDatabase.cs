using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhotoIQPro.Eval;

/// <summary>
/// SQLite-backed store for all evaluation results.
/// Each row represents one model/photo/pass combination.
/// Gate 5 and the final total score are back-filled after all three passes complete.
/// </summary>
public sealed class EvalDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public EvalDatabase(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
    }

    /// <summary>
    /// Creates all tables if they do not already exist.
    /// Safe to call on every run — existing data is preserved.
    /// </summary>
    public void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();

        // Legacy table — preserved so historical runs remain readable.
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS evaluations (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                model_name    TEXT    NOT NULL,
                photo_file    TEXT    NOT NULL,
                pass_number   INTEGER NOT NULL,
                description   TEXT,
                gate_1_pass   INTEGER,
                gate_2_pass   INTEGER,
                gate_3_pass   INTEGER,
                gate_4_pass   INTEGER,
                gate_5_pass   INTEGER,
                gate_6_pass   INTEGER,
                total_score   INTEGER,
                evaluated_at  TEXT
            )
            """;
        cmd.ExecuteNonQuery();

        // Ground truth: one row per test photo; seeded from TestConfig on first run.
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS evaluation_set (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name       TEXT    NOT NULL UNIQUE,
                gate1_forbidden TEXT    NOT NULL DEFAULT '[]',
                gate2_forbidden TEXT    NOT NULL DEFAULT '[]',
                has_gesture     INTEGER NOT NULL DEFAULT 0,
                gate6_forbidden TEXT    NOT NULL DEFAULT '[]',
                notes           TEXT,
                created_at      TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        // Scored results: one row per model/photo/pass, grouped by run_id.
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS evaluation_results (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id        TEXT    NOT NULL,
                model_name    TEXT    NOT NULL,
                photo_file    TEXT    NOT NULL,
                pass_number   INTEGER NOT NULL,
                description   TEXT,
                gate_1_pass   INTEGER,
                gate_2_pass   INTEGER,
                gate_3_pass   INTEGER,
                gate_4_pass   INTEGER,
                gate_5_pass   INTEGER,
                gate_6_pass   INTEGER,
                total_score   INTEGER,
                evaluated_at  TEXT
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts each test case into evaluation_set using INSERT OR IGNORE so existing rows
    /// are never overwritten. Call once per run to ensure ground truth is persisted.
    /// </summary>
    public void SeedEvaluationSet(IEnumerable<PhotoTestCase> photos)
    {
        foreach (var p in photos)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO evaluation_set
                    (file_name, gate1_forbidden, gate2_forbidden, has_gesture, gate6_forbidden, created_at)
                VALUES
                    ($file, $g1, $g2, $gesture, $g6, $at)
                """;
            cmd.Parameters.AddWithValue("$file", p.FileName);
            cmd.Parameters.AddWithValue("$g1", JsonSerializer.Serialize(p.Gate1Forbidden));
            cmd.Parameters.AddWithValue("$g2", JsonSerializer.Serialize(p.Gate2Forbidden));
            cmd.Parameters.AddWithValue("$gesture", p.HasGesture ? 1 : 0);
            cmd.Parameters.AddWithValue("$g6", JsonSerializer.Serialize(p.Gate6ForbiddenMisclassifications));
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the test cases stored in evaluation_set, ordered by insertion order.
    /// Call after SeedEvaluationSet so the table is never empty.
    /// </summary>
    public PhotoTestCase[] GetTestCases()
    {
        var results = new List<PhotoTestCase>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT file_name, gate1_forbidden, gate2_forbidden, has_gesture, gate6_forbidden " +
            "FROM evaluation_set ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string fileName  = reader.GetString(0);
            string[] g1      = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
            string[] g2      = JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [];
            bool hasGesture  = reader.GetInt32(3) != 0;
            string[] g6      = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [];
            results.Add(new PhotoTestCase(fileName, g1, g2, hasGesture, g6));
        }
        return results.ToArray();
    }

    /// <summary>
    /// Inserts a single pass result (without Gate 5 or the final score — those come later).
    /// Returns the auto-generated row id so the caller can update the row after Gate 5 is known.
    /// </summary>
    public long InsertResult(
        string runId,
        string model,
        string photo,
        int pass,
        string description,
        bool g1, bool g2, bool g3, bool g4, bool g6,
        int score)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO evaluation_results
                (run_id, model_name, photo_file, pass_number, description,
                 gate_1_pass, gate_2_pass, gate_3_pass, gate_4_pass,
                 gate_5_pass, gate_6_pass, total_score, evaluated_at)
            VALUES
                ($runId, $model, $photo, $pass, $description,
                 $g1, $g2, $g3, $g4,
                 NULL, $g6, $score, $at);
            SELECT last_insert_rowid();
            """;

        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$photo", photo);
        cmd.Parameters.AddWithValue("$pass", pass);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$g1", g1 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g2", g2 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g3", g3 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g4", g4 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g6", g6 ? 1 : 0);
        cmd.Parameters.AddWithValue("$score", score);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));

        return (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("Insert returned no row id."));
    }

    /// <summary>
    /// Back-fills Gate 5 and the corrected total score once all 3 passes have been evaluated.
    /// </summary>
    public void UpdateGate5AndScore(long id, bool g5, int score)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE evaluation_results
               SET gate_5_pass = $g5,
                   total_score = $score
             WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$g5", g5 ? 1 : 0);
        cmd.Parameters.AddWithValue("$score", score);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Prints two summary tables to stdout:
    ///   1. Per-model aggregate (gate averages, average score, PASS/FAIL verdict).
    ///   2. Per-photo per-model scores.
    /// Only results for <paramref name="runId"/> are included.
    /// </summary>
    public void PrintSummary(string runId, IReadOnlyList<string> models, IReadOnlyList<string> photos)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  EVALUATION SUMMARY");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");

        // ── Table 1: per-model gate averages ──────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"  {"Model",-22} | {"G1",-4} | {"G2",-4} | {"G3",-4} | {"G4",-4} | {"G5",-4} | {"G6",-4} | {"Avg Score",-10} | Result");
        Console.WriteLine($"  {new string('-', 22)}-+-{new string('-', 4)}-+-{new string('-', 4)}-+-{new string('-', 4)}-+-{new string('-', 4)}-+-{new string('-', 4)}-+-{new string('-', 4)}-+-{new string('-', 10)}-+-------");

        foreach (string model in models)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    AVG(gate_1_pass) AS g1,
                    AVG(gate_2_pass) AS g2,
                    AVG(gate_3_pass) AS g3,
                    AVG(gate_4_pass) AS g4,
                    AVG(gate_5_pass) AS g5,
                    AVG(gate_6_pass) AS g6,
                    AVG(total_score) AS avg_score,
                    MIN(total_score) AS min_score
                FROM evaluation_results
                WHERE run_id = $runId AND model_name = $model
                """;
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$model", model);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                Console.WriteLine($"  {model,-22} | (no data)");
                continue;
            }

            double g1Avg  = reader.GetDouble(0);
            double g2Avg  = reader.GetDouble(1);
            double g3Avg  = reader.GetDouble(2);
            double g4Avg  = reader.GetDouble(3);
            double g5Avg  = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
            double g6Avg  = reader.GetDouble(5);
            double avgScore = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
            int    minScore = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);

            // PASS = every photo scored >= 6 AND each gate passed on average > 50%
            bool pass = minScore >= 6
                && g1Avg > 0.5 && g2Avg > 0.5 && g3Avg > 0.5
                && g4Avg > 0.5 && g5Avg > 0.5 && g6Avg > 0.5;

            string G(double v) => v > 0.5 ? "✓" : "✗";

            Console.WriteLine(
                $"  {model,-22} | {G(g1Avg),-4} | {G(g2Avg),-4} | {G(g3Avg),-4} | " +
                $"{G(g4Avg),-4} | {G(g5Avg),-4} | {G(g6Avg),-4} | " +
                $"{avgScore:F1}/9{"",-5} | {(pass ? "PASS" : "FAIL")}");
        }

        // ── Table 2: per-photo scores per model ───────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  Per-photo scores (average across 3 passes, threshold 6/9):");
        Console.WriteLine();

        // Build header row
        var header = $"  {"Photo",-16}";
        foreach (string model in models)
        {
            string short_name = model.Length > 14 ? model[..14] : model;
            header += $" | {short_name,-14}";
        }
        Console.WriteLine(header);
        Console.WriteLine($"  {new string('-', 16)}" + string.Concat(models.Select(_ => $"-+-{new string('-', 14)}")));

        foreach (string photo in photos)
        {
            string row = $"  {photo,-16}";
            foreach (string model in models)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT AVG(total_score)
                    FROM evaluation_results
                    WHERE run_id = $runId AND model_name = $model AND photo_file = $photo
                    """;
                cmd.Parameters.AddWithValue("$runId", runId);
                cmd.Parameters.AddWithValue("$model", model);
                cmd.Parameters.AddWithValue("$photo", photo);

                object? result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                {
                    row += $" | {"—",-14}";
                }
                else
                {
                    double avg = Convert.ToDouble(result);
                    string mark = avg >= 6.0 ? "✓" : "✗";
                    row += $" | {mark} {avg:F1}/9{"",-8}";
                }
            }
            Console.WriteLine(row);
        }

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Returns the run_id of the most recent run strictly before <paramref name="currentRunId"/>,
    /// or null if this is the first recorded run.
    /// </summary>
    public string? GetPreviousRunId(string currentRunId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT run_id FROM evaluation_results
            WHERE run_id < $runId
            ORDER BY run_id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$runId", currentRunId);
        object? result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>
    /// Returns per-photo per-model aggregated gate pass rates (averaged across all passes)
    /// for the given <paramref name="runId"/>.
    /// </summary>
    public List<PhotoModelSummary> GetRunSummary(string runId)
    {
        var rows = new List<PhotoModelSummary>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT photo_file, model_name,
                AVG(gate_1_pass), AVG(gate_2_pass), AVG(gate_3_pass),
                AVG(gate_4_pass), AVG(gate_5_pass), AVG(gate_6_pass),
                AVG(total_score)
            FROM evaluation_results
            WHERE run_id = $runId
            GROUP BY photo_file, model_name
            ORDER BY photo_file, model_name
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PhotoModelSummary(
                Photo:    reader.GetString(0),
                Model:    reader.GetString(1),
                G1:       reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                G2:       reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                G3:       reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                G4:       reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                G5:       reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                G6:       reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                AvgScore: reader.IsDBNull(8) ? 0 : reader.GetDouble(8)
            ));
        }
        return rows;
    }

    // ── Claude eval schema + queries ──────────────────────────────────────────

    public void EnsureClaudeEvalSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS claude_eval_results (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id           TEXT    NOT NULL,
                file_name        TEXT    NOT NULL,
                file_path        TEXT    NOT NULL,
                image_used       TEXT    NOT NULL,
                ai_description   TEXT    NOT NULL,
                g1_no_hallucin   INTEGER NOT NULL,
                g2_subject       INTEGER NOT NULL,
                g3_person_count  INTEGER,            -- NULL means no people (auto-pass)
                g4_setting       INTEGER NOT NULL,
                g5_key_object    INTEGER NOT NULL,
                total_score      INTEGER NOT NULL,
                claude_notes     TEXT,
                evaluated_at     TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void InsertClaudeEvalResult(string runId, LibraryPhoto photo, ClaudeEvalResult result)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO claude_eval_results
                (run_id, file_name, file_path, image_used, ai_description,
                 g1_no_hallucin, g2_subject, g3_person_count, g4_setting, g5_key_object,
                 total_score, claude_notes, evaluated_at)
            VALUES
                ($runId, $fileName, $filePath, $imageUsed, $aiDesc,
                 $g1, $g2, $g3, $g4, $g5,
                 $score, $notes, $at)
            """;
        cmd.Parameters.AddWithValue("$runId",     runId);
        cmd.Parameters.AddWithValue("$fileName",  photo.FileName);
        cmd.Parameters.AddWithValue("$filePath",  photo.FilePath);
        cmd.Parameters.AddWithValue("$imageUsed", photo.ImagePath);
        cmd.Parameters.AddWithValue("$aiDesc",    photo.AiDescription);
        cmd.Parameters.AddWithValue("$g1",        result.G1 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g2",        result.G2 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g3",        result.PersonCount.HasValue ? (result.G3 ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$g4",        result.G4 ? 1 : 0);
        cmd.Parameters.AddWithValue("$g5",        result.G5 ? 1 : 0);
        cmd.Parameters.AddWithValue("$score",     result.Score);
        cmd.Parameters.AddWithValue("$notes",     (object?)result.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at",        DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<ClaudeEvalRow> GetClaudeEvalResults(string runId)
    {
        var rows = new List<ClaudeEvalRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT file_name, ai_description,
                   g1_no_hallucin, g2_subject, g3_person_count, g4_setting, g5_key_object,
                   total_score, claude_notes
            FROM claude_eval_results
            WHERE run_id = $runId
            ORDER BY total_score DESC, file_name
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bool? g3 = reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1;
            rows.Add(new ClaudeEvalRow(
                FileName:        reader.GetString(0),
                AiDescription:   reader.GetString(1),
                G1:              reader.GetInt32(2) == 1,
                G2:              reader.GetInt32(3) == 1,
                G3:              g3 ?? true,
                PersonCountNull: g3 == null,
                G4:              reader.GetInt32(5) == 1,
                G5:              reader.GetInt32(6) == 1,
                Score:           reader.GetInt32(7),
                Notes:           reader.IsDBNull(8) ? "" : reader.GetString(8)
            ));
        }
        return rows;
    }

    public ClaudeGateRates GetClaudeGateRates(string runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                AVG(g1_no_hallucin),
                AVG(g2_subject),
                AVG(CASE WHEN g3_person_count IS NULL THEN 1.0 ELSE g3_person_count END),
                AVG(g4_setting),
                AVG(g5_key_object)
            FROM claude_eval_results
            WHERE run_id = $runId
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new ClaudeGateRates(0, 0, 0, 0, 0);
        return new ClaudeGateRates(
            G1: reader.IsDBNull(0) ? 0 : reader.GetDouble(0),
            G2: reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
            G3: reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
            G4: reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
            G5: reader.IsDBNull(4) ? 0 : reader.GetDouble(4)
        );
    }

    // ── Prompt optimization schema + queries ──────────────────────────────────

    /// <summary>
    /// Creates tables for tracking prompt optimization iterations.
    /// </summary>
    public void EnsurePromptOptSchema()
    {
        using var cmd = _conn.CreateCommand();

        // Track each optimization run and its results
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS prompt_opt_runs (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id           TEXT    NOT NULL UNIQUE,
                prompt           TEXT    NOT NULL,
                model            TEXT    NOT NULL,
                image_count      INTEGER NOT NULL,
                avg_similarity   REAL    NOT NULL,
                min_similarity   REAL    NOT NULL,
                max_similarity   REAL    NOT NULL,
                accepted         INTEGER NOT NULL DEFAULT 0,
                notes            TEXT,
                created_at       TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        // Per-image similarity scores for each run
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS prompt_opt_scores (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id           TEXT    NOT NULL,
                file_name        TEXT    NOT NULL,
                claude_desc      TEXT    NOT NULL,
                ollama_desc      TEXT    NOT NULL,
                similarity       REAL    NOT NULL,
                created_at       TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void InsertPromptOptRun(
        string runId,
        string prompt,
        string model,
        int imageCount,
        double avgSim,
        double minSim,
        double maxSim,
        bool accepted,
        string? notes = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO prompt_opt_runs
                (run_id, prompt, model, image_count, avg_similarity, min_similarity, max_similarity, accepted, notes, created_at)
            VALUES
                ($runId, $prompt, $model, $count, $avg, $min, $max, $accepted, $notes, $at)
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$prompt", prompt);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$count", imageCount);
        cmd.Parameters.AddWithValue("$avg", avgSim);
        cmd.Parameters.AddWithValue("$min", minSim);
        cmd.Parameters.AddWithValue("$max", maxSim);
        cmd.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void InsertPromptOptScore(
        string runId,
        string fileName,
        string claudeDesc,
        string ollamaDesc,
        double similarity)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO prompt_opt_scores
                (run_id, file_name, claude_desc, ollama_desc, similarity, created_at)
            VALUES
                ($runId, $file, $claude, $ollama, $sim, $at)
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$file", fileName);
        cmd.Parameters.AddWithValue("$claude", claudeDesc);
        cmd.Parameters.AddWithValue("$ollama", ollamaDesc);
        cmd.Parameters.AddWithValue("$sim", similarity);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public bool AcceptPromptOptRun(string runId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE prompt_opt_runs SET accepted = 1 WHERE run_id = $id";
        cmd.Parameters.AddWithValue("$id", runId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<(string FileName, string ClaudeDesc, string OllamaDesc, double Similarity)> GetPromptOptScores(string runId)
    {
        var results = new List<(string, string, string, double)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT file_name, claude_desc, ollama_desc, similarity
            FROM prompt_opt_scores
            WHERE run_id = $runId
            ORDER BY similarity
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3)
            ));
        }
        return results;
    }

    // ── Reference corpus schema + queries ─────────────────────────────────────

    /// <summary>
    /// Creates the reference_corpus table if it does not already exist.
    /// Each row is one image described by Claude Code — the gold-standard for prompt testing.
    /// </summary>
    public void EnsureCorpusSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS reference_corpus (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name        TEXT    NOT NULL UNIQUE,
                file_path        TEXT    NOT NULL,
                image_used       TEXT    NOT NULL,
                photo_year       INTEGER,
                claude_description TEXT  NOT NULL,
                generated_by     TEXT    NOT NULL DEFAULT 'claude-code',
                created_at       TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts one corpus entry. Silently ignores duplicates (UNIQUE on file_name).</summary>
    public bool InsertCorpusEntry(
        string fileName,
        string filePath,
        string imageUsed,
        int?   photoYear,
        string description,
        string generatedBy = "claude-code")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO reference_corpus
                (file_name, file_path, image_used, photo_year, claude_description, generated_by, created_at)
            VALUES
                ($name, $path, $image, $year, $desc, $by, $at)
            """;
        cmd.Parameters.AddWithValue("$name",  fileName);
        cmd.Parameters.AddWithValue("$path",  filePath);
        cmd.Parameters.AddWithValue("$image", imageUsed);
        cmd.Parameters.AddWithValue("$year",  (object?)photoYear ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc",  description);
        cmd.Parameters.AddWithValue("$by",    generatedBy);
        cmd.Parameters.AddWithValue("$at",    DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Returns the set of file_name values already in the corpus (for deduplication).</summary>
    public HashSet<string> GetCorpusFileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT file_name FROM reference_corpus";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>Returns total count and per-decade breakdown.</summary>
    public (int Total, List<(int Decade, int Count)> ByDecade) GetCorpusStats()
    {
        var byDecade = new List<(int, int)>();
        using var cmd = _conn.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM reference_corpus";
        int total = Convert.ToInt32(cmd.ExecuteScalar());

        cmd.CommandText =
            """
            SELECT (photo_year / 10) * 10 AS decade, COUNT(*) AS cnt
            FROM reference_corpus
            WHERE photo_year IS NOT NULL
            GROUP BY decade
            ORDER BY decade
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            byDecade.Add((reader.GetInt32(0), reader.GetInt32(1)));

        return (total, byDecade);
    }

    /// <summary>
    /// Returns a random sample of N corpus entries for use in prompt testing.
    /// </summary>
    public List<CorpusEntry> GetCorpusSample(int count)
    {
        var results = new List<CorpusEntry>(count);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT file_name, file_path, image_used, photo_year, claude_description
            FROM reference_corpus
            ORDER BY RANDOM()
            LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$count", count);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new CorpusEntry(
                FileName:    reader.GetString(0),
                FilePath:    reader.GetString(1),
                ImageUsed:   reader.GetString(2),
                PhotoYear:   reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Description: reader.GetString(4)
            ));
        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _conn.Dispose();
        _disposed = true;
    }
}

public record ClaudeEvalRow(
    string FileName,
    string AiDescription,
    bool   G1, bool G2, bool G3,
    bool   PersonCountNull,
    bool   G4, bool G5,
    int    Score,
    string Notes
);

public record ClaudeGateRates(double G1, double G2, double G3, double G4, double G5);

public record CorpusEntry(
    string  FileName,
    string  FilePath,
    string  ImageUsed,
    int?    PhotoYear,
    string  Description
);

/// <summary>Per-photo per-model aggregate for one run (averaged across 3 passes).</summary>
public record PhotoModelSummary(
    string Photo,
    string Model,
    double G1, double G2, double G3, double G4, double G5, double G6,
    double AvgScore
);

using System.Text.RegularExpressions;

namespace PhotoIQPro.PromptHarness;

/// <summary>
/// Scores an Ollama description against structured ground truth using the
/// gate definitions loaded from gates.json.
/// All scoring is text-based (keyword matching, regex) — no API calls.
/// </summary>
public static class GateEvaluator
{
    public static Dictionary<string, int> Score(
        string description,
        GroundTruth gt,
        GatesConfig config)
    {
        var scores = new Dictionary<string, int>();
        string lower = description.ToLowerInvariant();

        foreach (var gate in config.Gates)
        {
            scores[gate.Id] = gate.Type switch
            {
                "verified_absent" => ScoreVerifiedAbsent(lower, gt, gate),
                "keyword_match"   => ScoreKeywordMatch(lower, gt, gate),
                "person_count"    => ScorePersonCount(lower, gt, gate),
                "filler_opener"   => ScoreFillerOpener(description, gate),
                "word_count"      => ScoreWordCount(description, gate),
                _                 => 0
            };
        }

        return scores;
    }

    public static int TotalScore(Dictionary<string, int> scores) =>
        scores.Values.Sum();

    public static int MaxScore(GatesConfig config, GroundTruth gt)
    {
        int max = 0;
        foreach (var gate in config.Gates)
        {
            // G3 is N/A when no people — don't include its points in max
            if (gate.Type == "person_count" && gt.PersonCount < 0)
                continue;
            max += gate.Points;
        }
        return max;
    }

    // ── Gate implementations ───────────────────────────────────────────────────

    private static int ScoreVerifiedAbsent(string lower, GroundTruth gt, GateConfig gate)
    {
        int score = gate.Points;
        foreach (var term in gt.VerifiedAbsent)
        {
            if (ContainsTerm(lower, term))
                score = Math.Max(0, score - gate.PenaltyPerHit);
        }
        return score;
    }

    private static int ScoreKeywordMatch(string lower, GroundTruth gt, GateConfig gate)
    {
        string[] keywords = gate.SourceField switch
        {
            "subject"     => Tokenize(gt.Subject),
            "setting"     => Tokenize(gt.Setting),
            "key_objects" => gt.KeyObjects.SelectMany(Tokenize).Distinct().ToArray(),
            _             => []
        };

        if (keywords.Length == 0) return gate.Points; // no GT data → pass by default

        int matched = keywords.Count(k => lower.Contains(k));
        double pct  = (double)matched / keywords.Length;

        if (pct >= gate.FullCreditPct)    return gate.Points;
        if (pct >= gate.PartialCreditPct) return gate.Points / 2;
        return 0;
    }

    private static int ScorePersonCount(string lower, GroundTruth gt, GateConfig gate)
    {
        if (gt.PersonCount < 0) return gate.Points; // N/A — full credit

        // Zero people: description must not mention a person
        if (gt.PersonCount == 0)
        {
            bool mentionsPerson = PersonWords.Any(w => lower.Contains(w));
            return mentionsPerson ? 0 : gate.Points;
        }

        // Check for digit or written-out number
        string expected = gt.PersonCount.ToString();
        string written  = NumberWord(gt.PersonCount);

        if (lower.Contains(expected) || (!string.IsNullOrEmpty(written) && lower.Contains(written)))
            return gate.Points;

        return 0;
    }

    private static int ScoreFillerOpener(string description, GateConfig gate)
    {
        foreach (var prefix in gate.BannedPrefixes)
        {
            if (description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 0;
        }
        return gate.Points;
    }

    private static int ScoreWordCount(string description, GateConfig gate)
    {
        int count = description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return (count >= gate.MinWords && count <= gate.MaxWords) ? gate.Points : 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool ContainsTerm(string lower, string term) =>
        lower.Contains(term.ToLowerInvariant());

    private static string[] Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[\s,/\-]+")
             .Where(t => t.Length >= 3)  // skip short stop-words
             .Distinct()
             .ToArray();

    private static readonly string[] PersonWords =
        ["person", "man", "woman", "boy", "girl", "child", "people", "individual", "adult"];

    private static string NumberWord(int n) => n switch
    {
        1 => "one",  2 => "two",   3 => "three", 4 => "four",
        5 => "five", 6 => "six",   7 => "seven", 8 => "eight",
        9 => "nine", 10 => "ten",  _ => ""
    };
}

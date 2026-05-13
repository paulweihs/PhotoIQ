namespace PhotoWell.Eval;

/// <summary>
/// Stateless gate evaluation functions.
/// All string comparisons are case-insensitive.
/// Each gate returns true on PASS and false on FAIL.
/// </summary>
public static class GateEvaluator
{
    private static readonly StringComparison IC = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Gate 1 — Object hallucination check.
    /// PASS: none of the per-photo forbidden object words appear in the description.
    /// </summary>
    public static bool Gate1(string description, string[] forbidden) =>
        !forbidden.Any(word => description.Contains(word, IC));

    /// <summary>
    /// Gate 2 — Activity inversion check.
    /// PASS: none of the per-photo forbidden activity phrases appear in the description.
    /// </summary>
    public static bool Gate2(string description, string[] forbidden) =>
        !forbidden.Any(phrase => description.Contains(phrase, IC));

    /// <summary>
    /// Gate 3 — Filler/context tag check (global list from TestConfig).
    /// PASS: none of the global filler tags appear in the description without visual evidence.
    /// </summary>
    public static bool Gate3(string description) =>
        !TestConfig.FillerTags.Any(tag => description.Contains(tag, IC));

    /// <summary>
    /// Gate 4 — Relationship inference check (global list from TestConfig).
    /// PASS: none of the relationship or posture inference terms appear in the description.
    /// "seated" is included in TestConfig.RelationshipTerms per spec.
    /// </summary>
    public static bool Gate4(string description) =>
        !TestConfig.RelationshipTerms.Any(term => description.Contains(term, IC));

    /// <summary>
    /// Gate 5 — Determinism check across 3 passes.
    /// PASS: all three passes produced identical gate results for G1, G2, G3, G4, and G6.
    /// A model that consistently fails the same gates still passes Gate 5 —
    /// determinism is about consistency, not correctness.
    /// </summary>
    public static bool Gate5(IReadOnlyList<(bool g1, bool g2, bool g3, bool g4, bool g6)> threePassResults)
    {
        if (threePassResults.Count != 3)
            throw new ArgumentException("Gate 5 requires exactly 3 pass results.", nameof(threePassResults));

        var first = threePassResults[0];
        return threePassResults.All(r =>
            r.g1 == first.g1 &&
            r.g2 == first.g2 &&
            r.g3 == first.g3 &&
            r.g4 == first.g4 &&
            r.g6 == first.g6);
    }

    /// <summary>
    /// Gate 6 — Gesture misclassification check.
    /// If <paramref name="hasGesture"/> is false: always PASS (nothing to evaluate).
    /// If <paramref name="hasGesture"/> is true: PASS only if none of the forbidden
    /// misclassification words appear in the description.
    /// </summary>
    public static bool Gate6(string description, bool hasGesture, string[] forbidden)
    {
        if (!hasGesture) return true;
        return !forbidden.Any(word => description.Contains(word, IC));
    }

    /// <summary>
    /// Computes the total score for a single photo/pass result.
    /// G1=2, G2=2, G3=1, G4=1, G5=2, G6=1 — max 9.
    /// </summary>
    public static int Score(bool g1, bool g2, bool g3, bool g4, bool g5, bool g6) =>
        (g1 ? 2 : 0) +
        (g2 ? 2 : 0) +
        (g3 ? 1 : 0) +
        (g4 ? 1 : 0) +
        (g5 ? 2 : 0) +
        (g6 ? 1 : 0);
}

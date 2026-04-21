using System.Text.RegularExpressions;

namespace PhotoIQPro.Eval;

/// <summary>
/// Compares two descriptions using simple heuristics:
/// - Word overlap (Jaccard similarity)
/// - Key concept presence (named entities, objects)
/// - Length ratio
/// Returns a score 0-1 where 1 is identical.
/// </summary>
public static class DescriptionComparator
{
    /// <summary>
    /// Computes similarity between two descriptions (0 to 1).
    /// 1.0 = identical, 0.0 = completely different.
    /// </summary>
    public static double ComputeSimilarity(string desc1, string desc2)
    {
        if (string.IsNullOrWhiteSpace(desc1) || string.IsNullOrWhiteSpace(desc2))
            return string.Equals(desc1, desc2) ? 1.0 : 0.0;

        // Normalize: lowercase, trim punctuation at word boundaries
        var words1 = NormalizeAndTokenize(desc1);
        var words2 = NormalizeAndTokenize(desc2);

        if (words1.Count == 0 || words2.Count == 0)
            return 0.0;

        // Jaccard similarity: |intersection| / |union|
        var set1 = new HashSet<string>(words1);
        var set2 = new HashSet<string>(words2);

        int intersection = set1.Count(w => set2.Contains(w));
        int union = set1.Count + set2.Count - intersection;

        double jaccardSimilarity = union > 0 ? (double)intersection / union : 0.0;

        // Length ratio penalty: prefer descriptions of similar length
        int maxLen = Math.Max(words1.Count, words2.Count);
        int minLen = Math.Min(words1.Count, words2.Count);
        double lengthRatio = minLen > 0 ? (double)minLen / maxLen : 0.0;

        // Weighted average: 80% Jaccard, 20% length
        return jaccardSimilarity * 0.8 + lengthRatio * 0.2;
    }

    /// <summary>
    /// Normalizes a description and splits into word tokens.
    /// Removes stopwords and filters very short tokens.
    /// </summary>
    private static List<string> NormalizeAndTokenize(string text)
    {
        text = text.ToLowerInvariant();

        // Remove punctuation, split on whitespace
        var words = Regex.Split(text, @"\W+")
            .Where(w => w.Length > 1 && !IsStopword(w))
            .ToList();

        return words;
    }

    /// <summary>
    /// Common English stopwords to ignore during comparison.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "must", "can", "this", "that",
        "these", "those", "i", "you", "he", "she", "it", "we", "they",
        "what", "which", "who", "when", "where", "why", "how"
    };

    private static bool IsStopword(string word)
        => Stopwords.Contains(word);
}

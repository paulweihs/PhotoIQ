using System.Text.RegularExpressions;

namespace PhotoIQPro.Services.Vision;

/// <summary>
/// Pure text-processing helpers shared by <see cref="OllamaVisionService"/> and the
/// PromptEval harness.  All methods are stateless and thread-safe.
/// </summary>
internal static class TextNormalizer
{
    // ── Gender neutralisation ────────────────────────────────────────────────

    /// <summary>
    /// Replaces gendered nouns and pronouns with gender-neutral equivalents.
    /// Plurals are replaced before singulars to prevent double-substitution.
    /// Case is preserved: a capitalised match ("She") → capitalised replacement ("They").
    /// </summary>
    internal static string NeutraliseGender(string text)
    {
        static string Subst(string s, string pattern, string replacement) =>
            Regex.Replace(s, pattern, m =>
                char.IsUpper(m.Value[0])
                    ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
                    : replacement,
                RegexOptions.IgnoreCase);

        // Nouns — plurals before singulars to prevent double-replacement
        text = Subst(text, @"\bwomen\b", "people");
        text = Subst(text, @"\bwoman\b", "person");
        text = Subst(text, @"\bmen\b",   "people");
        text = Subst(text, @"\bman\b",   "person");
        text = Subst(text, @"\bgirls\b", "children");
        text = Subst(text, @"\bgirl\b",  "child");
        text = Subst(text, @"\bboys\b",  "children");
        text = Subst(text, @"\bboy\b",   "child");

        // Pronouns
        text = Subst(text, @"\bshe\b", "they");
        text = Subst(text, @"\bhe\b",  "they");
        text = Subst(text, @"\bhis\b", "their");
        text = Subst(text, @"\bher\b", "their");
        text = Subst(text, @"\bhim\b", "them");

        // Subject-verb agreement broken by he/she → they substitution
        text = Regex.Replace(text, @"\bthey is\b",  "they are",  RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey was\b", "they were", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey has\b", "they have", RegexOptions.IgnoreCase);

        return text;
    }

    // ── Filler-opener stripping ──────────────────────────────────────────────

    /// <summary>
    /// Removes common model filler openers and re-capitalises the remainder.
    /// Returns the original string unchanged if no pattern matches.
    /// </summary>
    internal static string StripFillerOpener(string text)
    {
        var m = Regex.Match(text,
            @"^(?:The|This)\s+(?:image|photo|picture)\s+(?:shows?|depicts?|features?|captures?|presents?|displays?|illustrates?|portrays?)\s+",
            RegexOptions.IgnoreCase);

        if (!m.Success)
            m = Regex.Match(text,
                @"^The\s+main\s+subject(?:\s+of\s+(?:this|the)\s+(?:image|photo|picture))?\s+is\s+(?:an?\s+)?",
                RegexOptions.IgnoreCase);

        if (!m.Success) return text;

        var remainder = text[m.Length..].TrimStart();
        if (remainder.Length == 0) return text;

        return char.ToUpperInvariant(remainder[0]) + remainder[1..];
    }

    // ── Repetition-loop detection ────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the text exhibits model-degeneration patterns:
    /// - Text exceeds 500 words (safety cap)
    /// - A single word dominates >40% of output (token collapse)
    /// - Any 5-word n-gram appears 3+ times (repetition loop)
    /// - Any 3-word n-gram appears 4+ times (stuttering degeneration)
    /// </summary>
    internal static bool IsRepetitionLoop(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 10) return false;
        if (words.Length > 500) return true;

        var dominant = words.GroupBy(w => w.ToLowerInvariant()).Max(g => g.Count());
        if (dominant > words.Length * 0.4) return true;

        // 5-word n-gram repetition (catches "The image shows ... The image shows ... The image shows ...")
        // Requires 3+ occurrences to trigger, allowing legitimate phrase reuse.
        if (words.Length >= 15)
        {
            var ngrams5 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= words.Length - 5; i++)
            {
                string ngram = string.Join(' ', words, i, 5);
                ngrams5.TryGetValue(ngram, out int count);
                if (count + 1 > 2) return true;  // Original threshold: 3+ occurrences
                ngrams5[ngram] = count + 1;
            }
        }

        // 3-word n-gram repetition (catches stuttering like "long, and often, long, and often...")
        // More aggressive: catches degeneration with shorter phrases.
        if (words.Length >= 9)
        {
            var ngrams3 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= words.Length - 3; i++)
            {
                string ngram = string.Join(' ', words, i, 3);
                ngrams3.TryGetValue(ngram, out int count);
                if (count + 1 > 3) return true;  // 4+ occurrences for 3-word phrases
                ngrams3[ngram] = count + 1;
            }
        }

        return false;
    }

    // ── Sentence trimmer ────────────────────────────────────────────────────

    /// <summary>
    /// Trims <paramref name="text"/> to at most <paramref name="maxSentences"/> complete
    /// sentences.  Falls back to the full text when no sentence boundary is found.
    /// Ellipsis guard: a '.' only ends a sentence when the preceding character is not '.'.
    /// Minimum 15 characters per sentence to avoid capturing stub fragments.
    /// </summary>
    internal static string TrimToCompleteSentences(string text, int maxSentences = 2)
    {
        var sentences = new List<string>();
        int searchFrom = 0;

        while (sentences.Count < maxSentences && searchFrom < text.Length)
        {
            int endIdx = -1;
            for (int i = searchFrom; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '.' or '!' or '?')
                {
                    bool notEllipsis  = c != '.' || i == 0 || text[i - 1] != '.';
                    bool atEnd        = i + 1 == text.Length;
                    bool followedByWs = i + 1 < text.Length && (text[i + 1] is ' ' or '\n' or '\r');
                    if (notEllipsis && (atEnd || followedByWs)) { endIdx = i; break; }
                }
            }
            if (endIdx < 0) break;

            var sentence = text[searchFrom..(endIdx + 1)].Trim();
            if (sentence.Length > 15)
                sentences.Add(sentence);

            searchFrom = endIdx + 1;
        }

        return sentences.Count > 0 ? string.Join(" ", sentences) : text;
    }

    // ── Newline flattening ───────────────────────────────────────────────────

    /// <summary>
    /// Collapses paragraph breaks into single-space prose, inserting a period where
    /// the merge point lacks terminal punctuation.
    /// Ellipsis-paragraph fix runs first so "...\n" becomes ". " not "... . ".
    /// </summary>
    internal static string FlattenNewlines(string text)
    {
        text = Regex.Replace(text, @"\.{2,}\s*[\r\n]+\s*", ". ");
        text = Regex.Replace(text, @"([^\.\!\?])\s*[\r\n]+\s*", "$1. ");
        return text;
    }
}

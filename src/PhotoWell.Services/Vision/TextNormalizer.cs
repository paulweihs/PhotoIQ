using System.Text.RegularExpressions;

namespace PhotoWell.Services.Vision;

/// <summary>
/// Pure text-processing helpers shared by <see cref="OllamaVisionService"/> and the
/// PromptEval harness.  All methods are stateless and thread-safe.
/// </summary>
internal static class TextNormalizer
{
    // ── Gender neutralisation ────────────────────────────────────────────────

    /// <summary>
    /// Collapses compound gendered phrases (like "a man and a woman") into natural
    /// plurals (like "two people") before individual word substitutions occur.
    /// Prevents unnatural outputs like "A person and a person".
    /// </summary>
    private static string CollapsePersonPhrases(string text)
    {
        // "a man and a woman", "a woman and a man", "a man and a man", "a woman and a woman" → "two people"
        text = Regex.Replace(text,
            @"\b(?:a|an)\s+(?:man|woman|person)\s+and\s+(?:a|an)\s+(?:man|woman|person)\b",
            "two people",
            RegexOptions.IgnoreCase);

        // "a man, a woman, and a [man|woman]" etc. → "three people"
        // Matches 3+ gendered people connected by commas and "and"
        text = Regex.Replace(text,
            @"\b(?:a|an)\s+(?:man|woman|person)(?:\s*,\s*(?:a|an)\s+(?:man|woman|person))+\s*,?\s+and\s+(?:a|an)\s+(?:man|woman|person)\b",
            "three people",
            RegexOptions.IgnoreCase);

        return text;
    }

    // ── Shared substitution helper ───────────────────────────────────────────

    /// <summary>
    /// Replaces every regex match with <paramref name="replacement"/>, preserving the
    /// capitalisation of the first character of the original match.
    /// Guards against empty match values and empty replacements.
    /// </summary>
    private static string Subst(string s, string pattern, string replacement) =>
        Regex.Replace(s, pattern, m =>
        {
            if (string.IsNullOrEmpty(m.Value) || string.IsNullOrEmpty(replacement))
                return replacement ?? string.Empty;
            return char.IsUpper(m.Value[0])
                ? char.ToUpperInvariant(replacement[0]) + (replacement.Length > 1 ? replacement[1..] : string.Empty)
                : replacement;
        }, RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces gendered nouns and pronouns with gender-neutral equivalents.
    /// Plurals are replaced before singulars to prevent double-substitution.
    /// Case is preserved: a capitalised match ("She") → capitalised replacement ("They").
    /// </summary>
    /// <summary>
    /// Resolves "likely female/male [based on ...]" phrases using sentence context:
    /// - If the sentence already contains an explicit gendered noun (woman/man/girl/boy),
    ///   the phrase is stripped entirely — it is redundant information.
    /// - Otherwise, child context → "likely a girl/boy", adult context → "likely a woman/man".
    /// Must run before noun substitutions so original gendered words are still present.
    /// The "based on ..." qualifier is always dropped — it adds hedging without useful content.
    /// </summary>
    private static string NormaliseLikelyGender(string text)
    {
        var childWords    = new Regex(@"\b(?:child|children|kid|kids|toddler|infant|baby|young\s+(?:child|girl|boy|person))\b", RegexOptions.IgnoreCase);
        // Explicit gendered nouns — when already present, "likely female/male" is redundant.
        var explicitNouns = new Regex(@"\b(?:woman|women|man|men|girl|girls|boy|boys)\b", RegexOptions.IgnoreCase);

        return Regex.Replace(text,
            @"\blikely\s+(female|male)(?:\s+based\s+on\b[^,;.!?)]*)?",
            m =>
            {
                bool isFemale = m.Groups[1].Value.Equals("female", StringComparison.OrdinalIgnoreCase);

                // Look back to the start of the current sentence.
                int sentenceStart = text.LastIndexOfAny(['.', '!', '?'], Math.Max(0, m.Index - 1));
                if (sentenceStart < 0) sentenceStart = 0;
                var sentencePrefix = text[sentenceStart..m.Index];

                // If the sentence already names the gender explicitly, drop the phrase entirely.
                if (explicitNouns.IsMatch(sentencePrefix))
                    return "";

                bool isChildContext = childWords.IsMatch(sentencePrefix);
                string replacement = (isFemale, isChildContext) switch
                {
                    (true,  true)  => "likely a girl",
                    (true,  false) => "likely a woman",
                    (false, true)  => "likely a boy",
                    (false, false) => "likely a man",
                };

                return char.IsUpper(m.Value[0])
                    ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
                    : replacement;
            },
            RegexOptions.IgnoreCase);
    }

    internal static string NeutraliseGender(string text)
    {
        // Normalise "likely female/male" before noun substitutions so context words
        // (child, girl, boy, man, woman) are still in their original form.
        text = NormaliseLikelyGender(text);

        // First, collapse compound gendered phrases to prevent "a person and a person" outputs
        text = CollapsePersonPhrases(text);

        // Possessives — plain neutral replacement only; inserting "who appears to be a man's"
        // is ungrammatical, so these are handled separately before the main noun pass.
        text = Subst(text, @"\bwoman's\b", "person's");
        text = Subst(text, @"\bman's\b",   "person's");
        text = Subst(text, @"\bgirl's\b",  "child's");
        text = Subst(text, @"\bboy's\b",   "child's");

        // Nouns — plurals before singulars to prevent double-replacement.
        // A trailing comma is appended after the hint and stripped before terminal
        // punctuation below, producing: "person, who appears to be a woman, walking…"
        text = Subst(text, @"\bwomen\b", "people, who appear to be women,");
        text = Subst(text, @"\bwoman\b", "person, who appears to be a woman,");
        text = Subst(text, @"\bmen\b",   "people, who appear to be men,");
        text = Subst(text, @"\bman\b",   "person, who appears to be a man,");
        text = Subst(text, @"\bgirls\b", "children, who appear to be girls,");
        text = Subst(text, @"\bgirl\b",  "child, who appears to be a girl,");
        text = Subst(text, @"\bboys\b",  "children, who appear to be boys,");
        text = Subst(text, @"\bboy\b",   "child, who appears to be a boy,");

        // Collapse double commas produced when the original text had a comma immediately
        // after a gendered noun that was replaced with a comma-terminated hint phrase.
        // e.g. "a boy, walking" → "a child, who appears to be a boy,, walking" → fix here.
        text = Regex.Replace(text, @",\s*,", ",");

        // Strip trailing comma immediately before terminal punctuation or end-of-string.
        text = Regex.Replace(text, @",(\s*[.!?])", "$1");
        text = Regex.Replace(text, @",\s*$", "");

        // Pronouns
        text = Subst(text, @"\bshe\b", "they");
        text = Subst(text, @"\bhe\b",  "they");
        text = Subst(text, @"\bhis\b", "their");
        text = Subst(text, @"\bher\b", "their");
        text = Subst(text, @"\bhim\b", "them");

        // Subject-verb agreement broken by he/she → they substitution.
        // Third-person singular verbs must become plural after the pronoun swap.
        text = Regex.Replace(text, @"\bthey is\b",    "they are",  RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey was\b",   "they were", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey has\b",   "they have", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey seems\b", "they seem", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey looks\b", "they look", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey sits?\b", "they sit",  RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey stands?\b","they stand",RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey walks?\b", "they walk", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey wears?\b", "they wear", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey holds?\b", "they hold", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey smiles?\b","they smile",RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bthey appears?\b","they appear",RegexOptions.IgnoreCase);

        // Compound-predicate agreement: "they [verb] ... and is [verb]" → "and are [verb]"
        // Handles: "they have blonde hair and is wearing a shirt" → "...and are wearing"
        // The lookbehind matches "they" anywhere in the same sentence (no period before "and is").
        text = Regex.Replace(text, @"(?<=\bthey\b[^.]{0,300})\band\s+is\b", "and are", RegexOptions.IgnoreCase);

        // Re-capitalise "they/their/them" when it follows a sentence-ending period+space,
        // since the pronoun substitution can produce mid-sentence capitals or post-period lowercases.
        text = Regex.Replace(text, @"(?<=\.\s+)they\b", "They");
        text = Regex.Replace(text, @"(?<=\.\s+)their\b", "Their");
        text = Regex.Replace(text, @"(?<=\.\s+)them\b", "Them");

        // Post-substitution cleanup: catch "a person and a person" patterns that slipped through
        text = Regex.Replace(text,
            @"\b(?:a|an)\s+person\s+and\s+(?:a|an)\s+person\b",
            "two people",
            RegexOptions.IgnoreCase);

        return text;
    }

    /// <summary>
    /// Enforces gender neutrality by replacing possessive/objective forms and additional gendered terms.
    /// Complements NeutraliseGender with more aggressive pattern matching for edge cases.
    /// Handles: hers, his (possessive), hers (objective), theirs, actress/actor, waiter/waitress.
    /// </summary>
    internal static string EnforceGenderNeutrality(string text)
    {
        // Possessive forms (hers, his as pronouns)
        text = Subst(text, @"\bhers\b", "theirs");
        text = Subst(text, @"\bhis\b", "theirs");

        // Occupational/role gendering: actress/actor → performer, waiter/waitress → server
        text = Subst(text, @"\bactresses\b", "performers");
        text = Subst(text, @"\bactress\b", "performer");
        text = Subst(text, @"\bactors\b", "performers");
        text = Subst(text, @"\bactor\b", "performer");
        text = Subst(text, @"\bwaitresses\b", "servers");
        text = Subst(text, @"\bwaitress\b", "server");
        text = Subst(text, @"\bwaiters\b", "servers");
        text = Subst(text, @"\bwaiter\b", "server");

        // Additional pronouns that might slip through initial pass
        text = Subst(text, @"\bmother\b", "parent");
        text = Subst(text, @"\bfather\b", "parent");
        text = Subst(text, @"\bsister\b", "sibling");
        text = Subst(text, @"\bbrother\b", "sibling");

        return text;
    }

    // ── Filler-opener stripping ──────────────────────────────────────────────

    // Location qualifier: "in the foreground", "in the background", "in the center", etc.
    // Also "of the image", "of the photo" — covers all positions the model might insert.
    private const string SubjectLocation =
        @"(?:\s+(?:" +
            @"in\s+(?:the\s+)?(?:foreground|background|center|centre|middle|frame|image|photo|picture|scene)" +
            @"|of\s+(?:this|the)\s+(?:image|photo|picture|photograph|scene)" +
        @"))?";

    // Verb phrase: all forms the model uses after a subject noun phrase.
    // We do NOT strip the following article — "appears to be a dog" strips to "a dog",
    // which then capitalises to "A dog", keeping natural grammar.
    private const string SubjectVerb =
        @"\s+(?:appears?\s+to\s+be|consists?\s+of|is|are|was|were|include[sd]?|feature[sd]?)";

    /// <summary>
    /// Removes common model filler openers and re-capitalises the remainder.
    /// The article that follows the verb ("a", "an") is intentionally kept so the
    /// remainder reads naturally: "appears to be a dog" → "A dog", not "Dog".
    /// Returns the original string unchanged if no pattern matches.
    /// </summary>
    internal static string StripFillerOpener(string text)
    {
        // 1. "The/This image/photo/picture shows/depicts/features/..."
        var m = Regex.Match(text,
            @"^(?:The|This)\s+(?:image|photo|picture|photograph)\s+" +
            @"(?:shows?|depicts?|features?|captures?|presents?|displays?|illustrates?|portrays?)\s+",
            RegexOptions.IgnoreCase);

        // 2. "The [main|primary|central|dominant] subject [location] [verb phrase] [a/an]"
        //    e.g. "The main subject in the foreground appears to be a dog"  → "Dog."
        //         "The main subject is an elderly person"                   → "Elderly person."
        //    The optional article ("a/an") is consumed so the remainder starts at the noun.
        if (!m.Success)
            m = Regex.Match(text,
                @"^The\s+(?:main|primary|central|dominant|sole|only)\s+subject" +
                SubjectLocation + SubjectVerb + @"\s+(?:an?\s+)?",
                RegexOptions.IgnoreCase);

        // 3. "The setting [of the photo] [is/appears to be/seems to be]"
        if (!m.Success)
            m = Regex.Match(text,
                @"^The\s+setting(?:\s+of\s+(?:this|the)\s+(?:photo(?:graph)?|image|picture))?" +
                @"\s+(?:is|appears\s+to\s+be|seems\s+to\s+be)\s+",
                RegexOptions.IgnoreCase);

        if (!m.Success) return text;

        var remainder = text[m.Length..].TrimStart();
        if (remainder.Length == 0) return text;

        return char.ToUpperInvariant(remainder[0]) + remainder[1..];
    }

    // ── Verbose filler phrase stripping ─────────────────────────────────────

    /// <summary>
    /// Removes meta-commentary phrases the model inserts mid-sentence that waste words
    /// without adding searchable content.  Applied after filler-opener stripping and
    /// before gender neutralisation.
    /// </summary>
    internal static string StripVerboseFillers(string text)
    {
        // Strip "Sentence N:" labels that models emit when the prompt uses that format
        // for instructions (e.g. "Sentence 1: ...", "Sentence 2: ...", "Sentence 3: ...").
        text = Regex.Replace(text,
            @"\bSentence\s+\d+\s*:\s*",
            "",
            RegexOptions.IgnoreCase);

        // "X is the most prominent subject in this photo" → "X"
        text = Regex.Replace(text,
            @"\bis the (?:single )?most prominent subject (?:in|of) (?:this |the )?photo(?:graph)?\b[,.]?\s*",
            ". ",
            RegexOptions.IgnoreCase);

        // "X is the dominant subject" / "X, the dominant subject"
        text = Regex.Replace(text,
            @"\bis the dominant subject\b[,.]?\s*",
            ". ",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text,
            @",\s*the dominant subject\b",
            "",
            RegexOptions.IgnoreCase);

        // ", the most prominent subject" (appositive form)
        text = Regex.Replace(text,
            @",\s*the (?:single )?most prominent subject\b",
            "",
            RegexOptions.IgnoreCase);

        // Mid-sentence "The [main|primary|...] subject [location] [verb phrase] X" → "X" (capitalised).
        // Matches after a sentence boundary so only mid-sentence occurrences are caught here
        // (the opening occurrence is handled by StripFillerOpener which runs first).
        // We capture the first char of the remainder and uppercase it; the rest of the match
        // (the whole filler phrase) is consumed and dropped.
        text = Regex.Replace(text,
            @"(?<=[.!?]\s{1,2})The\s+(?:main|primary|central|dominant|sole|only)\s+subject" +
            SubjectLocation + SubjectVerb + @"\s+([A-Za-z])",
            m => char.ToUpperInvariant(m.Groups[1].Value[0]).ToString(),
            RegexOptions.IgnoreCase);

        // "The setting is/appears to be X" / "The setting of the photo is X" → "X" (capitalised).
        text = Regex.Replace(text,
            @"(?<=[.!?]\s{0,2}|^)The\s+setting(?:\s+of\s+(?:this|the)\s+(?:photo(?:graph)?|image|picture))?\s+(?:is|appears\s+to\s+be|seems\s+to\s+be)\s+([A-Za-z])",
            m => char.ToUpperInvariant(m.Groups[1].Value[0]).ToString(),
            RegexOptions.IgnoreCase);

        // "in this photo" / "in this image" / "in this photograph" as a trailing clause
        text = Regex.Replace(text,
            @"\bin this (?:photo(?:graph)?|image|picture)\b",
            "",
            RegexOptions.IgnoreCase);

        // "of this photo" / "of this image" (possessive variant)
        text = Regex.Replace(text,
            @"\bof this (?:photo(?:graph)?|image|picture)\b",
            "",
            RegexOptions.IgnoreCase);

        // Collapse double spaces and fix ". ." artifacts from the replacements above.
        text = Regex.Replace(text, @"\.\s*\.", ".");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        return text;
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

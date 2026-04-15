using Xunit;
using PhotoIQPro.Services.Vision;

namespace PhotoIQPro.Tests;

// ── NeutraliseGender ─────────────────────────────────────────────────────────

public class NeutraliseGenderTests
{
    // Nouns — singular
    [Theory]
    [InlineData("A woman walks by.",   "A person walks by.")]
    [InlineData("A man waves hello.",  "A person waves hello.")]
    [InlineData("A girl is playing.",  "A child is playing.")]
    [InlineData("A boy holds a kite.", "A child holds a kite.")]
    public void SingularNouns_AreNeutralised(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.NeutraliseGender(input));

    // Nouns — plural (must fire before singular to avoid double-replace)
    [Theory]
    [InlineData("Two women are talking.",   "Two people are talking.")]
    [InlineData("Several men stand there.", "Several people stand there.")]
    [InlineData("Three girls are laughing.", "Three children are laughing.")]
    [InlineData("Four boys run around.",     "Four children run around.")]
    public void PluralNouns_AreNeutralised(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.NeutraliseGender(input));

    // Pronouns — note: "She is" triggers the verb-agreement fix too, so the full pipeline
    // is "She is" → "They is" (Subst) → "they are" (verb fix). Use a verb not in the fix
    // list ("smiles") to isolate the pronoun step.
    [Theory]
    [InlineData("She smiles warmly.",    "They smiles warmly.")]
    [InlineData("He looks at the sky.",  "They looks at the sky.")]
    [InlineData("His hat is red.",       "Their hat is red.")]
    [InlineData("Her coat is blue.",     "Their coat is blue.")]
    [InlineData("I gave him the book.",  "I gave them the book.")]
    public void Pronouns_AreNeutralised(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.NeutraliseGender(input));

    // Verb agreement fix
    [Theory]
    [InlineData("They is happy.",  "they are happy.")]
    [InlineData("They was there.", "they were there.")]
    [InlineData("They has a cat.", "they have a cat.")]
    public void VerbAgreement_IsFixed_AfterPronounSubstitution(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.NeutraliseGender(input), ignoreCase: true);

    // Full pipeline — pronoun + verb
    // Note: the verb-agreement fix uses a literal lowercase replacement so the capital
    // produced by the pronoun step ("She"→"They") is subsequently lowercased ("they are").
    [Fact]
    public void She_Is_BecomesTheyAre()
    {
        var result = TextNormalizer.NeutraliseGender("She is wearing a red dress.");
        Assert.Equal("they are wearing a red dress.", result);
    }

    [Fact]
    public void He_Was_BecomesTheyWere()
    {
        var result = TextNormalizer.NeutraliseGender("He was standing near the door.");
        Assert.Equal("they were standing near the door.", result);
    }

    // Case preservation — tested on a sentence whose verb does NOT trigger the verb-fix,
    // so the capital from the pronoun substitution survives to the output.
    [Fact]
    public void CapitalisedMatch_ProducesCapitalisedReplacement()
    {
        // "She" → "They" (capital preserved); "walks" is not in the verb-fix list
        var result = TextNormalizer.NeutraliseGender("She walks to the park.");
        Assert.StartsWith("They", result);
    }

    [Fact]
    public void LowercaseMatch_ProducesLowercaseReplacement()
    {
        var result = TextNormalizer.NeutraliseGender("I saw her yesterday.");
        Assert.Contains("their", result);
    }

    // Word-boundary enforcement (no partial matches)
    [Fact]
    public void WordBoundary_ManInManagement_NotReplaced()
    {
        var result = TextNormalizer.NeutraliseGender("The management team works hard.");
        Assert.DoesNotContain("personagement", result);
        Assert.Contains("management", result);
    }

    [Fact]
    public void WordBoundary_HerInHere_NotReplaced()
    {
        var result = TextNormalizer.NeutraliseGender("The answer is here.");
        Assert.Contains("here", result);
        Assert.DoesNotContain("theire", result);
    }

    [Fact]
    public void WordBoundary_HisInThis_NotReplaced()
    {
        var result = TextNormalizer.NeutraliseGender("This is fine.");
        Assert.Contains("This", result);
        Assert.DoesNotContain("Theirs", result);
    }

    // Plurals-before-singulars: "men" must not become "personple"
    [Fact]
    public void MenNotDoubleReplaced()
    {
        var result = TextNormalizer.NeutraliseGender("The men walked in.");
        Assert.Equal("The people walked in.", result);
    }

    [Fact]
    public void WomenNotDoubleReplaced()
    {
        var result = TextNormalizer.NeutraliseGender("The women cheered.");
        Assert.Equal("The people cheered.", result);
    }

    // No-op on already neutral text
    [Fact]
    public void NeutralText_IsUnchanged()
    {
        const string neutral = "A person is sitting on a bench holding a coffee cup.";
        Assert.Equal(neutral, TextNormalizer.NeutraliseGender(neutral));
    }
}

// ── StripFillerOpener ────────────────────────────────────────────────────────

public class StripFillerOpenerTests
{
    // Pattern 1 verb variants
    [Theory]
    [InlineData("The image shows a dog running.",       "A dog running.")]
    [InlineData("The photo depicts a sunset.",          "A sunset.")]
    [InlineData("This picture features two adults.",    "Two adults.")]
    [InlineData("The image captures a busy street.",    "A busy street.")]
    [InlineData("This photo presents a white cat.",     "A white cat.")]
    [InlineData("The image displays a mountain view.",  "A mountain view.")]
    [InlineData("The image illustrates a workflow.",    "A workflow.")]
    [InlineData("The picture portrays a dancer.",       "A dancer.")]
    public void Pattern1_VariousVerbs_AreStripped(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.StripFillerOpener(input));

    // Pattern 2 variants — the regex includes (?:an?\s+)? so the article is consumed
    // as part of the stripped prefix; the remainder starts after "a/an ".
    [Theory]
    [InlineData("The main subject is a large tree.",                         "Large tree.")]
    [InlineData("The main subject is an elderly person.",                    "Elderly person.")]
    [InlineData("The main subject of this image is a golden retriever.",     "Golden retriever.")]
    [InlineData("The main subject of the photo is an open laptop.",          "Open laptop.")]
    public void Pattern2_MainSubject_IsStripped(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.StripFillerOpener(input));

    // Case-insensitive matching
    [Fact]
    public void Pattern1_CaseInsensitive()
    {
        var result = TextNormalizer.StripFillerOpener("THE IMAGE SHOWS a cat.");
        Assert.Equal("A cat.", result);
    }

    // Re-capitalisation
    [Fact]
    public void Remainder_IsCapitalised()
    {
        var result = TextNormalizer.StripFillerOpener("The image shows a happy family.");
        Assert.True(char.IsUpper(result[0]), "First char should be uppercase");
    }

    // No match — original returned unchanged
    [Theory]
    [InlineData("A person sits on a bench.")]
    [InlineData("Two adults are talking.")]
    [InlineData("Sunlight filters through the trees.")]
    public void NoMatch_ReturnsOriginal(string input)
        => Assert.Equal(input, TextNormalizer.StripFillerOpener(input));

    // Empty remainder after strip — original returned
    [Fact]
    public void EmptyRemainder_ReturnsOriginal()
    {
        // Strip would leave nothing; return the original rather than an empty string
        var input = "The image shows ";
        var result = TextNormalizer.StripFillerOpener(input);
        Assert.Equal(input, result);
    }
}

// ── IsRepetitionLoop ─────────────────────────────────────────────────────────

public class IsRepetitionLoopTests
{
    // Short text gates
    [Fact]
    public void FewWords_ReturnsFalse()
    {
        var text = string.Join(" ", Enumerable.Repeat("cat", 9));
        Assert.False(TextNormalizer.IsRepetitionLoop(text));
    }

    // Length cap
    [Fact]
    public void Over500Words_ReturnsTrue()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 501));
        Assert.True(TextNormalizer.IsRepetitionLoop(text));
    }

    [Fact]
    public void Exactly500Words_ReturnsFalse_WhenNoDominance()
    {
        // 500 unique-ish words, no single word dominant
        var words = Enumerable.Range(1, 500).Select(i => $"word{i}");
        var text = string.Join(" ", words);
        Assert.False(TextNormalizer.IsRepetitionLoop(text));
    }

    // Dominant word > 40%
    [Fact]
    public void DominantWord_Over40Percent_ReturnsTrue()
    {
        // 11 words total; 5 = "cat" = 45.5% > 40%
        var text = "cat cat cat cat cat dog bird fish turtle snake owl";
        Assert.True(TextNormalizer.IsRepetitionLoop(text));
    }

    [Fact]
    public void DominantWord_Under40Percent_ReturnsFalse()
    {
        // 10 words, 3 = "cat" = 30%
        var text = "cat cat cat dog bird fish turtle snake owl hawk";
        Assert.False(TextNormalizer.IsRepetitionLoop(text));
    }

    // N-gram repetition (≥ 15 words required for ngram check)
    [Fact]
    public void RepeatedNgram_ThreeTimes_ReturnsTrue()
    {
        // Repeat the same 5-word phrase 3 times within a ≥15-word sentence
        var phrase = "the quick brown fox jumps";
        var text = $"{phrase} over the lazy dog {phrase} over the lazy dog {phrase} end";
        // Confirm we're in ngram territory
        Assert.True(text.Split(' ').Length >= 15);
        Assert.True(TextNormalizer.IsRepetitionLoop(text));
    }

    [Fact]
    public void RepeatedNgram_TwiceThenUnique_ReturnsFalse()
    {
        // Same phrase exactly twice — count + 1 = 2, not > 2, so should be false
        var phrase = "the quick brown fox jumps";
        var filler = "over a lazy river near the old stone bridge today";
        var text = $"{phrase} {filler} {phrase} end result";
        Assert.False(TextNormalizer.IsRepetitionLoop(text));
    }

    // Clean normal description
    [Fact]
    public void NormalDescription_ReturnsFalse()
    {
        var text = "A person sits at a wooden table holding a coffee mug with both hands. " +
                   "The background shows a cozy kitchen with warm lighting and plants on the windowsill.";
        Assert.False(TextNormalizer.IsRepetitionLoop(text));
    }
}

// ── TrimToCompleteSentences ──────────────────────────────────────────────────

public class TrimToCompleteSentencesTests
{
    // Note: the 15-char minimum means each kept sentence must be > 15 chars.
    // All test sentences below exceed that threshold.

    // Basic trimming
    [Fact]
    public void TwoSentenceDefault_TrimsThird()
    {
        var text = "A person reads by the window. The cat sleeps on the sofa nearby. Outside the rain begins to fall.";
        var result = TextNormalizer.TrimToCompleteSentences(text);
        Assert.Equal("A person reads by the window. The cat sleeps on the sofa nearby.", result);
    }

    [Fact]
    public void MaxSentences1_RetainsOnlyFirst()
    {
        var text = "A golden retriever runs across the open field. A second long sentence follows here. Third sentence is added.";
        Assert.Equal("A golden retriever runs across the open field.", TextNormalizer.TrimToCompleteSentences(text, 1));
    }

    [Fact]
    public void MaxSentences3_RetainsThree()
    {
        var text = "A person walks along the river path. The sunset paints the sky in orange hues. Birds fly overhead in loose formation. A fourth sentence follows now.";
        Assert.Equal("A person walks along the river path. The sunset paints the sky in orange hues. Birds fly overhead in loose formation.", TextNormalizer.TrimToCompleteSentences(text, 3));
    }

    // Terminator variety
    [Fact]
    public void ExclamationMark_EndsSentence()
    {
        var text = "What an incredible view from here! The mountains are truly stunning today. Extra long sentence here.";
        Assert.Equal("What an incredible view from here! The mountains are truly stunning today.", TextNormalizer.TrimToCompleteSentences(text));
    }

    [Fact]
    public void QuestionMark_EndsSentence()
    {
        var text = "Is this a beautiful park? It certainly looks peaceful today. Extra long sentence appended.";
        Assert.Equal("Is this a beautiful park? It certainly looks peaceful today.", TextNormalizer.TrimToCompleteSentences(text));
    }

    // Ellipsis guard — "..." should not be treated as a sentence boundary
    [Fact]
    public void Ellipsis_DoesNotSplitSentence()
    {
        // "..." must not trigger a split mid-sentence
        var text = "They walked slowly... and then stopped. Second sentence.";
        var result = TextNormalizer.TrimToCompleteSentences(text, 1);
        // The first real boundary is the '.' after "stopped"
        Assert.Equal("They walked slowly... and then stopped.", result);
    }

    // Minimum 15-char sentence guard — stub fragments are skipped
    [Fact]
    public void ShortFragment_IsSkipped()
    {
        // "OK." is < 15 chars, so the parser skips it and keeps looking
        var text = "OK. A real and complete sentence here. Third.";
        var result = TextNormalizer.TrimToCompleteSentences(text, 1);
        Assert.Equal("A real and complete sentence here.", result);
    }

    // Fallback — no sentence boundary found returns full text
    [Fact]
    public void NoSentenceBoundary_ReturnsFullText()
    {
        var text = "Just a fragment with no terminator";
        Assert.Equal(text, TextNormalizer.TrimToCompleteSentences(text));
    }

    // Single sentence — no trimming needed
    [Fact]
    public void SingleSentence_ReturnedAsIs()
    {
        var text = "A person is standing in a park.";
        Assert.Equal(text, TextNormalizer.TrimToCompleteSentences(text));
    }

    // Sentence at very end of string (no trailing space)
    [Fact]
    public void LastCharIsPeriod_CountsAsBoundary()
    {
        var text = "Only one sentence here.";
        Assert.Equal("Only one sentence here.", TextNormalizer.TrimToCompleteSentences(text, 2));
    }
}

// ── FlattenNewlines ──────────────────────────────────────────────────────────

public class FlattenNewlinesTests
{
    // General newline — missing punctuation → period inserted
    [Fact]
    public void NewlineAfterWord_InsertsperiodAndSpace()
    {
        var text = "First line\nSecond line";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.Equal("First line. Second line", result);
    }

    [Fact]
    public void WindowsNewline_IsFlattened()
    {
        var text = "First line\r\nSecond line";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.Equal("First line. Second line", result);
    }

    // Already-terminated — no double period
    [Fact]
    public void LineEndingWithPeriod_NoDoublePeriod()
    {
        var text = "First sentence.\nSecond sentence.";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.DoesNotContain("..", result);
        Assert.Contains("First sentence.", result);
        Assert.Contains("Second sentence.", result);
    }

    // Ellipsis-paragraph fix
    [Fact]
    public void EllipsisBeforeNewline_CollapsedToSinglePeriod()
    {
        var text = "A person in a red shirt...\nThey are smiling.";
        var result = TextNormalizer.FlattenNewlines(text);
        // Should NOT produce "... . They" — ellipsis fix runs first
        Assert.DoesNotContain("... .", result);
        Assert.Contains(". They", result);
    }

    [Fact]
    public void MultipleEllipsis_BeforeNewline_CollapsedCorrectly()
    {
        var text = "Running fast....\nThen stopped.";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.DoesNotContain("....", result);
        // Result should join into a single-period boundary
        Assert.Contains(". Then", result);
    }

    // Multiple blank lines collapsed
    [Fact]
    public void MultipleNewlines_CollapsedToSingleSpace()
    {
        var text = "First paragraph\n\n\nSecond paragraph";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.DoesNotContain("\n", result);
    }

    // No-op on flat text
    [Fact]
    public void NoNewlines_ReturnsUnchanged()
    {
        var text = "A flat sentence with no line breaks at all.";
        Assert.Equal(text, TextNormalizer.FlattenNewlines(text));
    }

    // Combined: two paragraphs, first ends with ellipsis
    [Fact]
    public void Combined_EllipsisAndPlainNewline()
    {
        var text = "Intro text...\nMain content here\nFinal part.";
        var result = TextNormalizer.FlattenNewlines(text);
        Assert.DoesNotContain("\n", result);
        Assert.DoesNotContain("...", result);
    }
}

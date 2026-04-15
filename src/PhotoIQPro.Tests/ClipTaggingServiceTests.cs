using PhotoIQPro.Core.Interfaces;
using PhotoIQPro.Core.Models;
using PhotoIQPro.Services.Tagging;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>
/// Unit tests for <see cref="ClipTaggingService.ApplyFilters"/> — the core suppression
/// and threshold logic extracted from GenerateTagsAsync.
///
/// These tests run without ONNX models by supplying hand-crafted TagPrediction lists
/// directly to the internal static ApplyFilters method (accessible via InternalsVisibleTo).
///
/// Thresholds under test (defined in ClipTaggingService as private constants):
///   ConfidenceThreshold         = 0.22f  (base: Scene / Object / Style / Color)
///   ActivityConfidenceThreshold = 0.26f  (Activity)
///   OccasionConfidenceThreshold = 0.32f  (Event)
///   MaxTagsPerImage             = 10
/// </summary>
public class ClipTaggingServiceFilterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TagPrediction Score(string label, TagCategory cat, float confidence)
        => new(label, cat, confidence);

    /// <summary>
    /// Build a minimal allScores list that always includes indoor and outdoor anchors
    /// so the indoor/outdoor resolver has both to compare, then merge in extra predictions.
    /// </summary>
    private static List<TagPrediction> WithContext(float indoorScore, float outdoorScore,
        params TagPrediction[] extras)
    {
        var list = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, indoorScore),
            Score("outdoor", TagCategory.Scene, outdoorScore),
        };
        list.AddRange(extras);
        return list;
    }

    // ── 1. Outdoor-only suppression when photo is indoor ─────────────────────

    [Fact]
    public void OutdoorOnlyTags_Suppressed_WhenIndoorScoreHigher()
    {
        // indoor > outdoor → isIndoor = true → all OutdoorOnlyTags suppressed
        var allScores = WithContext(
            indoorScore:  0.40f,
            outdoorScore: 0.20f,
            Score("rain",      TagCategory.Scene, 0.30f),
            Score("snow",      TagCategory.Scene, 0.30f),
            Score("beach",     TagCategory.Scene, 0.30f),
            Score("ocean",     TagCategory.Scene, 0.30f),
            Score("lake",      TagCategory.Scene, 0.30f),
            Score("river",     TagCategory.Scene, 0.30f),
            Score("forest",    TagCategory.Scene, 0.30f),
            Score("mountains", TagCategory.Scene, 0.30f),
            Score("park",      TagCategory.Scene, 0.30f),
            Score("street",    TagCategory.Scene, 0.30f),
            Score("city",      TagCategory.Scene, 0.30f),
            Score("sunset",    TagCategory.Scene, 0.30f)
        );

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        Assert.DoesNotContain("rain",      labels);
        Assert.DoesNotContain("snow",      labels);
        Assert.DoesNotContain("beach",     labels);
        Assert.DoesNotContain("ocean",     labels);
        Assert.DoesNotContain("lake",      labels);
        Assert.DoesNotContain("river",     labels);
        Assert.DoesNotContain("forest",    labels);
        Assert.DoesNotContain("mountains", labels);
        Assert.DoesNotContain("park",      labels);
        Assert.DoesNotContain("street",    labels);
        Assert.DoesNotContain("city",      labels);
        Assert.DoesNotContain("sunset",    labels);
    }

    [Fact]
    public void OutdoorOnlyTags_Suppressed_WhenIndoorEqualsOutdoor()
    {
        // indoor == outdoor → isIndoor = true (indoor >= outdoor) → suppressed
        var allScores = WithContext(
            indoorScore:  0.30f,
            outdoorScore: 0.30f,
            Score("rain", TagCategory.Scene, 0.30f)
        );

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.DoesNotContain(results, r => r.Label == "rain");
    }

    // ── 2. Outdoor-only tags survive when photo is outdoor ───────────────────

    [Fact]
    public void OutdoorOnlyTags_NotSuppressed_WhenOutdoorScoreHigher()
    {
        // outdoor > indoor → isIndoor = false → OutdoorOnlyTags are NOT suppressed
        var allScores = WithContext(
            indoorScore:  0.10f,
            outdoorScore: 0.50f,
            Score("rain",      TagCategory.Scene, 0.30f),
            Score("snow",      TagCategory.Scene, 0.30f),
            Score("beach",     TagCategory.Scene, 0.30f),
            Score("ocean",     TagCategory.Scene, 0.30f),
            Score("lake",      TagCategory.Scene, 0.30f),
            Score("river",     TagCategory.Scene, 0.30f),
            Score("forest",    TagCategory.Scene, 0.30f),
            Score("mountains", TagCategory.Scene, 0.30f),
            Score("park",      TagCategory.Scene, 0.30f),
            Score("street",    TagCategory.Scene, 0.30f),
            Score("city",      TagCategory.Scene, 0.30f),
            Score("sunset",    TagCategory.Scene, 0.30f)
        );

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        // All twelve OutdoorOnlyTags exceed the base threshold (0.22) and are outdoor.
        Assert.Contains("rain",      labels);
        Assert.Contains("snow",      labels);
        Assert.Contains("beach",     labels);
        Assert.Contains("ocean",     labels);
        Assert.Contains("lake",      labels);
        Assert.Contains("river",     labels);
        Assert.Contains("forest",    labels);
        Assert.Contains("mountains", labels);
        Assert.Contains("park",      labels);
        Assert.Contains("street",    labels);
        Assert.Contains("city",      labels);
        Assert.Contains("sunset",    labels);
    }

    // ── 3. Occasion (Event) threshold = 0.32 ────────────────────────────────

    [Fact]
    public void EventTag_RequiresOccasionThreshold_NotBaseThreshold()
    {
        // Confidence 0.25 is above base (0.22) but below Event threshold (0.32).
        var allScores = new List<TagPrediction>
        {
            Score("indoor",       TagCategory.Scene, 0.40f),
            Score("outdoor",      TagCategory.Scene, 0.10f),
            Score("thanksgiving", TagCategory.Event, 0.25f),   // should be suppressed
            Score("christmas",    TagCategory.Event, 0.33f),   // should survive
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        Assert.DoesNotContain("thanksgiving", labels);  // 0.25 < 0.32 → suppressed
        Assert.Contains("christmas", labels);           // 0.33 >= 0.32 → survives
    }

    [Fact]
    public void EventTag_ExactlyAtOccasionThreshold_Included()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",    TagCategory.Scene, 0.40f),
            Score("outdoor",   TagCategory.Scene, 0.10f),
            Score("halloween", TagCategory.Event, 0.32f),  // exactly at threshold
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.Contains(results, r => r.Label == "halloween");
    }

    [Fact]
    public void EventTag_JustBelowOccasionThreshold_Excluded()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",    TagCategory.Scene, 0.40f),
            Score("outdoor",   TagCategory.Scene, 0.10f),
            Score("halloween", TagCategory.Event, 0.319f),  // just below 0.32
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.DoesNotContain(results, r => r.Label == "halloween");
    }

    // ── 4. Activity threshold = 0.26 ────────────────────────────────────────

    [Fact]
    public void ActivityTag_RequiresActivityThreshold_NotBaseThreshold()
    {
        // 0.23 is above base (0.22) but below Activity threshold (0.26).
        var allScores = new List<TagPrediction>
        {
            Score("indoor",   TagCategory.Scene,    0.40f),
            Score("outdoor",  TagCategory.Scene,    0.10f),
            Score("eating",   TagCategory.Activity, 0.23f),  // should be suppressed
            Score("dancing",  TagCategory.Activity, 0.27f),  // should survive
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        Assert.DoesNotContain("eating",  labels);  // 0.23 < 0.26 → suppressed
        Assert.Contains("dancing", labels);        // 0.27 >= 0.26 → survives
    }

    [Fact]
    public void ActivityTag_ExactlyAtActivityThreshold_Included()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",   TagCategory.Scene,    0.40f),
            Score("outdoor",  TagCategory.Scene,    0.10f),
            Score("swimming", TagCategory.Activity, 0.26f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.Contains(results, r => r.Label == "swimming");
    }

    // ── 5. Indoor/outdoor mutual exclusion ───────────────────────────────────

    [Fact]
    public void Indoor_And_Outdoor_NeverBothInResults()
    {
        // Both scores above base threshold — only the winner should appear.
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, 0.40f),
            Score("outdoor", TagCategory.Scene, 0.30f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToList();

        var hasIndoor  = labels.Contains("indoor");
        var hasOutdoor = labels.Contains("outdoor");

        // Exactly one of them may appear — not both, not neither (both exceed threshold).
        Assert.False(hasIndoor && hasOutdoor, "indoor and outdoor must not both appear");
        Assert.True(hasIndoor  || hasOutdoor, "at least one of indoor/outdoor should appear");
    }

    [Fact]
    public void Indoor_Wins_WhenIndoorScoreHigher()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, 0.50f),
            Score("outdoor", TagCategory.Scene, 0.30f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.Contains(results,    r => r.Label == "indoor");
        Assert.DoesNotContain(results, r => r.Label == "outdoor");
    }

    [Fact]
    public void Outdoor_Wins_WhenOutdoorScoreHigher()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, 0.25f),
            Score("outdoor", TagCategory.Scene, 0.45f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.Contains(results,    r => r.Label == "outdoor");
        Assert.DoesNotContain(results, r => r.Label == "indoor");
    }

    // ── 6. Working context suppresses athletic tags ──────────────────────────

    [Fact]
    public void Working_Suppresses_Dancing_Running_Sports()
    {
        // All four tags exceed the activity threshold (0.26).
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene,    0.40f),
            Score("outdoor", TagCategory.Scene,    0.10f),
            Score("working", TagCategory.Activity, 0.30f),
            Score("dancing", TagCategory.Activity, 0.30f),
            Score("running", TagCategory.Activity, 0.30f),
            Score("sports",  TagCategory.Activity, 0.30f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        Assert.Contains("working",     labels);   // anchor tag remains
        Assert.DoesNotContain("dancing", labels);
        Assert.DoesNotContain("running", labels);
        Assert.DoesNotContain("sports",  labels);
    }

    [Fact]
    public void Without_Working_Athletic_Tags_Are_Retained()
    {
        // Same scores but no "working" — athletic tags survive.
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene,    0.40f),
            Score("outdoor", TagCategory.Scene,    0.10f),
            Score("dancing", TagCategory.Activity, 0.30f),
            Score("running", TagCategory.Activity, 0.30f),
            Score("sports",  TagCategory.Activity, 0.30f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        Assert.Contains("dancing", labels);
        Assert.Contains("running", labels);
        Assert.Contains("sports",  labels);
    }

    [Fact]
    public void Working_Does_Not_Suppress_Other_Activities()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene,    0.40f),
            Score("outdoor", TagCategory.Scene,    0.10f),
            Score("working", TagCategory.Activity, 0.30f),
            Score("eating",  TagCategory.Activity, 0.30f),
            Score("hiking",  TagCategory.Activity, 0.30f),
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        var labels  = results.Select(r => r.Label).ToHashSet();

        // eating and hiking are not in the suppressed set.
        Assert.Contains("eating", labels);
        Assert.Contains("hiking", labels);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void EmptyScoreList_ReturnsEmpty()
    {
        var results = ClipTaggingService.ApplyFilters(new List<TagPrediction>());
        Assert.Empty(results);
    }

    [Fact]
    public void BaseThreshold_Tag_ExactlyAtThreshold_Included()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, 0.40f),
            Score("outdoor", TagCategory.Scene, 0.10f),
            Score("dog",     TagCategory.Object, 0.22f),  // exactly at base threshold
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.Contains(results, r => r.Label == "dog");
    }

    [Fact]
    public void BaseThreshold_Tag_BelowThreshold_Excluded()
    {
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene,  0.40f),
            Score("outdoor", TagCategory.Scene,  0.10f),
            Score("dog",     TagCategory.Object, 0.21f),  // just below 0.22
        };

        var results = ClipTaggingService.ApplyFilters(allScores);
        Assert.DoesNotContain(results, r => r.Label == "dog");
    }

    [Fact]
    public void MaxTagsPerImage_LimitsResults_To10()
    {
        // Create 15 high-scoring Object tags — only 10 should survive.
        var allScores = new List<TagPrediction>
        {
            Score("indoor",  TagCategory.Scene, 0.40f),
            Score("outdoor", TagCategory.Scene, 0.10f),
        };
        for (int i = 0; i < 15; i++)
            allScores.Add(Score($"tag{i}", TagCategory.Object, 0.30f));

        var results = ClipTaggingService.ApplyFilters(allScores);

        // indoor counts toward the cap too; total ≤ 10.
        Assert.True(results.Count <= 10, $"Expected ≤ 10 results, got {results.Count}");
    }
}

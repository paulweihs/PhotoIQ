using PhotoWell.Common;
using PhotoWell.Desktop.Models;

namespace PhotoWell.Desktop.Services;

/// <summary>
/// Persists and queries onboarding / tip-toast state via <see cref="UserPreferences"/>.
/// Also owns the canonical card and tip content arrays.
/// Registered as a singleton in DI.
/// </summary>
public class OnboardingService
{
    // ── State queries ─────────────────────────────────────────────────────────

    public bool ShouldShowOnboarding() => !UserPreferences.Current.HasCompletedOnboarding;

    public bool ShouldShowTip() =>
        UserPreferences.Current.HasCompletedOnboarding &&
        UserPreferences.Current.TipsEnabled;

    // ── State mutations ───────────────────────────────────────────────────────

    public void MarkOnboardingComplete()
    {
        UserPreferences.Current.HasCompletedOnboarding = true;
        UserPreferences.Current.Save();
    }

    public void DisableTips()
    {
        UserPreferences.Current.TipsEnabled = false;
        UserPreferences.Current.Save();
    }

    public void EnableTips()
    {
        UserPreferences.Current.TipsEnabled = true;
        UserPreferences.Current.Save();
    }

    public TipData GetNextTip()
    {
        var prefs = UserPreferences.Current;
        var tip   = AllTips[prefs.LastTipIndex % AllTips.Length];
        prefs.LastTipIndex++;
        prefs.Save();
        return tip;
    }

    // ── Card content ──────────────────────────────────────────────────────────

    public static readonly CardData[] AllCards =
    [
        new CardData(
            Eyebrow:     "Welcome",
            TitlePrefix: "Your photos, ",
            TitleItalic: "understood.",
            TitleSuffix: "",
            Body:        "Photowell uses local AI to read, describe, and search every photo in your library — privately, on your machine, without cloud services or subscriptions.",
            Visual:      CardVisualKey.Welcome),

        new CardData(
            Eyebrow:     "Privacy",
            TitlePrefix: "No cloud. No account. ",
            TitleItalic: "No exceptions.",
            TitleSuffix: "",
            Body:        "Every AI model runs locally on your hardware. Your photos never leave your machine — not for analysis, not for backup, not ever. This is architectural, not a setting.",
            Visual:      CardVisualKey.Privacy),

        new CardData(
            Eyebrow:     "The AI",
            TitlePrefix: "Every photo ",
            TitleItalic: "gets a description.",
            TitleSuffix: "",
            Body:        "When you import photos, a local vision model reads each one and writes a natural language description. That description powers search. You can edit or protect it — the AI never overwrites your words.",
            Visual:      CardVisualKey.TheAI),

        new CardData(
            Eyebrow:     "Search",
            TitlePrefix: "Search like ",
            TitleItalic: "you think.",
            TitleSuffix: "",
            Body:        "Don't remember the filename? Don't have tags? It doesn't matter. Type what you remember — a mood, a moment, a detail — and Photowell finds it.",
            Visual:      CardVisualKey.Search),

        new CardData(
            Eyebrow:     "Two Search Modes",
            TitlePrefix: "Semantic ",
            TitleItalic: "or",
            TitleSuffix: " keyword — your choice.",
            Body:        "Semantic search understands meaning and context. Keyword search matches exact text in tags, filenames, and dates. Use both independently or together.",
            Visual:      CardVisualKey.TwoModes),

        new CardData(
            Eyebrow:     "Favorites & Albums",
            TitlePrefix: "Organize without ",
            TitleItalic: "moving anything.",
            TitleSuffix: "",
            Body:        "Favorites and Albums are lightweight overlays on your existing files. No copying, no folder restructuring — your files stay exactly where they are on disk.",
            Visual:      CardVisualKey.FavoritesAlbums),

        new CardData(
            Eyebrow:     "Find Duplicates",
            TitlePrefix: "30 years of photos ",
            TitleItalic: "means duplicates.",
            TitleSuffix: "",
            Body:        "Photowell detects exact duplicates and RAW+JPEG pairs automatically. Choose which version to keep — or let Photowell apply your preference across the entire group.",
            Visual:      CardVisualKey.FindDuplicates),

        new CardData(
            Eyebrow:     "You're ready",
            TitlePrefix: "Add your ",
            TitleItalic: "first folder.",
            TitleSuffix: "",
            Body:        "Point Photowell at any folder — a drive, a year's worth of shots, a lifetime's archive. The AI gets to work immediately. You can search before it's finished.",
            Visual:      CardVisualKey.LetsGo),
    ];

    // ── Tip content ───────────────────────────────────────────────────────────

    public static readonly TipData[] AllTips =
    [
        new TipData(
            Icon:     "🔍",
            Title:    "Search understands mood, not just filenames.",
            Subtitle: "Try: \"foggy morning by the water\" or \"birthday cake, candlelight\" — no tags needed."),

        new TipData(
            Icon:     "⭐",
            Title:    "Favorites survive library reorganization.",
            Subtitle: "Starring a photo links it to Photowell's index, not its folder path. Move files freely."),

        new TipData(
            Icon:     "🧠",
            Title:    "You can always edit an AI description.",
            Subtitle: "Right-click any photo → Edit Description. Your version is protected and never overwritten."),

        new TipData(
            Icon:     "📷",
            Title:    "RAW and JPEG duplicates are detected automatically.",
            Subtitle: "Check Find Duplicates after importing a mixed library — Photowell pairs them intelligently."),

        new TipData(
            Icon:     "🔒",
            Title:    "No internet connection is ever required.",
            Subtitle: "Search, import, and AI analysis all run offline. Your photos never leave your machine."),

        new TipData(
            Icon:     "⌨️",
            Title:    "Keyboard shortcuts speed up your workflow.",
            Subtitle: "Arrow keys navigate photos. Ctrl+A selects all. Escape returns to the grid."),

        new TipData(
            Icon:     "🔄",
            Title:    "Imports survive restarts.",
            Subtitle: "If Photowell closes mid-import, the queue picks up exactly where it left off."),
    ];
}

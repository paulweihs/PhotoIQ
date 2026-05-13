namespace PhotoWell.Desktop.Models;

public enum CardVisualKey
{
    Welcome, Privacy, TheAI, Search, TwoModes, FavoritesAlbums, FindDuplicates, LetsGo
}

/// <summary>
/// Content data for a single onboarding card.
/// The title is split into three parts so the XAML code-behind can insert the italic Run
/// without binding (WPF Inline binding is not natively supported).
/// </summary>
public record CardData(
    string        Eyebrow,
    string        TitlePrefix,   // plain weight, colour #1a1a18
    string        TitleItalic,   // italic, colour #2B4A3F
    string        TitleSuffix,   // plain weight, colour #1a1a18 (empty for most cards)
    string        Body,
    CardVisualKey Visual);

using System.Text.RegularExpressions;

namespace PhotoWell.Services.Search;

/// <summary>
/// Strips structured prefix tokens from a search query before the FTS/date pipeline sees it.
///
/// Supported prefixes:
///   drive:G          → only show photos whose FilePath starts with "G:\"
///   drive:G:         → same
///   imported:may 2024         → filter by DateImported (not DateTaken)
///   imported:2024             → same, accepts any DateSearchParser expression
///   imported:sep 4 2012       → exact day by DateImported
/// </summary>
public static class SearchPrefixParser
{
    private static readonly Regex DriveRx = new(
        @"\bdrive:([A-Za-z]):?\\?", RegexOptions.IgnoreCase);

    private static readonly Regex ImportedPrefixRx = new(
        @"\bimported:", RegexOptions.IgnoreCase);

    public record Tokens(
        string? DriveRoot,              // e.g. "G:\" or null
        DateSearchParser.DateRange? ImportedRange,  // from imported:… or null
        string RemainingQuery);         // everything with prefixes stripped

    public static Tokens Parse(string query)
    {
        string remaining = query;
        string? driveRoot = null;
        DateSearchParser.DateRange? importedRange = null;

        // ── drive: ────────────────────────────────────────────────────────
        var dm = DriveRx.Match(remaining);
        if (dm.Success)
        {
            driveRoot = dm.Groups[1].Value.ToUpperInvariant() + @":\";
            remaining = Strip(remaining, dm);
        }

        // ── imported: ─────────────────────────────────────────────────────
        var im = ImportedPrefixRx.Match(remaining);
        if (im.Success)
        {
            // Everything after "imported:" is the potential date expression.
            // DateSearchParser.TryParse already strips the matched date from the text
            // and returns the leftover keywords in RemainingQuery.
            string afterColon = remaining[(im.Index + im.Length)..].Trim();
            var parsed = DateSearchParser.TryParse(afterColon);
            if (parsed != null)
            {
                importedRange = parsed.Range;
                // Keep text before "imported:" + any non-date keywords after it
                string before = remaining[..im.Index];
                remaining = (before + " " + parsed.RemainingQuery).Trim();
            }
            else
            {
                // Unrecognised date — just strip the "imported:" keyword
                remaining = Strip(remaining, im);
            }
        }

        return new Tokens(driveRoot, importedRange, remaining.Trim());
    }

    private static string Strip(string s, Match m) =>
        Regex.Replace(s.Remove(m.Index, m.Length), @"\s{2,}", " ").Trim();
}

using System.Text.RegularExpressions;

namespace PhotoWell.Services.Search;

/// <summary>
/// Parses natural-language date expressions out of a search query string.
///
/// Supported patterns (case-insensitive):
///   "may 2024"                → May 1 – May 31, 2024
///   "january 2024"            → January 1 – January 31, 2024
///   "2024"                    → Jan 1 – Dec 31, 2024
///   "2023-2024" / "2023 to 2024" → Jan 1, 2023 – Dec 31, 2024
///   "jan 2023 - mar 2024"     → Jan 1, 2023 – Mar 31, 2024
///   "may 2023 to july 2024"   → May 1, 2023 – Jul 31, 2024
///   "summer 2023"             → Jun 1 – Aug 31, 2023
///   "winter 2023"             → Dec 1, 2023 – Feb 28/29, 2024
///   "spring 2024"             → Mar 1 – May 31, 2024
///   "fall 2023" / "autumn 2023" → Sep 1 – Nov 30, 2023
///   "christmas 2022"          → Dec 2022
///   "thanksgiving 2022"       → Nov 2022
///   "halloween 2022"          → Oct 2022
///   "new year 2023"           → Jan 2023
///   "may" (no year)           → May across all years (1900–9999)
///   "2024 sunset" → date=2024, remaining keywords="sunset"
///
/// Returns null if no date expression is recognised.
/// </summary>
public static class DateSearchParser
{
    public record DateRange(DateTime From, DateTime To);
    public record ParseResult(DateRange Range, string RemainingQuery);

    // Month name → 1-based month number
    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"]   = 1,
        ["feb"] = 2, ["february"]  = 2,
        ["mar"] = 3, ["march"]     = 3,
        ["apr"] = 4, ["april"]     = 4,
        ["may"] = 5,
        ["jun"] = 6, ["june"]      = 6,
        ["jul"] = 7, ["july"]      = 7,
        ["aug"] = 8, ["august"]    = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"]  = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    private static readonly string MonthPattern =
        @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private static readonly string YearPattern = @"(?:19|20)\d{2}";

    // Named events
    private static readonly Dictionary<string, (int month, int? endMonth)> Events
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["christmas"]    = (12, null),
        ["thanksgiving"] = (11, null),
        ["halloween"]    = (10, null),
        ["easter"]       = (4,  null),
        ["new year"]     = (1,  null),
        ["new years"]    = (1,  null),
    };

    // Seasons → (start month, end month) — Northern Hemisphere
    private static readonly Dictionary<string, (int start, int end)> Seasons
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spring"] = (3,  5),
        ["summer"] = (6,  8),
        ["fall"]   = (9,  11),
        ["autumn"] = (9,  11),
        ["winter"] = (12, 2),   // Dec of given year → Feb of next year
    };

    /// <summary>
    /// Attempts to extract a date range from <paramref name="query"/>.
    /// Returns null if no date expression is found.
    /// </summary>
    public static ParseResult? TryParse(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        // Try patterns from most-specific to least-specific.
        // ExactDate must come first to prevent "sep 4 2012" matching "2012" as a bare year.
        // SeasonYear and EventYear must come before YearOnly — otherwise "summer 2023"
        // would match the bare year "2023" before the season pattern is tried.
        return TryParseExactDate(query)
            ?? TryParseMonthRange(query)
            ?? TryParseYearRange(query)
            ?? TryParseMonthYear(query)
            ?? TryParseSeasonYear(query)
            ?? TryParseEventYear(query)
            ?? TryParseYearOnly(query)
            ?? TryParseMonthOnly(query);
    }

    // ── Pattern: exact day ───────────────────────────────────────────────────
    //   ISO:       2013-10-31
    //   US slash:  10/31/2013  or  10/31/13
    //   US dash:   10-31-2013
    //   Named:     sep 4 2012 / sep 4, 2012 / september 4 2012 / 4 sep 2012

    private static ParseResult? TryParseExactDate(string query)
    {
        // ISO: yyyy-MM-dd
        var isoRx = new Regex(@"\b(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})\b");
        var mt = isoRx.Match(query);
        if (mt.Success && TryBuildDayRange(
                int.Parse(mt.Groups["y"].Value),
                int.Parse(mt.Groups["m"].Value),
                int.Parse(mt.Groups["d"].Value), out var r))
            return new ParseResult(r!, StripMatch(query, mt));

        // US numeric: M/D/YYYY  or  M/D/YY  or  M-D-YYYY
        var usRx = new Regex(@"\b(?<m>\d{1,2})[/\-](?<d>\d{1,2})[/\-](?<y>\d{2,4})\b");
        mt = usRx.Match(query);
        if (mt.Success)
        {
            int yr = int.Parse(mt.Groups["y"].Value);
            if (yr < 100) yr += 2000;
            if (TryBuildDayRange(yr, int.Parse(mt.Groups["m"].Value), int.Parse(mt.Groups["d"].Value), out r))
                return new ParseResult(r!, StripMatch(query, mt));
        }

        // Named: "sep 4 2012" / "sep 4, 2012"
        var namedMDY = new Regex(
            $@"\b(?<mo>{MonthPattern})\s+(?<d>\d{{1,2}}),?\s+(?<y>{YearPattern})\b",
            RegexOptions.IgnoreCase);
        mt = namedMDY.Match(query);
        if (mt.Success && TryBuildDayRange(
                int.Parse(mt.Groups["y"].Value),
                MonthNames[mt.Groups["mo"].Value],
                int.Parse(mt.Groups["d"].Value), out r))
            return new ParseResult(r!, StripMatch(query, mt));

        // Named: "4 sep 2012"
        var namedDMY = new Regex(
            $@"\b(?<d>\d{{1,2}})\s+(?<mo>{MonthPattern})\s+(?<y>{YearPattern})\b",
            RegexOptions.IgnoreCase);
        mt = namedDMY.Match(query);
        if (mt.Success && TryBuildDayRange(
                int.Parse(mt.Groups["y"].Value),
                MonthNames[mt.Groups["mo"].Value],
                int.Parse(mt.Groups["d"].Value), out r))
            return new ParseResult(r!, StripMatch(query, mt));

        return null;
    }

    private static bool TryBuildDayRange(int year, int month, int day, out DateRange? range)
    {
        range = null;
        if (year < 1900 || year > 9999 || month < 1 || month > 12) return false;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        range = new DateRange(
            new DateTime(year, month, day, 0, 0, 0),
            new DateTime(year, month, day, 23, 59, 59));
        return true;
    }

    // ── Pattern: "jan 2023 - mar 2024" / "jan 2023 to mar 2024" ─────────────

    private static ParseResult? TryParseMonthRange(string query)
    {
        var rx = new Regex(
            $@"(?<m1>{MonthPattern})\s+(?<y1>{YearPattern})\s*(?:-|to)\s*(?<m2>{MonthPattern})\s+(?<y2>{YearPattern})",
            RegexOptions.IgnoreCase);
        var m = rx.Match(query);
        if (!m.Success) return null;

        int month1 = MonthNames[m.Groups["m1"].Value];
        int year1  = int.Parse(m.Groups["y1"].Value);
        int month2 = MonthNames[m.Groups["m2"].Value];
        int year2  = int.Parse(m.Groups["y2"].Value);

        var from = new DateTime(year1, month1, 1);
        var to   = LastDayOfMonth(year2, month2);
        if (from > to) return null;

        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "2023 - 2024" / "2023 to 2024" ─────────────────────────────

    private static ParseResult? TryParseYearRange(string query)
    {
        var rx = new Regex(
            $@"(?<y1>{YearPattern})\s*(?:-|to)\s*(?<y2>{YearPattern})",
            RegexOptions.IgnoreCase);
        var m = rx.Match(query);
        if (!m.Success) return null;

        int year1 = int.Parse(m.Groups["y1"].Value);
        int year2 = int.Parse(m.Groups["y2"].Value);
        if (year2 < year1) return null;

        var from = new DateTime(year1, 1, 1);
        var to   = new DateTime(year2, 12, 31, 23, 59, 59);
        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "may 2024" / "january 2024" ────────────────────────────────

    private static ParseResult? TryParseMonthYear(string query)
    {
        // Accept "month year" or "year month"
        var rx = new Regex(
            $@"(?:(?<m>{MonthPattern})\s+(?<y>{YearPattern})|(?<y>{YearPattern})\s+(?<m>{MonthPattern}))",
            RegexOptions.IgnoreCase);
        var m = rx.Match(query);
        if (!m.Success) return null;

        int month = MonthNames[m.Groups["m"].Value];
        int year  = int.Parse(m.Groups["y"].Value);

        var from = new DateTime(year, month, 1);
        var to   = LastDayOfMonth(year, month);
        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "2024" (bare year) ─────────────────────────────────────────

    private static ParseResult? TryParseYearOnly(string query)
    {
        var rx = new Regex($@"\b(?<y>{YearPattern})\b", RegexOptions.IgnoreCase);
        var m  = rx.Match(query);
        if (!m.Success) return null;

        int year = int.Parse(m.Groups["y"].Value);
        var from = new DateTime(year, 1, 1);
        var to   = new DateTime(year, 12, 31, 23, 59, 59);
        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "summer 2023" / "fall 2023" ────────────────────────────────

    private static ParseResult? TryParseSeasonYear(string query)
    {
        var seasonPattern = string.Join("|", Seasons.Keys.Select(Regex.Escape));
        var rx = new Regex(
            $@"\b(?<s>{seasonPattern})\s+(?<y>{YearPattern})\b",
            RegexOptions.IgnoreCase);
        var m = rx.Match(query);
        if (!m.Success) return null;

        var (start, end) = Seasons[m.Groups["s"].Value];
        int year = int.Parse(m.Groups["y"].Value);

        DateTime from, to;
        if (start > end) // winter: Dec year → Feb year+1
        {
            from = new DateTime(year, start, 1);
            to   = LastDayOfMonth(year + 1, end);
        }
        else
        {
            from = new DateTime(year, start, 1);
            to   = LastDayOfMonth(year, end);
        }

        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "christmas 2022" / "halloween 2022" ────────────────────────

    private static ParseResult? TryParseEventYear(string query)
    {
        var eventPattern = string.Join("|", Events.Keys.Select(Regex.Escape));
        var rx = new Regex(
            $@"\b(?<e>{eventPattern})\s+(?<y>{YearPattern})\b",
            RegexOptions.IgnoreCase);
        var m = rx.Match(query);
        if (!m.Success) return null;

        var (month, _) = Events[m.Groups["e"].Value];
        int year = int.Parse(m.Groups["y"].Value);

        var from = new DateTime(year, month, 1);
        var to   = LastDayOfMonth(year, month);
        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Pattern: "may" (month only, no year) ────────────────────────────────
    // Matches all photos taken in that month across all years.

    private static ParseResult? TryParseMonthOnly(string query)
    {
        var rx = new Regex($@"\b(?<m>{MonthPattern})\b", RegexOptions.IgnoreCase);
        var m  = rx.Match(query);
        if (!m.Success) return null;

        int month = MonthNames[m.Groups["m"].Value];
        // Span all reasonable years — the repo filters by DateTaken.
        var from = new DateTime(1900, month, 1);
        var to   = new DateTime(9999, month, DateTime.DaysInMonth(9999, month), 23, 59, 59);
        return new ParseResult(new DateRange(from, to), StripMatch(query, m));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTime LastDayOfMonth(int year, int month)
    {
        int day = DateTime.DaysInMonth(year, month);
        return new DateTime(year, month, day, 23, 59, 59);
    }

    /// <summary>Returns query with the matched date expression removed and whitespace cleaned up.</summary>
    private static string StripMatch(string query, Match m)
        => Regex.Replace(
            query.Remove(m.Index, m.Length),
            @"\s{2,}", " ").Trim();
}

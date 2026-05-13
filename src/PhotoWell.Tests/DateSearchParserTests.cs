using PhotoWell.Services.Search;
using Xunit;

namespace PhotoWell.Tests;

public class DateSearchParserTests
{
    // ── Month + Year ──────────────────────────────────────────────────────────

    [Fact]
    public void MonthYear_FullName()
    {
        var r = DateSearchParser.TryParse("january 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 1, 31, 23, 59, 59), r.Range.To);
        Assert.Equal("", r.RemainingQuery);
    }

    [Fact]
    public void MonthYear_Abbreviation()
    {
        var r = DateSearchParser.TryParse("may 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 5, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 5, 31, 23, 59, 59), r.Range.To);
        Assert.Equal("", r.RemainingQuery);
    }

    [Fact]
    public void MonthYear_YearFirst()
    {
        var r = DateSearchParser.TryParse("2024 may");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 5, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 5, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void MonthYear_CaseInsensitive()
    {
        var r = DateSearchParser.TryParse("JUNE 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 6, 1), r.Range.From);
        Assert.Equal(new DateTime(2023, 6, 30, 23, 59, 59), r.Range.To);
    }

    // ── Year only ─────────────────────────────────────────────────────────────

    [Fact]
    public void YearOnly()
    {
        var r = DateSearchParser.TryParse("2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 59), r.Range.To);
        Assert.Equal("", r.RemainingQuery);
    }

    // ── Year range ────────────────────────────────────────────────────────────

    [Fact]
    public void YearRange_Dash()
    {
        var r = DateSearchParser.TryParse("2023-2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void YearRange_To()
    {
        var r = DateSearchParser.TryParse("2023 to 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void YearRange_Reversed_FallsBackToFirstYear()
    {
        // "2024-2023" — year range rejected (y2 < y1), falls back to bare year "2024".
        var r = DateSearchParser.TryParse("2024-2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 59), r.Range.To);
    }

    // ── Month range ───────────────────────────────────────────────────────────

    [Fact]
    public void MonthRange_Dash()
    {
        var r = DateSearchParser.TryParse("jan 2023 - mar 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 3, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void MonthRange_To()
    {
        var r = DateSearchParser.TryParse("may 2023 to july 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 5, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 7, 31, 23, 59, 59), r.Range.To);
    }

    // ── Season ────────────────────────────────────────────────────────────────

    [Fact]
    public void Season_Summer()
    {
        var r = DateSearchParser.TryParse("summer 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 6, 1), r.Range.From);
        Assert.Equal(new DateTime(2023, 8, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Season_Spring()
    {
        var r = DateSearchParser.TryParse("spring 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 3, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 5, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Season_Fall()
    {
        var r = DateSearchParser.TryParse("fall 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 9, 1), r.Range.From);
        Assert.Equal(new DateTime(2023, 11, 30, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Season_Autumn()
    {
        var r = DateSearchParser.TryParse("autumn 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 9, 1), r.Range.From);
        Assert.Equal(new DateTime(2023, 11, 30, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Season_Winter_SpansYears()
    {
        var r = DateSearchParser.TryParse("winter 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 12, 1), r.Range.From);
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 59), r.Range.To); // 2024 is a leap year
    }

    // ── Events ────────────────────────────────────────────────────────────────

    [Fact]
    public void Event_Christmas()
    {
        var r = DateSearchParser.TryParse("christmas 2022");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2022, 12, 1), r.Range.From);
        Assert.Equal(new DateTime(2022, 12, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Event_Halloween()
    {
        var r = DateSearchParser.TryParse("halloween 2022");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2022, 10, 1), r.Range.From);
        Assert.Equal(new DateTime(2022, 10, 31, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Event_Thanksgiving()
    {
        var r = DateSearchParser.TryParse("thanksgiving 2022");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2022, 11, 1), r.Range.From);
        Assert.Equal(new DateTime(2022, 11, 30, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void Event_NewYear()
    {
        var r = DateSearchParser.TryParse("new year 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 1, 1), r.Range.From);
        Assert.Equal(new DateTime(2023, 1, 31, 23, 59, 59), r.Range.To);
    }

    // ── Month only ────────────────────────────────────────────────────────────

    [Fact]
    public void MonthOnly_SpansAllYears()
    {
        var r = DateSearchParser.TryParse("may");
        Assert.NotNull(r);
        Assert.Equal(5, r.Range.From.Month);
        Assert.Equal(1900, r.Range.From.Year);
        Assert.Equal(5, r.Range.To.Month);
        Assert.Equal(9999, r.Range.To.Year);
        Assert.Equal("", r.RemainingQuery);
    }

    // ── Remaining keywords ────────────────────────────────────────────────────

    [Fact]
    public void DateWithKeyword_ExtractsRemaining()
    {
        var r = DateSearchParser.TryParse("2024 sunset");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 1, 1), r.Range.From);
        Assert.Equal("sunset", r.RemainingQuery);
    }

    [Fact]
    public void MonthYearWithKeyword_ExtractsRemaining()
    {
        var r = DateSearchParser.TryParse("may 2024 beach");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 5, 1), r.Range.From);
        Assert.Equal("beach", r.RemainingQuery);
    }

    [Fact]
    public void KeywordBeforeDate_ExtractsRemaining()
    {
        var r = DateSearchParser.TryParse("beach may 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 5, 1), r.Range.From);
        Assert.Equal("beach", r.RemainingQuery);
    }

    // ── No match ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoDateExpression_ReturnsNull()
    {
        var r = DateSearchParser.TryParse("sunset beach");
        Assert.Null(r);
    }

    [Fact]
    public void EmptyQuery_ReturnsNull()
    {
        var r = DateSearchParser.TryParse("");
        Assert.Null(r);
    }

    [Fact]
    public void NullQuery_ReturnsNull()
    {
        var r = DateSearchParser.TryParse(null!);
        Assert.Null(r);
    }

    // ── Leap year handling ────────────────────────────────────────────────────

    [Fact]
    public void February_LeapYear()
    {
        var r = DateSearchParser.TryParse("february 2024");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 59), r.Range.To);
    }

    [Fact]
    public void February_NonLeapYear()
    {
        var r = DateSearchParser.TryParse("february 2023");
        Assert.NotNull(r);
        Assert.Equal(new DateTime(2023, 2, 28, 23, 59, 59), r.Range.To);
    }
}

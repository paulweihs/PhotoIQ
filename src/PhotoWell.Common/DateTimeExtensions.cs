namespace PhotoWell.Common;

public static class DateTimeExtensions
{
    /// <summary>Returns the canonical holiday tag name for a date, or null if none applies.</summary>
    public static string? GetHolidayName(this DateTime date) => (date.Month, date.Day) switch
    {
        (10, 30) or (10, 31)                      => "halloween",
        (11, _) when date.IsUsaThanksgiving()     => "thanksgiving",
        (12, 24) or (12, 25) or (12, 26)          => "christmas",
        (12, 31) or (1,  1)                       => "new year",
        _                                         => null
    };

    /// <summary>Returns true if the date falls on or within one day of the 4th Thursday of November.</summary>
    public static bool IsUsaThanksgiving(this DateTime date)
    {
        var first     = new DateTime(date.Year, 11, 1);
        var offset    = ((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7;
        var thursday4 = first.AddDays(offset + 21);
        return Math.Abs((date.Date - thursday4).Days) <= 1;
    }
}

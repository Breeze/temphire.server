namespace TempHire.Server.Agent.Usage;

public enum UsagePeriodKind
{
    Day,
    Week,
    Month
}

/// <summary>One quota period: the days it covers, from <see cref="Start"/> up to (not including) <see cref="End"/>.</summary>
public record UsagePeriod(UsagePeriodKind Kind, DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly day) => day >= Start && day < End;

    /// <summary>When the period's quota starts over: midnight UTC at its end.</summary>
    public DateTimeOffset ResetsAt => new(End.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>
/// The day, week and month a moment falls in, in UTC.  Weeks start on Monday (ISO 8601);
/// months are calendar months.
///
/// <para>
/// UTC for everyone keeps the counters simple and the same for a whole company, wherever its
/// users are.  The price is that a user in California sees their "day" start at 4 or 5 pm; a
/// company time zone on the tenant would fix that.
/// </para>
/// </summary>
public static class UsagePeriods
{
    public static IReadOnlyList<UsagePeriod> At(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // DayOfWeek counts from Sunday; ISO weeks start on Monday.
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        return
        [
            new UsagePeriod(UsagePeriodKind.Day, today, today.AddDays(1)),
            new UsagePeriod(UsagePeriodKind.Week, weekStart, weekStart.AddDays(7)),
            new UsagePeriod(UsagePeriodKind.Month, monthStart, monthStart.AddMonths(1))
        ];
    }
}

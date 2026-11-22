using TempHire.Server.Controllers;

namespace TempHire.Server.Agent.Usage;

/// <summary>What Quinn tells a user whose question is refused because a quota is used up.</summary>
public static class QuotaMessages
{
    /// <summary>
    /// Which quota is used up - daily, weekly or monthly, the user's or the company's - and how
    /// long until it starts over: "You've used up your daily Quinn allowance ($0.50).
    /// You can ask again in 6 hours and 41 minutes, when it starts over at midnight UTC."
    /// </summary>
    public static string Refusal(UsagePeriodDto period, bool isCompany, string companyName, DateTimeOffset now)
    {
        var quota = period.Period switch
        {
            "day" => "daily",
            "week" => "weekly",
            _ => "monthly"
        };
        var limit = isCompany ? period.Company.Limit : period.User.Limit;
        var who = isCompany
            ? $"{companyName ?? "Your company"} has used up its {quota} Quinn allowance"
            : $"You've used up your {quota} Quinn allowance";

        return $"{who} ({Dollars(limit)}). You can ask again in {Wait(period.ResetsAt - now)}, " +
               $"when it starts over at midnight UTC{(period.Period == "day" ? "" : $" on {period.ResetsAt:dddd, MMMM d}")}.";
    }

    /// <summary>
    /// A wait in words, as precise as is useful: "3 days and 4 hours", "6 hours and 41 minutes",
    /// "12 minutes".  Rounded up to the minute, so it never promises a moment too early.
    /// </summary>
    public static string Wait(TimeSpan wait)
    {
        var minutes = Math.Max(1, (long)Math.Ceiling(wait.TotalMinutes));
        var days = minutes / (24 * 60);
        var hours = minutes / 60 % 24;
        var mins = minutes % 60;

        if (days > 0)
        {
            return hours > 0 ? $"{Count(days, "day")} and {Count(hours, "hour")}" : Count(days, "day");
        }

        if (hours > 0)
        {
            return mins > 0 ? $"{Count(hours, "hour")} and {Count(mins, "minute")}" : Count(hours, "hour");
        }

        return Count(mins, "minute");
    }

    private static string Count(long n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    /// <summary>US dollars, whatever the server's culture: "$0.50", "$10.00".</summary>
    public static string Dollars(decimal? amount) =>
        "$" + (amount ?? 0).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}

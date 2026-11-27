namespace FFXIVMomoTalk.Chat;

internal readonly record struct FriendDuration(int Years, int Months, int Days)
{
    public bool IsZero => Years == 0 && Months == 0 && Days == 0;
}

internal readonly record struct UpcomingAnniversary(int Years, int DaysRemaining, DateOnly AnniversaryDate)
{
    public bool IsToday => DaysRemaining == 0;
}

internal static class FriendDates
{
    public const int UpcomingAnniversaryWindowDays = 7;

    public static DateOnly ToLocalDate(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().DateTime);

    public static FriendDuration CalculateDuration(DateOnly startDate, DateOnly today)
    {
        if (today <= startDate)
            return new FriendDuration(0, 0, 0);

        var totalMonths = (today.Year - startDate.Year) * 12 + (today.Month - startDate.Month);
        var anchor = startDate.AddMonths(totalMonths);
        if (anchor > today)
        {
            totalMonths--;
            anchor = startDate.AddMonths(totalMonths);
        }

        return new FriendDuration(totalMonths / 12, totalMonths % 12, today.DayNumber - anchor.DayNumber);
    }

    public static UpcomingAnniversary? TryGetUpcomingAnniversary(
        DateOnly addedDate,
        DateOnly today,
        int windowDays = UpcomingAnniversaryWindowDays)
    {
        if (today < addedDate)
            return null;

        var years = Math.Max(1, today.Year - addedDate.Year);
        var anniversaryDate = addedDate.AddYears(years);
        if (anniversaryDate < today)
        {
            years++;
            anniversaryDate = addedDate.AddYears(years);
        }

        var daysRemaining = anniversaryDate.DayNumber - today.DayNumber;
        return daysRemaining <= windowDays ? new UpcomingAnniversary(years, daysRemaining, anniversaryDate) : null;
    }
}

#!/usr/bin/env dotnet
// dotnet run --file tests/FriendDates.cs
#:property PublishAot=false
#:include ../src/FFXIVMomoTalk/Chat/FriendDates.cs

using FFXIVMomoTalk.Chat;

// CalculateDuration checks
CheckDuration(new(2025, 11, 27), new(2025, 11, 27), 0, 0, 0, "same day");
CheckDuration(new(2026, 12, 1), new(2026, 11, 27), 0, 0, 0, "future clock skew");
CheckDuration(new(2026, 11, 20), new(2026, 11, 27), 0, 0, 7, "days only");
CheckDuration(new(2026, 9, 22), new(2026, 11, 27), 0, 2, 5, "months and days");
CheckDuration(new(2026, 8, 27), new(2026, 11, 27), 0, 3, 0, "exact months");
CheckDuration(new(2025, 11, 27), new(2026, 11, 27), 1, 0, 0, "exact year");
CheckDuration(new(2024, 8, 23), new(2026, 11, 27), 2, 3, 4, "years, months, and days");
CheckDuration(new(2025, 11, 22), new(2026, 11, 27), 1, 0, 5, "years and days");
CheckDuration(new(2024, 1, 31), new(2024, 2, 28), 0, 0, 28, "month-end non-leap boundary");
CheckDuration(new(2024, 1, 31), new(2024, 2, 29), 0, 1, 0, "month-end leap boundary");
CheckDuration(new(2024, 2, 29), new(2025, 2, 28), 1, 0, 0, "leap-day anniversary duration");

// TryGetUpcomingAnniversary checks
CheckNoAnniversary(new(2026, 11, 27), new(2026, 11, 27), "added today should not trigger 0th anniversary");
CheckAnniversary(new(2025, 11, 27), new(2026, 11, 27), 1, 0, isToday: true, "exact 1st anniversary today");
CheckAnniversary(new(2023, 12, 4), new(2026, 11, 27), 3, 7, isToday: false, "7 days ahead");
CheckNoAnniversary(new(2023, 12, 5), new(2026, 11, 27), "8 days ahead");
CheckNoAnniversary(new(2025, 11, 26), new(2026, 11, 27), "1 day past anniversary");
CheckAnniversary(new(2024, 1, 3), new(2026, 12, 29), 3, 5, isToday: false, "year-boundary wrap");
CheckAnniversary(new(2025, 1, 2), new(2025, 12, 29), 1, 4, isToday: false, "first year year-boundary wrap");
CheckAnniversary(new(2024, 2, 29), new(2025, 2, 28), 1, 0, isToday: true, "leap-day exact anniversary in non-leap year");
CheckAnniversary(new(2024, 2, 29), new(2025, 2, 21), 1, 7, isToday: false, "leap-day 7 days ahead in non-leap year");
CheckNoAnniversary(new(2027, 1, 1), new(2026, 11, 27), "future added date");

Console.WriteLine("FriendDates duration and upcoming anniversary checks passed.");

static void CheckDuration(DateOnly start, DateOnly today, int years, int months, int days, string label)
{
    var actual = FriendDates.CalculateDuration(start, today);
    if (actual != new FriendDuration(years, months, days))
        throw new InvalidOperationException($"Duration check '{label}' failed: expected ({years},{months},{days}), got {actual}.");
}

static void CheckAnniversary(DateOnly added, DateOnly today, int years, int daysRemaining, bool isToday, string label)
{
    var actual = FriendDates.TryGetUpcomingAnniversary(added, today)
        ?? throw new InvalidOperationException($"Anniversary check '{label}' returned null.");
    if (actual.Years != years || actual.DaysRemaining != daysRemaining || actual.IsToday != isToday)
        throw new InvalidOperationException($"Anniversary check '{label}' failed: expected ({years}y, {daysRemaining}d, today={isToday}), got {actual}.");
}

static void CheckNoAnniversary(DateOnly added, DateOnly today, string label)
{
    var actual = FriendDates.TryGetUpcomingAnniversary(added, today);
    if (actual is not null)
        throw new InvalidOperationException($"Anniversary check '{label}' expected null, got {actual}.");
}

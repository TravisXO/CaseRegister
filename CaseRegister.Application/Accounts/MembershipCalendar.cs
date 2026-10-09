namespace CaseRegister.Application.Accounts;

/// <summary>
/// What "today" means for practising certificates. A certificate runs for a calendar year in Zambia, so
/// the date is the date in Zambia (UTC+2, which has no daylight saving), not in UTC. Otherwise a
/// certificate would live for two hours of 1 January that it should not, and a new one would start two
/// hours late. All date checks take the time from the injected TimeProvider.
/// </summary>
public static class MembershipCalendar
{
    public static readonly TimeSpan ZambiaOffset = TimeSpan.FromHours(2);

    public static DateOnly Today(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return DateOnly.FromDateTime((timeProvider.GetUtcNow() + ZambiaOffset).UtcDateTime);
    }
}

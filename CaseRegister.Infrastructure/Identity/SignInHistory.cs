using CaseRegister.Application.Auth;
using CaseRegister.Application.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Remembers where each full sign-in came from (country and city, never an address) and tells the person when one is somewhere
/// new: a (country, city) pair not seen in their successful sign-ins in the last 90 days. The very first sign-in has nothing to be new
/// against, so it says nothing.
/// </summary>
internal sealed class SignInHistory(AppDbContext db, IClientLocationAccessor location, INotificationService notifications, TimeProvider time) : IAccountSecurity
{
    public static readonly TimeSpan Memory = TimeSpan.FromDays(90);

    public async Task RecordAsync(Guid userId, CancellationToken cancellationToken)
    {
        var place = location.Current;
        var known = place is not null && !string.IsNullOrEmpty(place.City);
        var since = time.GetUtcNow() - Memory;

        var first = !await db.SignInHistory.AnyAsync(entry => entry.UserId == userId, cancellationToken);
        var seen = known && await db.SignInHistory.AnyAsync(
            entry => entry.UserId == userId && entry.Country == place!.Country && entry.City == place.City && entry.CreatedAt > since,
            cancellationToken);

        db.SignInHistory.Add(new SignInHistoryEntry(userId, place?.Country, place?.City));
        await db.SaveChangesAsync(cancellationToken);

        if (known && !first && !seen)
        {
            await notifications.NotifyAsync([NotificationFactory.NewLocation(userId, Guid.CreateVersion7(), place!)], cancellationToken);
        }
    }

    public async Task<AccountSecuritySummary> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // The newest entry is this sign-in; the one before it is the "last" one.
        var previous = (await db.SignInHistory.AsNoTracking()
            .Where(entry => entry.UserId == userId)
            .OrderByDescending(entry => entry.CreatedAt).ThenByDescending(entry => entry.Id)
            .Skip(1).Take(1)
            .ToListAsync(cancellationToken)).SingleOrDefault();

        var remaining = await db.RecoveryCodes.CountAsync(code => code.UserId == userId && code.UsedAt == null && code.RevokedAt == null, cancellationToken);

        return new AccountSecuritySummary(previous is null ? null : new LastSignIn(previous.CreatedAt, previous.Country, previous.City), remaining);
    }
}

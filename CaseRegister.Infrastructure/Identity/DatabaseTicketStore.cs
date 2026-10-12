using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Keeps sign-in sessions in Postgres. The browser's cookie holds only a random session id; the ticket
/// (who the user is, their claims and timings) stays on the server, protected, in the user_sessions table.
/// Removing a row ends that session on the next request, with no waiting for anything to expire.
/// </summary>
internal sealed class DatabaseTicketStore(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider) : ITicketStore
{
    public const string Purpose = "CaseRegister.SessionTicket.v1";

    private const int SessionIdBytes = 32;
    private const int MaxUserAgentLength = 200;

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var userId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = timeProvider.GetUtcNow();

        // 256 random bits. Only the hash goes in the table, so the table alone cannot sign anyone in.
        var sessionId = Base64Url(RandomNumberGenerator.GetBytes(SessionIdBytes));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var session = new UserSession(
            userId,
            Hash(sessionId),
            [],
            ticket.Properties.ExpiresUtc ?? now.Add(TimeSpan.FromMinutes(30)),
            now,
            httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Truncate(httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString(), MaxUserAgentLength));

        // The public id goes into the ticket, so the member can be shown which session is theirs.
        var identity = ticket.Principal.Identities.First();
        foreach (var existing in identity.FindAll(AuthenticationClaims.SessionId).ToList())
        {
            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(AuthenticationClaims.SessionId, session.Id.ToString()));
        session.Ticket = Serialise(ticket);

        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        await EnforceSessionLimitAsync(scope.ServiceProvider, db, userId, now);

        return sessionId;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = Hash(key);

        var session = await db.Sessions.SingleOrDefaultAsync(s => s.SessionHash == hash);
        if (session is null)
        {
            return; // ended in the meantime: renewing must not bring it back
        }

        session.Ticket = Serialise(ticket);
        session.ExpiresAt = ticket.Properties.ExpiresUtc ?? session.ExpiresAt;
        session.LastActiveAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync();
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = Hash(key);

        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.SessionHash == hash);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAt <= timeProvider.GetUtcNow())
        {
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteDeleteAsync();
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.Ticket));
        }
        catch (CryptographicException)
        {
            // Unreadable (for example protected with a key that has since been lost): treat as not signed in.
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = Hash(key);

        // Gone at once. The row is deleted, not marked.
        await db.Sessions.Where(s => s.SessionHash == hash).ExecuteDeleteAsync();
    }

    // At most three sessions per user: a fourth sign-in ends the oldest. Expired ones are swept at the same time.
    private static async Task EnforceSessionLimitAsync(IServiceProvider services, AppDbContext db, Guid userId, DateTimeOffset now)
    {
        await db.Sessions.Where(s => s.UserId == userId && s.ExpiresAt <= now).ExecuteDeleteAsync();

        var excess = await db.Sessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Skip(SessionPolicy.MaxActiveSessions)
            .Select(s => s.Id)
            .ToListAsync();

        if (excess.Count == 0)
        {
            return;
        }

        await db.Sessions.Where(s => excess.Contains(s.Id)).ExecuteDeleteAsync();

        var audit = services.GetRequiredService<IAuditLog>();
        foreach (var sessionId in excess)
        {
            await audit.WriteAsync(new AuditEntry(
                AuditAction.SessionRevoked,
                userId,
                Subject: sessionId.ToString(),
                Detail: $"oldest session ended: limit of {SessionPolicy.MaxActiveSessions} reached"));
        }
    }

    private byte[] Serialise(AuthenticationTicket ticket) => _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private static string Hash(string sessionId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : new string([.. value.Where(character => !char.IsControl(character)).Take(length)]);
}

internal sealed class SessionService(AppDbContext db, IAuditLog audit, TimeProvider timeProvider) : ISessionService
{
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var sessions = await db.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        return [.. sessions.Select(s => new SessionInfo(s.Id, s.CreatedAt, s.LastActiveAt, s.IpAddress, s.UserAgent, s.Id == currentSessionId))];
    }

    public async Task<EndSessionOutcome> EndAsync(Guid userId, Guid sessionId, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return EndSessionOutcome.NotFound;
        }

        // Resource check: a member can only end their own sessions.
        if (session.UserId != userId)
        {
            return EndSessionOutcome.Forbidden;
        }

        // Ending the session and the entry that says so are one transaction.
        await CaseRegister.Infrastructure.Approvals.Transactions.RunAsync(
            db,
            async () =>
            {
                await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry(AuditAction.SessionRevoked, userId, Subject: sessionId.ToString(), IpAddress: remoteIpAddress, Detail: "ended by the member", TargetType: "Session"),
                    cancellationToken);
            },
            cancellationToken);
        return EndSessionOutcome.Ended;
    }
}

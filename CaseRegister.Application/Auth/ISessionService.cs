namespace CaseRegister.Application.Auth;

/// <summary>One sign-in session, as its owner sees it. The id is a public identifier and cannot be used as a cookie.</summary>
public sealed record SessionInfo(
    Guid Id,
    DateTimeOffset SignedInAt,
    DateTimeOffset LastActiveAt,
    string? IpAddress,
    string? UserAgent,
    bool IsCurrent);

public enum EndSessionOutcome
{
    Ended,
    NotFound,

    /// <summary>The session belongs to someone else. A member may only end their own.</summary>
    Forbidden,
}

public static class SessionPolicy
{
    /// <summary>A fourth sign-in ends the oldest of the three.</summary>
    public const int MaxActiveSessions = 3;
}

/// <summary>A member's own sessions. Admin powers over other people's sessions come later, with maker-checker.</summary>
public interface ISessionService
{
    Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default);

    Task<EndSessionOutcome> EndAsync(Guid userId, Guid sessionId, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

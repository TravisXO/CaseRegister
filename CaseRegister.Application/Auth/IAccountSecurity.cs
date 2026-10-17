namespace CaseRegister.Application.Auth;

/// <summary>The place and time of an earlier successful sign-in. Country and city only.</summary>
public sealed record LastSignIn(DateTimeOffset At, string? Country, string? City);

/// <param name="LastSignIn">The sign-in before this one, or null on the very first.</param>
/// <param name="RecoveryCodesRemaining">Unused, unrevoked recovery codes. Two or fewer is a warning.</param>
public sealed record AccountSecuritySummary(LastSignIn? LastSignIn, int RecoveryCodesRemaining);

public interface IAccountSecurity
{
    Task<AccountSecuritySummary> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}

public static class RecoveryCodeAdvice
{
    /// <summary>At this many left or fewer, the person is told to make new ones.</summary>
    public const int WarnAtOrBelow = 2;
}

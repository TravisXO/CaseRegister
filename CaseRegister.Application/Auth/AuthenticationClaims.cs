namespace CaseRegister.Application.Auth;

/// <summary>Claim names and values the sign-in session carries.</summary>
public static class AuthenticationClaims
{
    /// <summary>"Authentication methods references" (RFC 8176): how the user proved who they are.</summary>
    public const string Amr = "amr";

    /// <summary>A password only. Enough to reach two-factor enrolment, nothing else.</summary>
    public const string AmrPassword = "pwd";

    /// <summary>Password plus a second factor. Required for everything except enrolment.</summary>
    public const string AmrMultiFactor = "mfa";

    /// <summary>The public id of the session (not the cookie value), so a member can tell which one is theirs.</summary>
    public const string SessionId = "cr:sid";

    /// <summary>
    /// A property of the sign-in ticket (not a claim): when the member really signed in. It survives
    /// renewals, so the 8-hour limit counts from the real sign-in however active the session is.
    /// </summary>
    public const string SignedInAtProperty = "cr.signed_in_at";
}

namespace CaseRegister.Application.Auth;

public enum BreachCheckResult
{
    NotBreached,
    Breached,

    /// <summary>The check could not be completed. Callers must treat this as a failure, not a pass.</summary>
    Unavailable,
}

/// <summary>Checks a password against a database of known breaches.</summary>
public interface IBreachedPasswordChecker
{
    Task<BreachCheckResult> CheckAsync(string password, CancellationToken cancellationToken = default);
}

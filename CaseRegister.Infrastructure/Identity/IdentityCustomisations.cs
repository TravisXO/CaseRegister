using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>Usernames and emails are both email addresses, so both use the one email normaliser.</summary>
internal sealed class EmailLookupNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => name is null ? null : EmailNormaliser.Normalise(name);

    public string? NormalizeEmail(string? email) => email is null ? null : EmailNormaliser.Normalise(email);
}

/// <summary>Only Approved accounts may sign in. Pending, Rejected and Suspended ones may not.</summary>
internal sealed class ApprovedAccountConfirmation : IUserConfirmation<ApplicationUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(user.AccountStatus == AccountStatus.Approved);
}

/// <summary>Account-level rules Identity does not know about: normalised email, a name and a membership number.</summary>
internal sealed class AccountUserValidator : IUserValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        var errors = new List<IdentityError>();

        if (user.Email is null || !EmailNormaliser.IsNormalised(user.Email) || !string.Equals(user.UserName, user.Email, StringComparison.Ordinal))
        {
            errors.Add(new IdentityError { Code = "EmailNotNormalised", Description = "The email address must be stored in normalised form and be the username." });
        }

        if (string.IsNullOrWhiteSpace(user.FullName))
        {
            errors.Add(new IdentityError { Code = "FullNameRequired", Description = "A full name is required." });
        }

        // Members have a membership number and a class; association staff have neither. Never half of it.
        if (user.MembershipNumber is null != user.MembershipClass is null)
        {
            errors.Add(new IdentityError
            {
                Code = "MembershipDetailsIncomplete",
                Description = "A membership number and a membership class go together: both, or neither for association staff.",
            });
        }

        if (user.MembershipClass is { } membershipClass && !Enum.IsDefined(membershipClass))
        {
            errors.Add(new IdentityError { Code = "MembershipClassInvalid", Description = "That is not a membership class." });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
    }
}

/// <summary>Stops absurdly long input reaching the slow password hash. Identity has no maximum length of its own.</summary>
internal sealed class PasswordLengthLimitValidator : IPasswordValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password) =>
        Task.FromResult(
            password is { Length: > PasswordPolicy.MaximumLength }
                ? IdentityResult.Failed(new IdentityError
                {
                    Code = "PasswordTooLong",
                    Description = $"The password must be at most {PasswordPolicy.MaximumLength} characters.",
                })
                : IdentityResult.Success);
}

internal sealed class PersonalInfoPasswordValidator(IOptions<MembershipOptions> membership) : IPasswordValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        var rejected = password is not null
            && PasswordPersonalInfoRule.ContainsPersonalInfo(
                password,
                user.Email ?? string.Empty,
                user.FullName,
                user.MembershipNumber,
                membership.Value.DisplayPrefix);

        return Task.FromResult(
            rejected
                ? IdentityResult.Failed(new IdentityError
                {
                    Code = "PasswordContainsPersonalInfo",
                    Description = "The password must not contain your email address, name or membership number.",
                })
                : IdentityResult.Success);
    }
}

/// <summary>
/// Rejects passwords found in known breaches. If the check cannot be completed it fails closed: no
/// password is accepted that has not been checked.
/// </summary>
internal sealed partial class BreachedPasswordValidator(IBreachedPasswordChecker checker, ILogger<BreachedPasswordValidator> logger)
    : IPasswordValidator<ApplicationUser>
{
    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        // Passwords that already fail the length rules are rejected by other validators; there is no
        // point sending those out for checking.
        if (password is null
            || password.Length < manager.Options.Password.RequiredLength
            || password.Length > PasswordPolicy.MaximumLength)
        {
            return IdentityResult.Success;
        }

        switch (await checker.CheckAsync(password))
        {
            case BreachCheckResult.Breached:
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "PasswordBreached",
                    Description = "This password has appeared in a known data breach. Choose a different one.",
                });

            case BreachCheckResult.Unavailable:
                LogCheckUnavailable(logger);
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "PasswordCheckUnavailable",
                    Description = "We couldn't check this password right now. Please try again shortly.",
                });

            default:
                return IdentityResult.Success;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The breached-password check was unavailable, so a password was refused (fail closed).")]
    private static partial void LogCheckUnavailable(ILogger logger);
}

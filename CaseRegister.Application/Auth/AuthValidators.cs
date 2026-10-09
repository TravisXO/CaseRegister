using FluentValidation;

namespace CaseRegister.Application.Auth;

public sealed class SignInCommandValidator : AbstractValidator<SignInCommand>
{
    // The longest email address that can exist (RFC 5321) and a generous cap on the Turnstile token.
    private const int MaxEmailLength = 254;
    private const int MaxTurnstileTokenLength = 2048;

    public SignInCommandValidator()
    {
        RuleFor(command => command.Email).NotEmpty().MaximumLength(MaxEmailLength);

        // No minimum here: a short password is simply a wrong one. The maximum stops huge input
        // reaching the (deliberately slow) password hash.
        RuleFor(command => command.Password).NotEmpty().MaximumLength(PasswordPolicy.MaximumLength);
        RuleFor(command => command.TurnstileToken).MaximumLength(MaxTurnstileTokenLength);
    }
}

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(command => command.CurrentPassword).NotEmpty().MaximumLength(PasswordPolicy.MaximumLength);
        RuleFor(command => command.NewPassword).NotEmpty().MaximumLength(PasswordPolicy.MaximumLength);
    }
}

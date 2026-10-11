using System.Text.Json;
using System.Text.Json.Serialization;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using FluentValidation;

namespace CaseRegister.Application.Approvals;

/// <summary>The payload of a role change: which role.</summary>
public sealed record RolePayload(Role Role);

/// <summary>The payload of a certificate verification: which certificate.</summary>
public sealed record CertificatePayload(Guid CertificateId);

/// <summary>
/// The one place a proposal's payload is written and read. It is canonical JSON (fixed property order,
/// no spaces), so equal proposals have equal text and equal hashes.
/// </summary>
public static class ActionPayloads
{
    public const string Empty = "{}";

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Build(ProposeActionCommand command) => command.Type switch
    {
        PendingActionType.AssignRole or PendingActionType.RemoveRole => Serialize(new RolePayload(command.Role!.Value)),
        PendingActionType.VerifyPractisingCertificate => Serialize(new CertificatePayload(command.CertificateId!.Value)),
        _ => Empty,
    };

    /// <summary>What the proposal says in words, for the admin deciding on it.</summary>
    public static string Describe(PendingActionType type, string payload, string? targetName)
    {
        var who = targetName ?? "the account";

        return type switch
        {
            PendingActionType.AssignRole => $"Give {who} the {Role(payload).Role} role",
            PendingActionType.RemoveRole => $"Take the {Role(payload).Role} role away from {who}",
            PendingActionType.VerifyPractisingCertificate => $"Verify a practising certificate of {who}",
            PendingActionType.ReactivateUser => $"Reactivate the suspended account of {who}",
            PendingActionType.ReactivateAgency => $"Reactivate the suspended agency {who}",
            PendingActionType.ResetTwoFactor => $"Reset two-factor for {who}: remove the authenticator and recovery codes, and end all sessions",
            PendingActionType.SuspendUser => $"Suspend the account of {who} and end all sessions",
            PendingActionType.EndSessions => $"End all sessions of {who}",
            PendingActionType.SuspendAgency => $"Suspend the agency {who}",
            _ => type.ToString(),
        };
    }

    public static RolePayload Role(string payload) => Deserialize<RolePayload>(payload);

    public static CertificatePayload Certificate(string payload) => Deserialize<CertificatePayload>(payload);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, _options) ?? throw new InvalidOperationException("A stored payload could not be read.");
}

public sealed class ProposeActionCommandValidator : AbstractValidator<ProposeActionCommand>
{
    public ProposeActionCommandValidator()
    {
        RuleFor(command => command.Type).IsInEnum();
        RuleFor(command => command.Reason).Must(reason => ApprovalRules.CheckReason(reason) is null)
            .WithMessage($"Give a reason of {ApprovalRules.MinimumReasonLength} to {ApprovalRules.MaximumReasonLength} characters.");

        // What a type needs, and nothing else: an extra parameter would be something the confirmer is not shown.
        RuleFor(command => command).Must(UsesExactlyTheParametersItNeeds)
            .WithName("Parameters")
            .WithMessage("The target and parameters do not fit this type of action.");

        RuleFor(command => command.Role!.Value).IsInEnum().When(command => command.Role is not null);
    }

    private static bool UsesExactlyTheParametersItNeeds(ProposeActionCommand command) => command.Type switch
    {
        PendingActionType.AssignRole or PendingActionType.RemoveRole =>
            command is { TargetUserId: not null, TargetAgencyId: null, Role: not null, CertificateId: null },
        PendingActionType.VerifyPractisingCertificate =>
            command is { TargetUserId: not null, TargetAgencyId: null, Role: null, CertificateId: not null },
        PendingActionType.ReactivateUser or PendingActionType.ResetTwoFactor or PendingActionType.SuspendUser or PendingActionType.EndSessions =>
            command is { TargetUserId: not null, TargetAgencyId: null, Role: null, CertificateId: null },
        PendingActionType.ReactivateAgency or PendingActionType.SuspendAgency =>
            command is { TargetUserId: null, TargetAgencyId: not null, Role: null, CertificateId: null },
        _ => false,
    };
}

public sealed class ConfirmActionCommandValidator : AbstractValidator<ConfirmActionCommand>
{
    private const int HashLength = 64;

    public ConfirmActionCommandValidator()
    {
        RuleFor(command => command.PayloadHash).NotEmpty().MaximumLength(HashLength);
        RuleFor(command => command.TwoFactorCode).NotEmpty().MaximumLength(Auth.TwoFactorCodeRules.MaxLength);
    }
}

public sealed class DecisionReasonCommandValidator : AbstractValidator<DecisionReasonCommand>
{
    public DecisionReasonCommandValidator()
    {
        RuleFor(command => command.Reason).Must(reason => ApprovalRules.CheckReason(reason) is null)
            .WithMessage($"Give a reason of {ApprovalRules.MinimumReasonLength} to {ApprovalRules.MaximumReasonLength} characters.");
    }
}

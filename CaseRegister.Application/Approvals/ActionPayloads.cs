using System.Text.Json;
using System.Text.Json.Serialization;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using FluentValidation;

namespace CaseRegister.Application.Approvals;

/// <summary>The payload of a role change: which role.</summary>
public sealed record RolePayload(Role Role);

/// <summary>The payload of a certificate verification: which certificate.</summary>
public sealed record CertificatePayload(Guid CertificateId);

/// <summary>
/// The payload of an approval: which agency the applicant joins (an existing one, or a new one with this name)
/// and which flags the proposer has acknowledged. Everything here is in the hash the confirmer quotes.
/// </summary>
public sealed record ApproveApplicationPayload(Guid? ExistingAgencyId, string? NewAgencyName, IReadOnlyList<string> AcknowledgedFlags);

/// <summary>The payload of a staff invite: who, and in which role. The email is normalised.</summary>
public sealed record InvitePayload(string Email, string FullName, Role Role);

/// <summary>
/// The one place a proposal's payload is written and read. It is canonical JSON (fixed property order,
/// no spaces), so equal proposals have equal text and equal hashes.
/// </summary>
public static class ActionPayloads
{
    public const string Empty = "{}";

    /// <summary>The roles a staff invite can give. Agents sign up and are approved; they are never invited.</summary>
    public static readonly IReadOnlyList<Domain.Accounts.Role> InvitableRoles = [Domain.Accounts.Role.Admin, Domain.Accounts.Role.CaseReviewer];

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Build(ProposeActionCommand command) => command.Type switch
    {
        PendingActionType.AssignRole or PendingActionType.RemoveRole => Serialize(new RolePayload(command.Role!.Value)),
        PendingActionType.VerifyPractisingCertificate => Serialize(new CertificatePayload(command.CertificateId!.Value)),
        PendingActionType.ApproveApplication => Serialize(new ApproveApplicationPayload(
            command.ExistingAgencyId,
            command.NewAgencyName?.Trim(),
            [.. (command.AcknowledgedFlags ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])),
        PendingActionType.InviteStaff => Serialize(new InvitePayload(
            EmailNormaliser.Normalise(command.InviteEmail!),
            command.InviteFullName!.Trim(),
            command.Role!.Value)),
        _ => Empty,
    };

    /// <summary>What the proposal says in words, for the admin deciding on it.</summary>
    public static string Describe(PendingActionType type, string payload, string? targetName, string? existingAgencyName = null)
    {
        var who = targetName ?? "the account";

        return type switch
        {
            PendingActionType.AssignRole => $"Give {who} the {RoleLabels.For(Role(payload).Role)} role",
            PendingActionType.RemoveRole => $"Take the {RoleLabels.For(Role(payload).Role)} role away from {who}",
            PendingActionType.VerifyPractisingCertificate => $"Verify a practising certificate of {who}",
            PendingActionType.ReactivateUser => $"Reactivate the suspended account of {who}",
            PendingActionType.ReactivateAgency => $"Reactivate the suspended agency {who}",
            PendingActionType.ResetTwoFactor => $"Reset two-factor for {who}: remove the authenticator and recovery codes, end all sessions, and make a one-time code they need to set it up again",
            PendingActionType.SuspendUser => $"Suspend the account of {who} and end all sessions",
            PendingActionType.EndSessions => $"End all sessions of {who}",
            PendingActionType.SuspendAgency => $"Suspend the agency {who}",
            PendingActionType.ApproveApplication => DescribeApproval(who, payload, existingAgencyName),
            PendingActionType.RejectApplication => $"Reject the application of {who}",
            PendingActionType.InviteStaff => DescribeInvite(payload),
            _ => type.ToString(),
        };
    }

    public static RolePayload Role(string payload) => Deserialize<RolePayload>(payload);

    public static CertificatePayload Certificate(string payload) => Deserialize<CertificatePayload>(payload);

    public static ApproveApplicationPayload ApproveApplication(string payload) => Deserialize<ApproveApplicationPayload>(payload);

    public static InvitePayload Invite(string payload) => Deserialize<InvitePayload>(payload);

    private static string DescribeApproval(string who, string payload, string? existingAgencyName)
    {
        var approval = ApproveApplication(payload);
        var agency = approval.NewAgencyName is { } name ? $"a new agency called \"{name}\""
            : existingAgencyName is not null ? $"the existing agency \"{existingAgencyName}\""
            : "an existing agency";
        var flags = approval.AcknowledgedFlags.Count == 0 ? "no flags" : $"flags acknowledged: {string.Join(", ", approval.AcknowledgedFlags)}";

        return $"Approve the application of {who}: join {agency}, verify their certificate, make them a registered agent ({flags})";
    }

    private static string DescribeInvite(string payload)
    {
        var invite = Invite(payload);

        return $"Invite {invite.FullName} ({invite.Email}) as {RoleLabels.For(invite.Role)}. They set a password and two-factor from a link that lasts 24 hours";
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, _options) ?? throw new InvalidOperationException("A stored payload could not be read.");
}

public sealed class ProposeActionCommandValidator : AbstractValidator<ProposeActionCommand>
{
    private const int MaxEmailLength = 254;

    public ProposeActionCommandValidator()
    {
        RuleFor(command => command.Type).IsInEnum();
        RuleFor(command => command.Reason).Must(reason => ApprovalRules.CheckReason(reason) is null)
            .WithMessage($"Give a reason of {ApprovalRules.MinimumReasonLength} to {ApprovalRules.MaximumReasonLength} characters.");

        RuleFor(command => command.Reason).Must(reason => ApprovalRules.CheckIdentityReason(reason) is null)
            .When(command => ApprovalRules.NeedsIdentityCheckReason(command.Type))
            .WithMessage($"Say how you checked the person's identity, in at least {ApprovalRules.MinimumIdentityReasonLength} characters.");

        // What a type needs, and nothing else: an extra parameter would be something the confirmer is not shown.
        RuleFor(command => command).Must(UsesExactlyTheParametersItNeeds)
            .WithName("Parameters")
            .WithMessage("The target and parameters do not fit this type of action.");

        RuleFor(command => command.Role!.Value).IsInEnum().When(command => command.Role is not null);

        RuleFor(command => command.NewAgencyName!).MaximumLength(MemberApplication.MaxAgencyNameLength).When(command => command.NewAgencyName is not null);
        RuleFor(command => command.AcknowledgedFlags!).Must(flags => ApplicationFlagNames.TryParse(flags, out _))
            .When(command => command.AcknowledgedFlags is not null)
            .WithMessage("Those are not application flags.");
        RuleFor(command => command.InviteEmail!).EmailAddress().MaximumLength(MaxEmailLength).When(command => command.InviteEmail is not null);
        RuleFor(command => command.InviteFullName!).MaximumLength(200).When(command => command.InviteFullName is not null);
        RuleFor(command => command.Role!.Value)
            .Must(role => ActionPayloads.InvitableRoles.Contains(role))
            .When(command => command.Type == PendingActionType.InviteStaff && command.Role is not null)
            .WithMessage("Only an Admin or a CaseReviewer can be invited.");
    }

    private static bool UsesExactlyTheParametersItNeeds(ProposeActionCommand command)
    {
        var noApprovalFields = command is { ExistingAgencyId: null, NewAgencyName: null, InviteEmail: null, InviteFullName: null }
            && (command.AcknowledgedFlags is null || command.AcknowledgedFlags.Count == 0);

        return command.Type switch
        {
            PendingActionType.AssignRole or PendingActionType.RemoveRole =>
                command is { TargetUserId: not null, TargetAgencyId: null, Role: not null, CertificateId: null } && noApprovalFields,
            PendingActionType.VerifyPractisingCertificate =>
                command is { TargetUserId: not null, TargetAgencyId: null, Role: null, CertificateId: not null } && noApprovalFields,
            PendingActionType.ReactivateUser or PendingActionType.ResetTwoFactor or PendingActionType.SuspendUser
                or PendingActionType.EndSessions or PendingActionType.RejectApplication =>
                command is { TargetUserId: not null, TargetAgencyId: null, Role: null, CertificateId: null } && noApprovalFields,
            PendingActionType.ReactivateAgency or PendingActionType.SuspendAgency =>
                command is { TargetUserId: null, TargetAgencyId: not null, Role: null, CertificateId: null } && noApprovalFields,

            // Exactly one of: an existing agency, or the name of a new one.
            PendingActionType.ApproveApplication =>
                command is { TargetUserId: not null, TargetAgencyId: null, Role: null, CertificateId: null, InviteEmail: null, InviteFullName: null }
                && (command.ExistingAgencyId is not null) != !string.IsNullOrWhiteSpace(command.NewAgencyName)
                && (command.NewAgencyName is null || !string.IsNullOrWhiteSpace(command.NewAgencyName)),
            PendingActionType.InviteStaff =>
                command is { TargetUserId: null, TargetAgencyId: null, Role: not null, CertificateId: null, ExistingAgencyId: null, NewAgencyName: null }
                && (command.AcknowledgedFlags is null || command.AcknowledgedFlags.Count == 0)
                && !string.IsNullOrWhiteSpace(command.InviteEmail)
                && !string.IsNullOrWhiteSpace(command.InviteFullName),
            _ => false,
        };
    }
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

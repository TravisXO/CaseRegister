using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using CaseRegister.Application.Applications;
using CaseRegister.Domain.Accounts;

namespace CaseRegister.Infrastructure.Approvals;

/// <summary>
/// The maker-checker engine. Every rule lives in <see cref="ApprovalRules"/> and the operations; this
/// class only puts them in order: check who and what, check the current state, ask for a fresh
/// two-factor code, then flip the proposal and apply the action in one transaction.
/// </summary>
internal sealed partial class PendingActionService(
    AppDbContext db,
    IEnumerable<IAdminOperation> operations,
    ITwoFactorService twoFactor,
    IAuditLog audit,
    TimeProvider time,
    IOptions<StaffInviteOptions> invites,
    NotificationDispatcher notifications,
    ILogger<PendingActionService> logger) : IPendingActionService
{
    private const string OnePendingIndex = "ux_pending_actions_one_pending_per_target";

    private readonly Dictionary<PendingActionType, IAdminOperation> _operations = operations.ToDictionary(operation => operation.Type);

    private sealed record Actor(Guid Id, Guid? AgencyId);

    private sealed record Attempt(ConfirmOutcome Outcome, IReadOnlyList<ApprovalViolation>? Problems = null, bool Repeat = false, string? SetupToken = null, IReadOnlyList<NotificationIntent>? Intents = null, string? EnrolmentCode = null);

    // --- Proposing ---------------------------------------------------------------------------------

    public async Task<ProposeResult> ProposeAsync(Guid proposerId, ProposeActionCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (await ActorAsync(proposerId, cancellationToken) is not { } proposer)
        {
            return new ProposeResult(ProposeOutcome.NotAnAdmin);
        }

        var validation = new ProposeActionCommandValidator().Validate(command);
        if (!validation.IsValid)
        {
            return new ProposeResult(ProposeOutcome.Invalid, Problems: [.. validation.Errors.Select(error => new ApprovalViolation("Invalid", error.ErrorMessage))]);
        }

        var self = ApprovalRules.CheckProposal(proposer.Id, proposer.AgencyId, command.TargetUserId, command.TargetAgencyId);
        if (self.Count > 0)
        {
            return new ProposeResult(ProposeOutcome.Refused, Problems: self);
        }

        if (!await TargetExistsAsync(command.TargetUserId, command.TargetAgencyId, cancellationToken))
        {
            return new ProposeResult(ProposeOutcome.TargetNotFound);
        }

        // Granting needs two admins whoever the target is; taking away needs two only when the target is an admin.
        var targetIsAdmin = command.TargetUserId is { } userId && await AdminCensus.HoldsAdminRoleAsync(db, userId, cancellationToken);
        if (!ApprovalRules.RequiresTwoAdmins(command.Type, targetIsAdmin)
            && !(command.Type != PendingActionType.EndSessions && await ImmediateActionLimit.ReachedAsync(db, proposer.Id, time.GetUtcNow(), cancellationToken)))
        {
            return new ProposeResult(ProposeOutcome.NoApprovalNeeded);
        }

        // Only a proposal that could be confirmed right now is accepted. The same checks run again at confirmation.
        var payload = ActionPayloads.Build(command);
        var targetEmail = command.Type == PendingActionType.InviteStaff ? EmailNormaliser.Normalise(command.InviteEmail!) : null;
        var plan = await _operations[command.Type].PlanAsync(new OperationContext(proposer.Id, command.TargetUserId, command.TargetAgencyId, payload, targetEmail, command.Reason), cancellationToken);
        if (plan.Problems.Count > 0)
        {
            return new ProposeResult(ProposeOutcome.Refused, Problems: plan.Problems);
        }

        var now = time.GetUtcNow();
        var action = PendingAction.Propose(command.Type, command.TargetUserId, command.TargetAgencyId, payload, command.Reason, proposer.Id, now, targetEmail);
        var outcome = ProposeOutcome.Proposed;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteVerifiedAsync(db, async () =>
        {
            outcome = ProposeOutcome.Proposed;
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // A proposal whose 72 hours ran out no longer counts as pending, so it does not block a new one.
            var existing = await db.PendingActions.SingleOrDefaultAsync(
                other => other.Type == command.Type
                    && other.TargetUserId == command.TargetUserId
                    && other.TargetAgencyId == command.TargetAgencyId
                    && other.TargetEmail == targetEmail
                    && other.Status == PendingActionStatus.Pending,
                cancellationToken);

            if (existing is not null)
            {
                if (existing.EffectiveStatus(now) != PendingActionStatus.Expired)
                {
                    outcome = ProposeOutcome.AlreadyPending;
                    return;
                }

                existing.Expire(now);
                await db.SaveChangesAsync(cancellationToken);
                await WriteExpiredAsync(existing, cancellationToken);
            }

            db.PendingActions.Add(action);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { ConstraintName: OnePendingIndex })
            {
                // Two admins proposed the same thing at the same moment: the database lets only one through.
                outcome = ProposeOutcome.AlreadyPending;
                return;
            }

            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.ActionProposed,
                    proposer.Id,
                    Subject: TargetOf(action),
                    IpAddress: remoteIpAddress,
                    Detail: AuditText.Fit($"{action.Id}; {action.Type}; before: {plan.Before}; after: {plan.After}; hash {action.PayloadHash[..12]}; reason: {action.Reason}"), TargetType: TargetTypeOf(action), Before: new { state = plan.Before }, After: new { state = plan.After }),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });

        if (outcome == ProposeOutcome.Proposed)
        {
            // After the commit: the other admins who can decide it are told it is waiting.
            await notifications.ProposalMadeAsync(action.Id, action.Type, proposer.Id, action.TargetUserId, cancellationToken);

            return new ProposeResult(outcome, await ViewAsync(action.Id, cancellationToken));
        }

        return new ProposeResult(outcome);
    }

    // --- Reading -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PendingActionView>> ListAsync(bool includeClosed, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var query = db.PendingActions.AsNoTracking();
        if (!includeClosed)
        {
            query = query.Where(action => action.Status == PendingActionStatus.Pending);
        }

        var rows = await query.OrderByDescending(action => action.ProposedAt).ToListAsync(cancellationToken);

        // Expiry is worked out here, from the time: a pending row past its 72 hours is shown as expired and,
        // unless the closed ones were asked for, left out.
        var shown = includeClosed ? rows : rows.Where(action => action.EffectiveStatus(now) == PendingActionStatus.Pending).ToList();

        return await ToViewsAsync(shown, now, cancellationToken);
    }

    public async Task<PendingActionView?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ViewAsync(id, cancellationToken);

    // --- Confirming --------------------------------------------------------------------------------

    public async Task<ConfirmResult> ConfirmAsync(Guid id, Guid confirmerId, ConfirmActionCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (await ActorAsync(confirmerId, cancellationToken) is not { } confirmer)
        {
            return new ConfirmResult(ConfirmOutcome.NotAnAdmin);
        }

        var action = await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null)
        {
            return new ConfirmResult(ConfirmOutcome.NotFound);
        }

        // A repeat of a confirmation that already went through gets the same answer and does nothing more.
        if (IsRepeat(action, confirmer.Id, command.PayloadHash))
        {
            return new ConfirmResult(ConfirmOutcome.Confirmed, await ViewAsync(id, cancellationToken), WasRepeat: true);
        }

        // The confirmer approves exactly what they saw, or nothing.
        if (!action.HashMatches(command.PayloadHash))
        {
            return new ConfirmResult(ConfirmOutcome.HashMismatch);
        }

        var now = time.GetUtcNow();
        var people = ApprovalRules.CheckDecision(action, confirmer.Id, confirmer.AgencyId, now);
        if (people.Count > 0)
        {
            return await RefusedForPeopleAsync(action, people, cancellationToken);
        }

        // The state of things now, checked before a code is spent on it.
        var (stateProblems, plan) = await CheckStateAsync(action, confirmer.Id, new OperationOutput(), cancellationToken);
        if (plan is null)
        {
            // The state may only look wrong because another confirmation went through since the proposal was read above.
            // Whether the proposal is still pending is settled first: only if it is, the state is the reason.
            if (await NoLongerOpenAsync(id, confirmer.Id, command.PayloadHash, cancellationToken) is { } settled)
            {
                return settled;
            }

            return await RecordRefusalAsync(id, confirmer.Id, stateProblems, remoteIpAddress, cancellationToken);
        }

        // A signed-in session is not enough: a fresh code, which also proves the confirmer is the one at the keyboard.
        var step = await twoFactor.VerifyCodeAsync(confirmer.Id, command.TwoFactorCode, remoteIpAddress, cancellationToken);
        if (step.Outcome == TwoFactorOutcome.RateLimited)
        {
            return new ConfirmResult(ConfirmOutcome.RateLimited, RetryAfter: step.RetryAfter);
        }

        if (step.Outcome != TwoFactorOutcome.Succeeded)
        {
            return new ConfirmResult(ConfirmOutcome.CodeRejected);
        }

        var attempt = await ApplyAsync(action, confirmer, command.PayloadHash, remoteIpAddress, cancellationToken);

        if (attempt is { Outcome: ConfirmOutcome.Confirmed, Repeat: false })
        {
            // Committed. Now the proposer is told, and so is anyone the action touched (the person whose roles changed, an agency's members).
            await notifications.ProposalConfirmedAsync(id, action.Type, action.ProposedByUserId, attempt.Intents ?? [], cancellationToken);
        }

        return attempt.Outcome switch
        {
            ConfirmOutcome.Confirmed => new ConfirmResult(
                ConfirmOutcome.Confirmed,
                await ViewAsync(id, cancellationToken),
                WasRepeat: attempt.Repeat,
                SetupLink: attempt.SetupToken is { } setupToken ? invites.Value.LinkFor(setupToken) : null,
                EnrolmentCode: attempt.EnrolmentCode),
            ConfirmOutcome.Refused => await RecordRefusalAsync(id, confirmer.Id, attempt.Problems!, remoteIpAddress, cancellationToken),
            ConfirmOutcome.Expired or ConfirmOutcome.NotAllowed or ConfirmOutcome.NotPending =>
                await RefusedForPeopleAsync(action, attempt.Problems ?? [], cancellationToken, attempt.Outcome),
            _ => new ConfirmResult(attempt.Outcome, Problems: attempt.Problems),
        };
    }

    /// <summary>
    /// The decision itself, in one transaction: flip the proposal (the optimistic concurrency check that lets
    /// only one of two simultaneous confirmations through), then apply the action, then audit, then commit.
    /// </summary>
    private async Task<Attempt> ApplyAsync(PendingAction seen, Actor confirmer, string quotedHash, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        Attempt? result = null;
        var output = new OperationOutput();
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteVerifiedAsync(db, async () =>
            {
                result = null;
                output = new OperationOutput();
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                // The proposal row is locked before anything else is looked at, so two confirmations of it go one after the other,
                // and the second one reads what the first one left. Everything below happens in this order, every time:
                // is it still open (or already confirmed by this same admin), then the rules about people, then the state of
                // things, and only then the decision. A confirmation that lost the race is therefore always "not pending",
                // never a refusal about a state the winner changed.
                await LockProposalAsync(seen.Id, cancellationToken);
                var action = await db.PendingActions.SingleAsync(a => a.Id == seen.Id, cancellationToken);
                var now = time.GetUtcNow();

                if (IsRepeat(action, confirmer.Id, quotedHash))
                {
                    result = new Attempt(ConfirmOutcome.Confirmed, Repeat: true);
                    return;
                }

                var open = ApprovalRules.CheckOpen(action, now);
                if (open.Count > 0)
                {
                    result = new Attempt(ClassifyPeopleProblems(open), open);
                    return;
                }

                // Anything that can lower the number of admins goes one at a time, so two cannot both pass the count.
                if (ApprovalRules.CanReduceAdmins(seen.Type))
                {
                    await AdminCensus.LockAsync(db, cancellationToken);
                }

                var people = ApprovalRules.CheckDecision(action, confirmer.Id, confirmer.AgencyId, now);
                if (people.Count > 0)
                {
                    result = new Attempt(ClassifyPeopleProblems(people), people);
                    return;
                }

                // Checked again under the lock, against the state as it is at this moment.
                var (stateProblems, plan) = await CheckStateAsync(action, confirmer.Id, output, cancellationToken);
                if (plan is null)
                {
                    result = new Attempt(ConfirmOutcome.Refused, stateProblems);
                    return;
                }

                action.Confirm(confirmer.Id, now);
                await db.SaveChangesAsync(cancellationToken); // fails if anyone else decided it since we read it

                await plan.ApplyAsync(cancellationToken);

                await audit.WriteAsync(
                    new AuditEntry(
                        AuditAction.ActionConfirmed,
                        confirmer.Id,
                        Subject: TargetOf(action),
                        IpAddress: remoteIpAddress,
                        Detail: AuditText.Fit($"{action.Id}; {action.Type}; proposed by {action.ProposedByUserId}; before: {plan.Before}; after: {plan.After}"), TargetType: TargetTypeOf(action), Before: new { state = plan.Before }, After: new { state = plan.After }),
                    cancellationToken);

                await audit.WriteAsync(
                    new AuditEntry(
                        plan.Effect,
                        confirmer.Id,
                        Subject: plan.EffectSubject ?? TargetOf(action),
                        IpAddress: remoteIpAddress,
                        Detail: AuditText.Fit($"approved action {action.Id}; before: {plan.Before}; after: {plan.After}"), Before: new { state = plan.Before }, After: new { state = plan.After }),
                    cancellationToken);

                result = new Attempt(ConfirmOutcome.Confirmed, SetupToken: output.SetupToken, Intents: [.. output.Intents], EnrolmentCode: output.EnrolmentCode);
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody changed the proposal (or the thing it applies to) at the same moment. Nothing of ours was kept.
            db.ChangeTracker.Clear();
            var fresh = await db.PendingActions.AsNoTracking().SingleAsync(a => a.Id == seen.Id, cancellationToken);

            result = IsRepeat(fresh, confirmer.Id, quotedHash)
                ? new Attempt(ConfirmOutcome.Confirmed, Repeat: true)
                : fresh.Status == PendingActionStatus.Pending
                    ? new Attempt(ConfirmOutcome.Refused, [new ApprovalViolation("ChangedMeanwhile", "Something changed while this was being applied. Nothing was done; check it and confirm again.")])
                    : new Attempt(ConfirmOutcome.NotPending, [new ApprovalViolation(ApprovalRules.NotPending, $"This proposal is already {fresh.Status.ToString().ToLowerInvariant()}.")]);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            // A part of the action failed (for example a rule in the database). The transaction was rolled back,
            // so nothing of it was kept, and the proposal is still pending.
            LogApplyFailed(logger, seen.Id, seen.Type, exception);
            db.ChangeTracker.Clear();
            result = new Attempt(ConfirmOutcome.Refused, [new ApprovalViolation("ApplyFailed", "Part of this action could not be carried out, so none of it was. Nothing was changed.")]);
        }

        return result!;
    }

    /// <summary>Waits for any other transaction that has this proposal locked, then holds the lock until this one ends.</summary>
    private async Task LockProposalAsync(Guid id, CancellationToken cancellationToken) =>
        _ = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM pending_actions WHERE id = {id} FOR UPDATE").ToListAsync(cancellationToken);

    /// <summary>
    /// A confirmation was about to be refused for the state of things. If the proposal is in fact no longer open (another admin
    /// confirmed it, or it was rejected, withdrawn or expired) or this same admin's confirmation already went through, that is
    /// the answer: the state only looks wrong because of the decision. Null if the proposal is still open.
    /// </summary>
    private async Task<ConfirmResult?> NoLongerOpenAsync(Guid id, Guid confirmerId, string quotedHash, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var fresh = await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (fresh is null)
        {
            return new ConfirmResult(ConfirmOutcome.NotFound);
        }

        if (IsRepeat(fresh, confirmerId, quotedHash))
        {
            return new ConfirmResult(ConfirmOutcome.Confirmed, await ViewAsync(id, cancellationToken), WasRepeat: true);
        }

        var open = ApprovalRules.CheckOpen(fresh, time.GetUtcNow());

        return open.Count == 0 ? null : await RefusedForPeopleAsync(fresh, open, cancellationToken);
    }

    // --- Rejecting and withdrawing -----------------------------------------------------------------

    public Task<DecisionResult> RejectAsync(Guid id, Guid rejecterId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        CloseAsync(id, rejecterId, reason, remoteIpAddress, withdraw: false, cancellationToken);

    public Task<DecisionResult> WithdrawAsync(Guid id, Guid proposerId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        CloseAsync(id, proposerId, reason, remoteIpAddress, withdraw: true, cancellationToken);

    private async Task<DecisionResult> CloseAsync(Guid id, Guid actorId, string reason, string? remoteIpAddress, bool withdraw, CancellationToken cancellationToken)
    {
        if (await ActorAsync(actorId, cancellationToken) is not { } actor)
        {
            return new DecisionResult(DecisionOutcome.NotAnAdmin);
        }

        if (ApprovalRules.CheckReason(reason) is { } badReason)
        {
            return new DecisionResult(DecisionOutcome.Invalid, Problems: [badReason]);
        }

        db.ChangeTracker.Clear();
        var action = await db.PendingActions.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null)
        {
            return new DecisionResult(DecisionOutcome.NotFound);
        }

        var now = time.GetUtcNow();

        // Withdrawing is the proposer's own act; rejecting is a different admin's, and never about themselves.
        List<ApprovalViolation> problems = [.. withdraw ? ApprovalRules.CheckOpen(action, now) : ApprovalRules.CheckDecision(action, actor.Id, actor.AgencyId, now)];
        if (withdraw && actor.Id != action.ProposedByUserId)
        {
            problems.Add(new ApprovalViolation("NotProposer", "Only the admin who proposed this can withdraw it."));
        }

        if (problems.Count > 0)
        {
            var outcome = ClassifyPeopleProblems(problems) switch
            {
                ConfirmOutcome.Expired => DecisionOutcome.Expired,
                ConfirmOutcome.NotPending => DecisionOutcome.NotPending,
                _ => DecisionOutcome.NotAllowed,
            };

            if (outcome == DecisionOutcome.Expired)
            {
                await PersistExpiryAsync(id, cancellationToken);
            }

            return new DecisionResult(outcome, Problems: problems);
        }

        var before = $"status: {action.Status}";
        if (withdraw)
        {
            action.Withdraw(actor.Id, now, reason);
        }
        else
        {
            action.Reject(actor.Id, now, reason);
        }

        try
        {
            // The decision and its entry are one transaction: neither exists without the other.
            await Transactions.RunAsync(
                db,
                async () =>
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.WriteAsync(
                        new AuditEntry(
                            withdraw ? AuditAction.ActionWithdrawn : AuditAction.ActionRejected,
                            actor.Id,
                            Subject: TargetOf(action),
                            IpAddress: remoteIpAddress,
                            Detail: AuditText.Fit($"{action.Id}; {action.Type}; {before} -> status: {action.Status}; nothing was applied; reason: {action.DecisionReason}"),
                            TargetType: TargetTypeOf(action),
                            Before: new { status = before },
                            After: new { status = action.Status.ToString() }),
                        cancellationToken);
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new DecisionResult(DecisionOutcome.NotPending, Problems: [new ApprovalViolation(ApprovalRules.NotPending, "This proposal was decided at the same moment.")]);
        }

        if (!withdraw)
        {
            // Rejected (committed): the admin who proposed it is told. A withdrawal is their own act, so there is nobody to tell.
            await notifications.ProposalRejectedAsync(id, action.Type, action.ProposedByUserId, cancellationToken);
        }

        return new DecisionResult(DecisionOutcome.Done, await ViewAsync(id, cancellationToken));
    }

    // --- Helpers -----------------------------------------------------------------------------------

    /// <summary>
    /// Everything about the current state that must hold for a confirmation to go ahead: the proposal is
    /// what was proposed, the people involved are still active admins, and the operation itself is still
    /// possible (role rules, the target's status, the two-admin minimum). Every problem is reported, not just the first.
    /// The plan is null if there is any problem.
    /// </summary>
    private async Task<(IReadOnlyList<ApprovalViolation> Problems, OperationPlan? Plan)> CheckStateAsync(PendingAction action, Guid confirmerId, OperationOutput output, CancellationToken cancellationToken)
    {
        // Stored hash against the stored fields. If someone changed the proposal behind the application's back, nothing is applied.
        if (!action.IsIntact)
        {
            return ([new ApprovalViolation("ProposalTampered", "This proposal no longer matches its hash, so it will not be applied.")], null);
        }

        List<ApprovalViolation> problems = [];

        if (!await AdminCensus.IsActiveAdminAsync(db, action.ProposedByUserId, cancellationToken))
        {
            problems.Add(new ApprovalViolation("ProposerNoLongerAdmin", "The admin who proposed this is no longer an active admin."));
        }

        if (!await AdminCensus.IsActiveAdminAsync(db, confirmerId, cancellationToken))
        {
            problems.Add(new ApprovalViolation("ConfirmerNoLongerAdmin", "You are no longer an active admin."));
        }

        var plan = await _operations[action.Type].PlanAsync(ContextOf(action, confirmerId, output), cancellationToken);
        problems.AddRange(plan.Problems);

        return (problems, problems.Count == 0 ? plan : null);
    }

    private static bool IsRepeat(PendingAction action, Guid confirmerId, string quotedHash) =>
        action.Status == PendingActionStatus.Confirmed && action.DecidedByUserId == confirmerId && action.HashMatches(quotedHash);

    private static ConfirmOutcome ClassifyPeopleProblems(IReadOnlyList<ApprovalViolation> problems) =>
        problems.Any(problem => problem.Code == ApprovalRules.Expired) ? ConfirmOutcome.Expired
        : problems.Any(problem => problem.Code == ApprovalRules.NotPending) ? ConfirmOutcome.NotPending
        : ConfirmOutcome.NotAllowed;

    private async Task<ConfirmResult> RefusedForPeopleAsync(
        PendingAction action,
        IReadOnlyList<ApprovalViolation> problems,
        CancellationToken cancellationToken,
        ConfirmOutcome? outcome = null)
    {
        var kind = outcome ?? ClassifyPeopleProblems(problems);
        if (kind == ConfirmOutcome.Expired)
        {
            await PersistExpiryAsync(action.Id, cancellationToken);
        }

        return new ConfirmResult(kind, Problems: problems);
    }

    /// <summary>Leaves the proposal pending, and writes down why this confirmation was refused.</summary>
    private async Task<ConfirmResult> RecordRefusalAsync(Guid id, Guid byUserId, IReadOnlyList<ApprovalViolation> problems, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var action = await db.PendingActions.SingleAsync(a => a.Id == id, cancellationToken);

        // The note on the proposal and the entry that records the refusal are one transaction.
        await Transactions.RunAsync(
            db,
            async () =>
            {
                if (action.Status == PendingActionStatus.Pending)
                {
                    action.RecordRefusal(problems[0], time.GetUtcNow());

                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        db.ChangeTracker.Clear(); // decided at the same moment; the refusal is still reported below
                    }
                }

                await audit.WriteAsync(
                    new AuditEntry(
                        AuditAction.ActionRefused,
                        byUserId,
                        Subject: TargetOf(action),
                        IpAddress: remoteIpAddress,
                        Detail: AuditText.Fit($"{action.Id}; {action.Type}; confirmation refused, still pending: {string.Join(", ", problems.Select(problem => problem.Code))}"),
                        TargetType: TargetTypeOf(action)),
                    cancellationToken);
            },
            cancellationToken);

        return new ConfirmResult(ConfirmOutcome.Refused, await ViewAsync(id, cancellationToken), problems);
    }

    /// <summary>Writes down that a proposal's time ran out.</summary>
    private async Task PersistExpiryAsync(Guid id, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var action = await db.PendingActions.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (action is null || action.Status != PendingActionStatus.Pending)
        {
            return;
        }

        action.Expire(time.GetUtcNow());

        try
        {
            await Transactions.RunAsync(
                db,
                async () =>
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await WriteExpiredAsync(action, cancellationToken);
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // someone else recorded it first
        }
    }

    private Task WriteExpiredAsync(PendingAction action, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry(
                AuditAction.ActionExpired,
                ActorUserId: null,
                Subject: TargetOf(action),
                Detail: $"{action.Id}; {action.Type}; not confirmed within {ApprovalRules.Lifetime.TotalHours:0} hours; nothing was applied", TargetType: TargetTypeOf(action)),
            cancellationToken);

    private static OperationContext ContextOf(PendingAction action, Guid actorId, OperationOutput output) =>
        new(actorId, action.TargetUserId, action.TargetAgencyId, action.Payload, action.TargetEmail, action.Reason) { Output = output, EventId = action.Id };

    // An invite has no account yet, so it is named by a label of its email address, never the address itself.
    private static string TargetTypeOf(PendingAction action) => action.TargetUserId is not null ? "User" : action.TargetAgencyId is not null ? "Agency" : "Email";

    private static string TargetOf(PendingAction action) =>
        (action.TargetUserId ?? action.TargetAgencyId)?.ToString() ?? AuditSubject.ForAttemptedEmail(action.TargetEmail);

    private async Task<Actor?> ActorAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await AdminCensus.IsActiveAdminAsync(db, userId, cancellationToken))
        {
            return null;
        }

        var agencyId = await db.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.AgencyId).SingleAsync(cancellationToken);

        return new Actor(userId, agencyId);
    }

    private async Task<bool> TargetExistsAsync(Guid? userId, Guid? agencyId, CancellationToken cancellationToken) =>
        userId is { } user
            ? await db.Users.AnyAsync(u => u.Id == user, cancellationToken)
            : agencyId is null || await db.Agencies.AnyAsync(a => a.Id == agencyId, cancellationToken); // an invite targets an email address, which has no account yet

    private async Task<PendingActionView?> ViewAsync(Guid id, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var action = await db.PendingActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

        return action is null ? null : (await ToViewsAsync([action], time.GetUtcNow(), cancellationToken))[0];
    }

    private async Task<IReadOnlyList<PendingActionView>> ToViewsAsync(IReadOnlyList<PendingAction> actions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var userIds = actions
            .SelectMany(action => new[] { action.TargetUserId, action.ProposedByUserId, action.DecidedByUserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var agencyIds = actions.Select(action => action.TargetAgencyId).OfType<Guid>().ToList();

        // An approval names the agency the applicant joins, so the admin deciding is shown its name and not just that it exists.
        agencyIds.AddRange(actions.Where(action => action.Type == PendingActionType.ApproveApplication).Select(action => ActionPayloads.ApproveApplication(action.Payload).ExistingAgencyId).OfType<Guid>());
        agencyIds = [.. agencyIds.Distinct()];

        var userNames = await db.Users.AsNoTracking().Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        var agencyNames = await db.Agencies.AsNoTracking().Where(agency => agencyIds.Contains(agency.Id)).ToDictionaryAsync(agency => agency.Id, agency => agency.Name, cancellationToken);

        return
        [
            .. actions.Select(action =>
            {
                var targetName = action.TargetUserId is { } user ? userNames.GetValueOrDefault(user)
                    : action.TargetAgencyId is { } agency ? agencyNames.GetValueOrDefault(agency)
                    : ActionPayloads.Invite(action.Payload).FullName; // an invite: the person has no account yet

                return new PendingActionView(
                    action.Id,
                    action.Type,
                    action.TargetUserId,
                    action.TargetAgencyId,
                    targetName,
                    ActionPayloads.Describe(action.Type, action.Payload, targetName, action.Type == PendingActionType.ApproveApplication && ActionPayloads.ApproveApplication(action.Payload).ExistingAgencyId is { } existing ? agencyNames.GetValueOrDefault(existing) : null),
                    action.Payload,
                    action.PayloadHash,
                    action.Reason,
                    action.EffectiveStatus(now),
                    action.ProposedByUserId,
                    userNames.GetValueOrDefault(action.ProposedByUserId),
                    action.ProposedAt,
                    action.ExpiresAt,
                    action.DecidedByUserId,
                    action.DecidedAt,
                    action.DecisionReason,
                    action.LastRefusalCode,
                    action.LastRefusalMessage,
                    action.LastRefusedAt,
                    action.TargetEmail);
            }),
        ];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Confirming proposal {ProposalId} ({Type}) failed while it was being applied, and was rolled back.")]
    private static partial void LogApplyFailed(ILogger logger, Guid proposalId, PendingActionType type, Exception exception);
}

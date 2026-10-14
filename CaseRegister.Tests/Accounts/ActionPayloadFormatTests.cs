using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;

namespace CaseRegister.Tests.Accounts;

/// <summary>
/// A proposal's hash covers the exact payload text, so that text must not move when the web layer's JSON settings do.
/// The payload has its own serialiser options in <see cref="ActionPayloads"/>; these tests pin the text it writes.
/// </summary>
public class ActionPayloadFormatTests
{
    private static readonly Guid _agency = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_role_is_written_as_its_name()
    {
        var payload = ActionPayloads.Build(new ProposeActionCommand(PendingActionType.AssignRole, Guid.NewGuid(), null, Role.CaseReviewer, null, "Needed for reviews"));

        Assert.Equal("""{"role":"CaseReviewer"}""", payload);
    }

    [Fact]
    public void An_approval_is_written_in_a_fixed_order_with_sorted_flags()
    {
        var payload = ActionPayloads.Build(new ProposeActionCommand(
            PendingActionType.ApproveApplication, Guid.NewGuid(), null, null, null, "Checked the certificate",
            ExistingAgencyId: _agency, AcknowledgedFlags: ["b-flag", "a-flag", "a-flag"]));

        Assert.Equal("""{"existingAgencyId":"11111111-2222-3333-4444-555555555555","newAgencyName":null,"acknowledgedFlags":["a-flag","b-flag"]}""", payload);
    }

    [Fact]
    public void An_invite_is_written_with_a_normalised_email_and_the_role_name()
    {
        var payload = ActionPayloads.Build(new ProposeActionCommand(
            PendingActionType.InviteStaff, null, null, Role.Admin, null, "New administrator",
            InviteEmail: "  New.Admin@Example.Test ", InviteFullName: " New Admin "));

        Assert.Equal("""{"email":"new.admin@example.test","fullName":"New Admin","role":"Admin"}""", payload);
    }
}

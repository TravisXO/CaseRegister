namespace CaseRegister.Domain.Accounts;

/// <summary>
/// The class of membership (Estate Agents Act, section 7(2)). It is stored and shown, but it decides
/// nothing: what a member may do depends on holding a current, verified practising certificate.
/// </summary>
public enum MembershipClass
{
    Full,
    Associate,
    Student,
    Honorary,
}

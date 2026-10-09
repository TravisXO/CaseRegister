using System.Linq.Expressions;
using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Certificates;

/// <summary>
/// A practising certificate. One runs for one calendar year, 1 January to 31 December, and a new one
/// is added each year: old ones are history and are never changed. Only a verified certificate that
/// covers today counts.
/// </summary>
public sealed class PractisingCertificate : Entity
{
    // For EF Core.
    private PractisingCertificate()
    {
    }

    public Guid UserId { get; private set; }

    public CertificateNumber Number { get; private set; } = null!;

    public DateOnly ValidFrom { get; private set; }

    public DateOnly ValidTo { get; private set; }

    /// <summary>When an admin checked it against the association's records. Null until then.</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }

    public Guid? VerifiedByUserId { get; private set; }

    public bool IsVerified => VerifiedAt is not null;

    /// <summary>Why a certificate with these details cannot exist. Empty when it can.</summary>
    public static IReadOnlyList<string> Check(CertificateNumber number, DateOnly validFrom, DateOnly validTo)
    {
        ArgumentNullException.ThrowIfNull(number);

        var problems = new List<string>();

        if (validFrom.Year != number.Year || validTo.Year != number.Year)
        {
            problems.Add($"The validity dates must be in {number.Year}, the year in the certificate number.");
        }

        if (validFrom != new DateOnly(validFrom.Year, 1, 1))
        {
            problems.Add("A certificate is valid from 1 January.");
        }

        if (validTo != new DateOnly(validTo.Year, 12, 31))
        {
            problems.Add("A certificate is valid to 31 December.");
        }

        return problems;
    }

    /// <summary>Adds a new, unverified certificate. Throws if the number and dates do not agree.</summary>
    public static PractisingCertificate Create(Guid userId, CertificateNumber number, DateOnly validFrom, DateOnly validTo)
    {
        var problems = Check(number, validFrom, validTo);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', problems));
        }

        return new PractisingCertificate
        {
            UserId = userId,
            Number = number,
            ValidFrom = validFrom,
            ValidTo = validTo,
        };
    }

    /// <summary>Marks the certificate as checked. Once verified it stays verified, and cannot be verified twice.</summary>
    public void Verify(Guid verifiedByUserId, DateTimeOffset now)
    {
        if (IsVerified)
        {
            throw new InvalidOperationException("This certificate has already been verified.");
        }

        VerifiedAt = now;
        VerifiedByUserId = verifiedByUserId;
    }

    /// <summary>
    /// The one definition of "current": verified, and <paramref name="today"/> falls inside the dates (both ends
    /// included). It is an expression so the same rule runs in memory and, translated, in a database query.
    /// </summary>
    public static Expression<Func<PractisingCertificate, bool>> CurrentOn(DateOnly today) =>
        certificate => certificate.VerifiedAt != null && certificate.ValidFrom <= today && today <= certificate.ValidTo;

    /// <summary>True when the certificate is current on <paramref name="today"/>. See <see cref="CurrentOn"/>.</summary>
    public bool IsCurrent(DateOnly today) => CurrentOn(today).Compile()(this);
}

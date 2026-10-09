using System.Reflection;
using CaseRegister.Application.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Certificates;
using Microsoft.Extensions.Time.Testing;

namespace CaseRegister.Tests.Accounts;

public class CertificateNumberTests
{
    [Theory]
    [InlineData("9611/2026", 9611, 2026)]
    [InlineData("1/2026", 1, 2026)]
    [InlineData("999999/2026", 999_999, 2026)]
    [InlineData("  9611/2026  ", 9611, 2026)]
    [InlineData("009611/2026", 9611, 2026)]
    [InlineData("12/1900", 12, 1900)]
    [InlineData("12/2999", 12, 2999)]
    public void Accepted_forms_parse(string input, int sequence, int year)
    {
        Assert.True(CertificateNumber.TryParse(input, out var number));
        Assert.Equal(sequence, number.Sequence);
        Assert.Equal(year, number.Year);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9611")]
    [InlineData("/2026")]
    [InlineData("9611/")]
    [InlineData("9611/26")] // two-digit year
    [InlineData("9611/20266")] // five-digit year
    [InlineData("1234567/2026")] // seven digits
    [InlineData("0/2026")]
    [InlineData("000000/2026")]
    [InlineData("9611-2026")]
    [InlineData("9611 / 2026")]
    [InlineData("9611\\2026")]
    [InlineData("abc/2026")]
    [InlineData("9611/202a")]
    [InlineData("-5/2026")]
    [InlineData("9611/1899")]
    [InlineData("9611/3000")]
    [InlineData("٩٦١١/2026")] // Arabic-Indic digits
    [InlineData("9611/2026\n2027")]
    [InlineData("9611/2026/1")]
    public void Rejected_forms_do_not_parse(string? input)
    {
        Assert.False(CertificateNumber.TryParse(input, out var number));
        Assert.Null(number);
        Assert.Throws<FormatException>(() => CertificateNumber.Parse(input));
    }

    [Fact]
    public void It_is_written_as_digits_slash_year_without_leading_zeros()
    {
        Assert.Equal("9611/2026", CertificateNumber.Parse("009611/2026").ToString());
    }

    [Fact]
    public void The_same_number_typed_two_ways_is_equal()
    {
        Assert.Equal(CertificateNumber.Parse("9611/2026"), CertificateNumber.Parse(" 009611/2026 "));
        Assert.NotEqual(CertificateNumber.Parse("9611/2026"), CertificateNumber.Parse("9611/2027"));
    }
}

public class PractisingCertificateTests
{
    private static readonly Guid _member = Guid.NewGuid();
    private static readonly Guid _admin = Guid.NewGuid();
    private static readonly DateTimeOffset _now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static PractisingCertificate Certificate2026() =>
        PractisingCertificate.Create(_member, CertificateNumber.Parse("9611/2026"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    // --- Number and dates must agree --------------------------------------------------------------

    [Fact]
    public void A_certificate_runs_for_exactly_one_calendar_year()
    {
        var certificate = Certificate2026();

        Assert.Equal(new DateOnly(2026, 1, 1), certificate.ValidFrom);
        Assert.Equal(new DateOnly(2026, 12, 31), certificate.ValidTo);
        Assert.False(certificate.IsVerified);
    }

    [Theory]
    [InlineData(2025, 1, 1, 2025, 12, 31)] // a different year from the number
    [InlineData(2027, 1, 1, 2027, 12, 31)]
    [InlineData(2026, 1, 1, 2027, 12, 31)] // runs into the next year
    [InlineData(2025, 1, 1, 2026, 12, 31)] // starts in the year before
    public void The_year_in_the_number_must_match_the_validity_year(int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        var problems = PractisingCertificate.Check(CertificateNumber.Parse("9611/2026"), new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(toYear, toMonth, toDay));

        Assert.NotEmpty(problems);
        Assert.Throws<ArgumentException>(() => PractisingCertificate.Create(_member, CertificateNumber.Parse("9611/2026"), new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(toYear, toMonth, toDay)));
    }

    [Theory]
    [InlineData(2026, 1, 2, 2026, 12, 31)] // does not start on 1 January
    [InlineData(2026, 2, 1, 2026, 12, 31)]
    [InlineData(2026, 1, 1, 2026, 12, 30)] // does not end on 31 December
    [InlineData(2026, 1, 1, 2026, 6, 30)]
    [InlineData(2026, 1, 1, 2026, 1, 1)]
    public void It_runs_from_1_January_to_31_December_and_nothing_else(int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        Assert.NotEmpty(PractisingCertificate.Check(CertificateNumber.Parse("9611/2026"), new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(toYear, toMonth, toDay)));
    }

    [Fact]
    public void A_well_formed_certificate_has_no_problems()
    {
        Assert.Empty(PractisingCertificate.Check(CertificateNumber.Parse("9611/2026"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Empty(PractisingCertificate.Check(CertificateNumber.Parse("1/2028"), new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31))); // a leap year
    }

    // --- Verified, and current --------------------------------------------------------------------

    [Fact]
    public void An_unverified_certificate_is_never_current()
    {
        var certificate = Certificate2026();

        Assert.False(certificate.IsCurrent(new DateOnly(2026, 6, 1)));
        Assert.False(certificate.IsCurrent(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Verifying_records_who_and_when()
    {
        var certificate = Certificate2026();

        certificate.Verify(_admin, _now);

        Assert.True(certificate.IsVerified);
        Assert.Equal(_now, certificate.VerifiedAt);
        Assert.Equal(_admin, certificate.VerifiedByUserId);
    }

    [Fact]
    public void A_certificate_cannot_be_verified_twice()
    {
        var certificate = Certificate2026();
        certificate.Verify(_admin, _now);

        Assert.Throws<InvalidOperationException>(() => certificate.Verify(Guid.NewGuid(), _now.AddDays(1)));
        Assert.Equal(_admin, certificate.VerifiedByUserId);
    }

    [Theory]
    [InlineData(2025, 12, 31, false)] // the day before it starts
    [InlineData(2026, 1, 1, true)] // the first day
    [InlineData(2026, 6, 15, true)]
    [InlineData(2026, 12, 31, true)] // the last day
    [InlineData(2027, 1, 1, false)] // the day after it ends
    public void A_verified_certificate_is_current_from_1_January_to_31_December_inclusive(int year, int month, int day, bool expected)
    {
        var certificate = Certificate2026();
        certificate.Verify(_admin, _now);

        Assert.Equal(expected, certificate.IsCurrent(new DateOnly(year, month, day)));
    }

    // --- History: never overwritten ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(PractisingCertificate.UserId))]
    [InlineData(nameof(PractisingCertificate.Number))]
    [InlineData(nameof(PractisingCertificate.ValidFrom))]
    [InlineData(nameof(PractisingCertificate.ValidTo))]
    [InlineData(nameof(PractisingCertificate.VerifiedAt))]
    [InlineData(nameof(PractisingCertificate.VerifiedByUserId))]
    public void No_detail_of_a_certificate_can_be_changed_from_outside(string property)
    {
        var setter = typeof(PractisingCertificate).GetProperty(property)!.SetMethod;

        Assert.True(setter is null || !setter.IsPublic, $"{property} must not have a public setter");
        Assert.DoesNotContain(
            typeof(PractisingCertificate).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.StartsWith("Set", StringComparison.Ordinal) || method.Name.StartsWith("Change", StringComparison.Ordinal) || method.Name.StartsWith("Renew", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_year_is_a_new_certificate_with_its_own_identity()
    {
        var last = Certificate2026();
        var next = PractisingCertificate.Create(_member, CertificateNumber.Parse("9611/2027"), new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));

        Assert.NotEqual(last.Id, next.Id);
        Assert.Equal(last.UserId, next.UserId);
    }
}

public class AgencyTests
{
    [Fact]
    public void A_new_agency_is_active_with_a_trimmed_name_and_a_version_7_id()
    {
        var agency = Agency.Create("  Copperbelt Homes  ");

        Assert.Equal("Copperbelt Homes", agency.Name);
        Assert.Equal(AgencyStatus.Active, agency.Status);
        Assert.Equal(7, agency.Id.ToByteArray(bigEndian: true)[6] >> 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_agency_needs_a_name(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Agency.Create(name));
    }

    [Fact]
    public void An_agency_name_has_a_length_limit()
    {
        Assert.Equal(Agency.MaxNameLength, Agency.Create(new string('a', Agency.MaxNameLength)).Name.Length);
        Assert.Throws<ArgumentException>(() => Agency.Create(new string('a', Agency.MaxNameLength + 1)));
    }
}

public class MembershipCalendarTests
{
    [Theory]
    [InlineData("2026-06-01T10:00:00Z", "2026-06-01")]
    [InlineData("2026-12-31T21:59:59Z", "2026-12-31")] // 23:59:59 in Zambia: still 31 December
    [InlineData("2026-12-31T22:00:00Z", "2027-01-01")] // midnight in Zambia: 1 January
    [InlineData("2026-12-31T23:59:59Z", "2027-01-01")]
    [InlineData("2027-01-01T00:00:00Z", "2027-01-01")]
    [InlineData("2025-12-31T22:00:00Z", "2026-01-01")]
    public void Today_is_the_date_in_Zambia_UTC_plus_two(string utcInstant, string expectedDate)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(utcInstant, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture), MembershipCalendar.Today(clock));
    }

    [Fact]
    public void The_offset_is_two_hours_with_no_daylight_saving()
    {
        Assert.Equal(TimeSpan.FromHours(2), MembershipCalendar.ZambiaOffset);
    }

    [Fact]
    public void A_certificate_ends_at_midnight_in_Zambia_not_in_London()
    {
        var certificate = PractisingCertificate.Create(Guid.NewGuid(), CertificateNumber.Parse("9611/2026"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        certificate.Verify(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.True(certificate.IsCurrent(MembershipCalendar.Today(new FakeTimeProvider(DateTimeOffset.Parse("2026-12-31T21:59:59Z", System.Globalization.CultureInfo.InvariantCulture)))));
        Assert.False(certificate.IsCurrent(MembershipCalendar.Today(new FakeTimeProvider(DateTimeOffset.Parse("2026-12-31T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture)))));
    }
}

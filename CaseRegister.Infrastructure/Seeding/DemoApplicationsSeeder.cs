using System.Security.Cryptography;
using Bogus;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Certificates;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CaseRegister.Infrastructure.Seeding;

public sealed record DemoSeedResult(int AgenciesCreated, int ApplicationsCreated);

/// <summary>
/// Fake agencies and a few pending member applications, so the whole sign-up and approval flow can be tried by hand. Everything is made
/// up (Bogus, with a fixed seed, so the same names come out every time) and every email address is on example.test, a reserved domain
/// that can never belong to a real person. It refuses to run anywhere but Development, and running it again adds nothing.
/// </summary>
public sealed class DemoApplicationsSeeder(
    IHostEnvironment environment,
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IPasswordHasher<ApplicationUser> hasher,
    IAuditLog audit,
    TimeProvider time)
{
    private const int AgencyCount = 4;
    private const int FirstMembershipNumber = 2001;

    public async Task<DemoSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException($"Demo data can only be made in the Development environment, not in '{environment.EnvironmentName}'.");
        }

        var faker = new Faker("en") { Random = new Randomizer(20261012) };
        var agencyNames = Enumerable.Range(0, AgencyCount * 3)
            .Select(_ => $"{faker.Address.City()} {faker.PickRandom("Properties", "Estates", "Realty", "Homes", "Lettings")}")
            .Distinct()
            .Take(AgencyCount)
            .ToList();

        var agenciesCreated = 0;
        foreach (var name in agencyNames)
        {
            if (!await db.Agencies.AnyAsync(agency => agency.Name == name, cancellationToken))
            {
                db.Agencies.Add(Agency.Create(name));
                agenciesCreated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // What each applicant claims. The first agency is named exactly, the second is named with a different ending (the reviewer is
        // shown it as a likely match), the third is new, and the last applicant claims a membership number someone else already has.
        var year = time.GetUtcNow().AddHours(2).Year; // Zambian time
        var claims = new (string AgencyName, int Number, MembershipClass Class)[]
        {
            (agencyNames[0], FirstMembershipNumber, MembershipClass.Full),
            (agencyNames[0], FirstMembershipNumber + 1, MembershipClass.Full),
            ($"{agencyNames[1]} Ltd", FirstMembershipNumber + 2, MembershipClass.Associate),
            ($"{faker.Address.City()} Prime Realty", FirstMembershipNumber + 3, MembershipClass.Full),
            (agencyNames[2], FirstMembershipNumber, MembershipClass.Full),
        };

        var applicationsCreated = 0;
        for (var index = 0; index < claims.Length; index++)
        {
            var email = $"demo.applicant{index + 1}@example.test";
            var fullName = faker.Name.FullName();
            if (await users.FindByEmailAsync(email) is not null)
            {
                continue;
            }

            var (agencyName, number, membershipClass) = claims[index];
            var flags = index == claims.Length - 1 ? ApplicationConcerns.DuplicateMembershipNumber : ApplicationConcerns.None;

            await Transactions.RunAsync(
                db,
                async () =>
                {
                    var user = ApplicationUser.CreateStaff(email, fullName, AccountStatus.Pending);
                    user.PasswordHash = hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))); // nobody signs in as them
                    var created = await users.CreateAsync(user);
                    if (!created.Succeeded)
                    {
                        throw new InvalidOperationException($"The demo account {email} could not be created: {string.Join(',', created.Errors.Select(error => error.Code))}");
                    }


                    db.MemberApplications.Add(MemberApplication.Submit(
                        user.Id,
                        MembershipNumber.FromValue(number),
                        membershipClass,
                        agencyName,
                        CertificateNumber.Parse($"{9000 + index + 1}/{year}"),
                        flags,
                        time.GetUtcNow()));
                    await db.SaveChangesAsync(cancellationToken);

                    await audit.WriteAsync(
                        new AuditEntry(AuditAction.MemberSignedUp, user.Id, Subject: user.Id.ToString(), Detail: "demo data from seed-demo", TargetType: "User"),
                        cancellationToken);
                },
                cancellationToken);
            applicationsCreated++;
        }

        return new DemoSeedResult(agenciesCreated, applicationsCreated);
    }
}

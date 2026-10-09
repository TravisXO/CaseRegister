using CaseRegister.Api.Security;
using CaseRegister.Application;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers(options => options.AddWebSecurityFilters());
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebSecurity();

var app = builder.Build();

// Must be first, so everything after it sees the real client address and scheme.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Development only, and exempt from the deny-by-default policy so the API description can be read.
    app.MapOpenApi().AllowAnonymous();

    // Development only. Production schema changes are applied deliberately, never on startup.
    await app.Services.MigrateDatabaseAsync();

    // Development only, and the seeder itself refuses to run anywhere else.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Lets the integration tests start the application with WebApplicationFactory<Program>.
public partial class Program;

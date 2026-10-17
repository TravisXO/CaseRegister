using CaseRegister.Api.Cli;
using CaseRegister.Api.Hardening;
using CaseRegister.Api.Security;
using CaseRegister.Application;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Infrastructure.Seeding;

// The container's health check: "dotnet CaseRegister.Api.dll healthcheck". It asks the running app's /health/live and
// exits. It comes before anything is built so it is quick and needs no configuration of its own.
if (args.Length > 0 && args[0] == HealthCheckCommand.Name)
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// "dotnet run -- migrate": applies pending migrations from the administrator's machine. It needs only the owner connection string,
// so it comes before the services are added (the rest of the application's settings are not needed, and not asked for).
if (args.Length > 0 && args[0] == MigrateCommand.Name)
{
    return await MigrateCommand.RunAsync(args, builder.Configuration, new ConsoleTerminal());
}

// Add services to the container.

builder.AddApiHardening();
builder.Services.AddControllers(options => options.AddWebSecurityFilters()).AddStrictJson();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddApiDescription();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebSecurity();

var app = builder.Build();

// "dotnet run -- seed-admin", "seed-demo", "verify-audit" and "anchor-audit" run a command instead of the web server. They come first, before anything
// touches the database, because it has to be able to refuse while migrations are pending.
if (args.Length > 0 && args[0] == SeedAdminCommand.Name)
{
    return await SeedAdminCommand.RunAsync(args, app.Services, app.Configuration, new ConsoleTerminal());
}

if (args.Length > 0 && BreakGlassCommands.Handles(args[0]))
{
    return await BreakGlassCommands.RunAsync(args, app.Services, app.Configuration, new ConsoleTerminal());
}

if (args.Length > 0 && args[0] == DemoCommands.Name)
{
    return await DemoCommands.RunAsync(args, app.Services, new ConsoleTerminal());
}

if (args.Length > 0 && AuditCommands.Handles(args[0]))
{
    return await AuditCommands.RunAsync(args, app.Services, new ConsoleTerminal());
}

// Forwarded headers, request log, error handling, security headers, HTTPS, limits: in the order that matters.
app.UseApiHardening();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Development only. Production schema changes are applied deliberately, never on startup.
    await app.Services.MigrateDatabaseAsync();

    // Development only, and the seeder itself refuses to run anywhere else.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
}

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOperationalEndpoints();

// Development only: the API description and its reference page. Exempt from the deny-by-default policy so they can be read.
app.MapApiReference();

app.Run();

return 0;

// Lets the integration tests start the application with WebApplicationFactory<Program>.
public partial class Program;

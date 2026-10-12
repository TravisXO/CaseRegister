using System.Text.Json.Serialization;
using CaseRegister.Api.Cli;
using CaseRegister.Api.Security;
using CaseRegister.Application;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers(options => options.AddWebSecurityFilters())
    // Enums travel as their names ("AssignRole"), never as numbers that mean something different if the list is reordered.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebSecurity();

var app = builder.Build();

// "dotnet run -- seed-admin", "verify-audit" and "anchor-audit" run a command instead of the web server. They come first, before anything
// touches the database, because it has to be able to refuse while migrations are pending.
if (args.Length > 0 && args[0] == SeedAdminCommand.Name)
{
    return await SeedAdminCommand.RunAsync(args, app.Services, app.Configuration, new ConsoleTerminal());
}

if (args.Length > 0 && AuditCommands.Handles(args[0]))
{
    return await AuditCommands.RunAsync(args, app.Services, new ConsoleTerminal());
}

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

return 0;

// Lets the integration tests start the application with WebApplicationFactory<Program>.
public partial class Program;

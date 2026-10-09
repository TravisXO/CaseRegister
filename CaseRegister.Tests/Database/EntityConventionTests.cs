using CaseRegister.Domain.Common;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace CaseRegister.Tests.Database;

/// <summary>
/// Proves the conventions every entity gets. The tests use a throwaway entity because the real
/// ones do not exist yet; the conventions are applied by the same code the real context uses.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public class EntityConventionTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _start = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void New_entities_get_version_7_guids()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => new Widget().Id).ToList();

        Assert.All(ids, id =>
        {
            var bytes = id.ToByteArray(bigEndian: true);
            Assert.Equal(7, bytes[6] >> 4);
            Assert.Equal(0b10, bytes[8] >> 6);
        });
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Tables_and_columns_are_snake_case_and_timestamps_are_timestamptz()
    {
        var (connectionString, _) = await CreateDatabaseAsync();

        var columns = await ColumnsAsync(connectionString);

        Assert.Equal("timestamp with time zone", columns["created_at"]);
        Assert.Equal("timestamp with time zone", columns["updated_at"]);
        Assert.Contains("id", columns.Keys);
        Assert.Contains("display_name", columns.Keys);

        // The concurrency token is Postgres's own xmin, not a column of ours.
        Assert.DoesNotContain("version", columns.Keys);
        Assert.DoesNotContain("xmin", columns.Keys);
    }

    [Fact]
    public async Task Timestamps_come_from_the_time_provider()
    {
        var (connectionString, clock) = await CreateDatabaseAsync();
        var id = await AddWidgetAsync(connectionString, clock);

        clock.Advance(TimeSpan.FromHours(2));
        await using (var context = NewContext(connectionString, clock))
        {
            var widget = await context.Widgets.SingleAsync(w => w.Id == id);
            widget.DisplayName = "Renamed";
            await context.SaveChangesAsync();
        }

        await using var verify = NewContext(connectionString, clock);
        var stored = await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == id);
        Assert.Equal(_start, stored.CreatedAt);
        Assert.Equal(_start.AddHours(2), stored.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
    }

    [Fact]
    public async Task Two_people_editing_the_same_record_cannot_silently_overwrite_each_other()
    {
        var (connectionString, clock) = await CreateDatabaseAsync();
        var id = await AddWidgetAsync(connectionString, clock);

        await using var first = NewContext(connectionString, clock);
        await using var second = NewContext(connectionString, clock);
        var firstCopy = await first.Widgets.SingleAsync(w => w.Id == id);
        var secondCopy = await second.Widgets.SingleAsync(w => w.Id == id);

        firstCopy.DisplayName = "First editor";
        await first.SaveChangesAsync();

        secondCopy.DisplayName = "Second editor";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = NewContext(connectionString, clock);
        Assert.Equal("First editor", (await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == id)).DisplayName);
    }

    private async Task<(string ConnectionString, FakeTimeProvider Clock)> CreateDatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(_start);

        await using var context = NewContext(connectionString, clock);
        await context.Database.EnsureCreatedAsync();

        return (connectionString, clock);
    }

    private static async Task<Guid> AddWidgetAsync(string connectionString, FakeTimeProvider clock)
    {
        await using var context = NewContext(connectionString, clock);
        var widget = new Widget { DisplayName = "Original" };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync();

        return widget.Id;
    }

    private static WidgetDbContext NewContext(string connectionString, FakeTimeProvider clock)
    {
        var builder = new DbContextOptionsBuilder<WidgetDbContext>();
        AppDbContextOptions.Configure(builder, connectionString);
        builder.AddInterceptors(new EntityTimestampInterceptor(clock));

        return new WidgetDbContext(builder.Options);
    }

    private static async Task<Dictionary<string, string>> ColumnsAsync(string connectionString)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'widgets'", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new Dictionary<string, string>();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }

    private sealed class Widget : Entity
    {
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyEntityConventions();
    }
}

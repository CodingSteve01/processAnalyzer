using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// The rules that live in SQL — working time, activity labels, projection.
/// </summary>
/// <remarks>
/// These are the pieces where a mistake is invisible: a wrong calendar makes every duration wrong in the same
/// direction, a collapsed activity label turns two approval steps into one and reports rework that is not there.
/// Every case below reproduces a defect that actually shipped during development.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AnalyticsSqlTests
{
    private readonly PostgresFixture _postgres;

    public AnalyticsSqlTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task BusinessSeconds_SkipsTheHoursNobodyWorks()
    {
        await ConfigureCalendarAsync(hours: 8, holidays: []);

        // Wednesday 15:00 to Friday 09:00 with an 8-hour day starting at 07:00: Wednesday is already over at 15:00,
        // Thursday contributes its full day, Friday two hours.
        var seconds = await ScalarAsync("SELECT analytics.biz_seconds('2026-05-20 15:00+02', '2026-05-22 09:00+02')");

        Assert.Equal(10 * 3600, seconds, 1);
    }

    [Fact]
    public async Task BusinessSeconds_SkipsTheWeekend()
    {
        await ConfigureCalendarAsync(hours: 8, holidays: []);

        // Friday afternoon to Monday morning. Without the weekend rule this is the single edge that tops every
        // bottleneck ranking, and the whole list becomes a weekend detector.
        var seconds = await ScalarAsync("SELECT analytics.biz_seconds('2026-05-22 15:00+02', '2026-05-25 09:00+02')");

        Assert.Equal(2 * 3600, seconds, 1);
    }

    [Fact]
    public async Task BusinessSeconds_RemovesAFullHoliday()
    {
        await ConfigureCalendarAsync(hours: 8, holidays: [("2026-05-21", 0.0m)]);

        var seconds = await ScalarAsync("SELECT analytics.biz_seconds('2026-05-20 15:00+02', '2026-05-22 09:00+02')");

        // The same span as the first case, minus the Thursday. Read the holiday flags the wrong way round and this
        // stays at ten hours — which is exactly what happened, and nothing about the result looked wrong.
        Assert.Equal(2 * 3600, seconds, 1);
    }

    [Fact]
    public async Task BusinessSeconds_HalvesAHalfHoliday()
    {
        await ConfigureCalendarAsync(hours: 8, holidays: [("2026-05-21", 0.5m)]);

        var seconds = await ScalarAsync("SELECT analytics.biz_seconds('2026-05-21 00:00+02', '2026-05-21 23:59+02')");

        Assert.Equal(4 * 3600, seconds, 1);
    }

    [Theory]
    [InlineData("demo.document.release-granted.v1", "{\"role\": \"Accounting\"}", "Freigabe erteilt (Buchhaltung)")]
    [InlineData(
        "demo.workflow-action.executed.v1",
        "{\"actionType\": \"SendDocumentEmail\"}",
        "Workflow-Schritt ausgeführt (Mailversand)"
    )]
    [InlineData("demo.document.classification-resolved.v1", "{\"method\": \"manual\"}", "Beleg zugeordnet (von Hand)")]
    public async Task ActivityLabel_CarriesTheAttributeThatNamesTheStep(string type, string attrs, string expected)
    {
        var label = await TextAsync(
            $"SELECT analytics.label_activity(analytics.activity_of('{type}', '{attrs}'::jsonb))"
        );

        // Two approvals by two roles are two steps. Without the discriminator they are one, every multi-role
        // approval reads as 100% rework, and the variant list collapses to "granted → granted".
        Assert.Equal(expected, label);
    }

    [Fact]
    public async Task ActivityLabel_MarksATypeNobodyHasNamed()
    {
        var label = await TextAsync("SELECT analytics.label_activity('demo.thing.rescheduled.v1')");

        // Marked, not guessed and not dropped. An unlabelled type is new instrumentation, and that is worth seeing.
        Assert.StartsWith("⚠", label);
        Assert.Contains("demo.thing.rescheduled.v1", label);
    }

    [Fact]
    public async Task Identity_IsOffUnlessTheSettingSaysOtherwise()
    {
        await ExecuteAsync("UPDATE analytics.setting SET value = 'false' WHERE key = 'show_actor_identity'");
        await ExecuteAsync(
            "INSERT INTO dim.actor (actor_key, source_id, display_name) VALUES ('a:testkey0001', 'u-1', 'Anna Beispiel') "
                + "ON CONFLICT (actor_key) DO UPDATE SET display_name = EXCLUDED.display_name"
        );

        var hidden = await TextAsync("SELECT analytics.person('a:testkey0001')");
        Assert.Equal("a:testkey0001", hidden);

        await ExecuteAsync("UPDATE analytics.setting SET value = 'true' WHERE key = 'show_actor_identity'");
        var shown = await TextAsync("SELECT analytics.person('a:testkey0001')");
        Assert.Equal("Anna Beispiel", shown);

        await ExecuteAsync("UPDATE analytics.setting SET value = 'false' WHERE key = 'show_actor_identity'");
        await ExecuteAsync("DELETE FROM dim.actor WHERE actor_key = 'a:testkey0001'");
    }

    private async Task ConfigureCalendarAsync(decimal hours, (string Day, decimal Factor)[] holidays)
    {
        await ExecuteAsync("DELETE FROM analytics.holiday");
        await ExecuteAsync("DELETE FROM analytics.business_slot");
        // Formatted with the invariant culture, not the machine's: under de-DE a decimal renders with a comma, and
        // "0,5" inside a VALUES list silently becomes two columns. The failure surfaces as "INSERT has more
        // expressions than target columns", which points nowhere near the number.
        for (var day = 1; day <= 5; day++)
            await ExecuteAsync(
                "INSERT INTO analytics.business_slot (dow, open_from, open_to, hours, source) "
                    + FormattableString.Invariant($"VALUES ({day}, '07:00', '15:00', {hours}, 'test')")
            );

        foreach (var (day, factor) in holidays)
            await ExecuteAsync(
                FormattableString.Invariant(
                    $"INSERT INTO analytics.holiday (day, label, factor, source) VALUES ('{day}', 'Test', {factor}, 'test')"
                )
            );
    }

    private async Task<double> ScalarAsync(string sql) => Convert.ToDouble(await RawAsync(sql));

    private async Task<string> TextAsync(string sql) => (string)(await RawAsync(sql))!;

    private async Task<object?> RawAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}

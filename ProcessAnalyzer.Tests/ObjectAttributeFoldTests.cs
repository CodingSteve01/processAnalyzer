using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// How a classification travels from the mirror into something the analysis can group by.
/// </summary>
/// <remarks>
/// The fold is the one place where "what a case is" comes into being, and every way it can be wrong is silent: a stale
/// value, a classification for an object that does not exist, an empty string that erases a real answer. None of those
/// throw: they produce a screen that groups confidently by the wrong thing.
/// <para>
/// It is also deliberately NOT tied to the batch the projection is working on, so these cases include the two orders
/// that a batch-bound fold would get wrong: a statement that arrives before its object, and a run over history that
/// already happened.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ObjectAttributeFoldTests
{
    private readonly PostgresFixture _postgres;

    public ObjectAttributeFoldTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task NewestStatementWins()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 1, "document:1", "{\"belegart\": \"Rechnung\"}");
        await StateAsync(eventSourceId: 2, "document:1", "{\"belegart\": \"Gutschrift\"}");

        await FoldAsync();

        // A classification that changes is a correction, not a history: the last word is what the case is.
        Assert.Equal("Gutschrift", await ValueAsync("document:1", "belegart"));
    }

    [Fact]
    public async Task AnOlderStatementArrivingLateDoesNotOverwriteTheNewerOne()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 2, "document:1", "{\"belegart\": \"Gutschrift\"}");
        await FoldAsync();

        // Batches can arrive out of order. Folding an older statement afterwards must not walk the value backwards.
        await StateAsync(eventSourceId: 1, "document:1", "{\"belegart\": \"Rechnung\"}");
        await FoldAsync();

        Assert.Equal("Gutschrift", await ValueAsync("document:1", "belegart"));
    }

    [Fact]
    public async Task AStatementMadeBeforeItsObjectExistedIsPickedUpLater()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 1, "document:99", "{\"belegart\": \"Rechnung\"}");

        // Nothing yet: the object is not in the projection, and a classification without an object is a row nobody
        // could ever join.
        await FoldAsync();
        Assert.Null(await ValueAsync("document:99", "belegart"));

        await using (var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ocel.object (id, type, first_seen, last_seen)
                VALUES ('document:99', 'document', now(), now());
                """
            );
        }

        // This is what "self-healing" has to mean: the next run is correct without anybody replaying a batch.
        await FoldAsync();
        Assert.Equal("Rechnung", await ValueAsync("document:99", "belegart"));
    }

    [Fact]
    public async Task AnEmptyValueSaysNothingAndDoesNotEraseAnAnswer()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 1, "document:1", "{\"belegart\": \"Rechnung\"}");
        await StateAsync(eventSourceId: 2, "document:1", "{\"belegart\": \"\"}");

        await FoldAsync();

        Assert.Equal("Rechnung", await ValueAsync("document:1", "belegart"));
    }

    [Fact]
    public async Task SeveralClassificationsOnOneObjectStaySideBySide()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 1, "document:1", "{\"belegart\": \"Rechnung\", \"bereich\": \"Bereich A\"}");

        await FoldAsync();

        Assert.Equal("Rechnung", await ValueAsync("document:1", "belegart"));
        Assert.Equal("Bereich A", await ValueAsync("document:1", "bereich"));
    }

    [Fact]
    public async Task TheScopeNarrowsToCasesCarryingTheValue()
    {
        await SeedAsync();
        await StateAsync(eventSourceId: 1, "document:1", "{\"belegart\": \"Rechnung\"}");
        await StateAsync(eventSourceId: 2, "document:2", "{\"belegart\": \"Gutschrift\"}");
        await FoldAsync();

        Assert.True(await InScopeAsync("document:1", "belegart", "Rechnung"));
        Assert.False(await InScopeAsync("document:2", "belegart", "Rechnung"));

        // A name without a value asks "classified at all": the question behind every coverage gap.
        Assert.True(await InScopeAsync("document:2", "belegart", null));
        Assert.False(await InScopeAsync("document:3", "belegart", null));

        // No property means no filter, never "no cases". A scope that silently emptied every panel would read as a
        // system with no data.
        Assert.True(await InScopeAsync("document:3", null, null));
    }

    /// <summary>Six documents and six events to hang statements on. No classifications: each case makes its own.</summary>
    private async Task SeedAsync()
    {
        await _postgres.ResetAsync();
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       performer_id, source_application, payload)
            SELECT n,
                   gen_random_uuid(),
                   'demo.document.uploaded.v1',
                   timestamptz '2026-08-07 08:00+02' + make_interval(hours => n),
                   timestamptz '2026-08-07 08:00+02' + make_interval(hours => n),
                   'User', 'u-1', 'erp', {0}::jsonb
            FROM generate_series(1, 6) AS n;

            INSERT INTO ocel.object (id, type, first_seen, last_seen)
            SELECT 'document:' || n, 'document', now(), now()
            FROM generate_series(1, 6) AS n;
            """,
            // A parameter rather than a literal: ExecuteSqlRaw runs the statement through string.Format first, and a
            // bare {} in the SQL is read as a placeholder.
            "{}"
        );
    }

    /// <summary>The source stating a classification on an object reference, the way the mirror receives it.</summary>
    /// <remarks>
    /// The row key is derived from the event and the document number rather than generated, so a case can state twice
    /// about the same object from two different events without colliding, which is exactly the ordering these tests
    /// are about.
    /// </remarks>
    private async Task StateAsync(long eventSourceId, string objectRef, string attributes)
    {
        var id = objectRef.Split(':')[1];

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO journal.event_object (source_id, event_source_id, object_type, object_id, qualifier, attributes)
            VALUES ({0}, {1}, 'document', {2}, 'affected', {3}::jsonb);
            """,
            eventSourceId * 1000 + long.Parse(id),
            eventSourceId,
            id,
            attributes
        );
    }

    private async Task FoldAsync()
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync("SELECT ocel.project_object_attributes();");
    }

    private async Task<string?> ValueAsync(string objectId, string name)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var rows = await db
            .Database.SqlQueryRaw<string>(
                "SELECT value AS \"Value\" FROM ocel.object_attribute WHERE object_id = {0} AND name = {1}",
                objectId,
                name
            )
            .ToListAsync(CancellationToken.None);

        return rows.SingleOrDefault();
    }

    private async Task<bool> InScopeAsync(string objectId, string? name, string? value)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var rows = await db
            .Database.SqlQueryRaw<bool>(
                "SELECT analytics.case_in_scope({0}, NULL, NULL, NULL, {1}, {2}) AS \"Value\"",
                objectId,
                (object?)name ?? DBNull.Value,
                (object?)value ?? DBNull.Value
            )
            .ToListAsync(CancellationToken.None);

        return rows.Single();
    }
}

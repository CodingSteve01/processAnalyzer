using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Analytics;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Endpoints;

/// <summary>
/// What a resource is, correctable from inside the tool.
/// </summary>
/// <remarks>
/// The source says how an event arrived — as a user, as a device, as a scheduled job. That is the channel, and it is a
/// fact. What it is not is an identity: the same account id arrives as both 'User' and 'Device' because a driver
/// confirms from the tablet and corrects at the desk, and a technical account that posts through the API arrives as
/// 'User' and looks like a person for the rest of the analysis.
/// <para>
/// Nothing in the log can settle that, because nothing in the log knows. Somebody in the company does, so they get a
/// list and a control — the same arrangement as the vocabulary, for the same reason.
/// </para>
/// </remarks>
public static class ActorEndpoints
{
    private static readonly string[] Kinds = ["human", "job", "service", "device", "external"];

    public static WebApplication MapActorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/actors");

        // Everybody who ever did anything, busiest first, with what they are and where that came from. Enough that a
        // reader can find the account they mean without scrolling: after fifty rows a list stops being read.
        group.MapGet(
            "/",
            async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
                Results.Ok(
                    await Query.RunAsync(
                        factory,
                        """
                        SELECT i.actor_key                              AS schluessel,
                               analytics.person(i.actor_key)            AS person,
                               r.role                                   AS rolle,
                               i.kind                                   AS art,
                               i.derived_kind                           AS art_aus_dem_log,
                               i.is_corrected                           AS korrigiert,
                               i.note                                   AS notiz,
                               i.events                                 AS schritte,
                               -- More than one channel means the same account acted both ways. That is the case where
                               -- the log alone cannot say what the account is.
                               i.channels                               AS kanaele
                        FROM dim.actor_identity i
                        LEFT JOIN dim.actor_role r ON r.actor_key = i.actor_key
                        ORDER BY i.events DESC
                        LIMIT 500
                        """,
                        ct
                    )
                )
        );

        // One correction. Refreshes what depends on it right away: marking an account as a bot changes the automation
        // figures, and a figure that only changes at the next pull would look like the correction did nothing.
        group.MapPut(
            "/kind",
            async (IDbContextFactory<AppDbContext> factory, ActorKindEdit edit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(edit.ActorKey))
                    return Results.BadRequest(new { error = "Ohne Akteur geht das nicht." });
                if (!Kinds.Contains(edit.Kind))
                    return Results.BadRequest(new { error = $"Art muss eine von {string.Join(", ", Kinds)} sein." });

                await using var db = await factory.CreateDbContextAsync(ct);
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await connection.OpenAsync(ct);

                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO dim.actor_kind_override (actor_key, kind, note, updated_at)
                    VALUES (@key, @kind, @note, now())
                    ON CONFLICT (actor_key) DO UPDATE
                        SET kind = EXCLUDED.kind, note = EXCLUDED.note, updated_at = now()
                    """,
                    connection
                );
                command.Parameters.AddWithValue("key", edit.ActorKey);
                command.Parameters.AddWithValue("kind", edit.Kind);
                command.Parameters.AddWithValue(
                    "note",
                    string.IsNullOrWhiteSpace(edit.Note) ? DBNull.Value : edit.Note.Trim()
                );
                await command.ExecuteNonQueryAsync(ct);

                await RefreshAsync(db, ct);
                return Results.Ok(new { edit.ActorKey, edit.Kind });
            }
        );

        // Back to what the log says.
        group.MapPost(
            "/kind/reset",
            async (IDbContextFactory<AppDbContext> factory, ActorReference key, CancellationToken ct) =>
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await connection.OpenAsync(ct);

                await using var command = new NpgsqlCommand(
                    "DELETE FROM dim.actor_kind_override WHERE actor_key = @key",
                    connection
                );
                command.Parameters.AddWithValue("key", key.ActorKey);
                await command.ExecuteNonQueryAsync(ct);

                await RefreshAsync(db, ct);
                return Results.Ok(new { key.ActorKey });
            }
        );

        return app;
    }

    /// <summary>
    /// What a corrected kind changes: who the actors are, and then every case measure that asks whether a person was
    /// involved.
    /// </summary>
    private static async Task RefreshAsync(AppDbContext db, CancellationToken ct)
    {
        db.Database.SetCommandTimeout(600);
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW dim.actor_identity", ct);
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.object_lifecycle", ct);
    }

    private sealed record ActorKindEdit(string ActorKey, string Kind, string? Note);

    private sealed record ActorReference(string ActorKey);
}

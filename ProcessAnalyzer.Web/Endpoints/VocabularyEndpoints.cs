using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Analytics;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Endpoints;

/// <summary>
/// Naming things, from inside the tool.
/// </summary>
/// <remarks>
/// The vocabulary arrives as files with the deployment, which is right for shipping a known source and wrong for the
/// moment it matters: a step appears unnamed on a screen, the person looking at it knows the word, and without this
/// the only way to write it down is a file on a server plus a restart. So the word gets lost and the screen keeps
/// showing a dotted identifier behind a warning sign.
/// <para>
/// What is typed here wins over the file and survives a restart (see 021-labels-editable.sql). The file value is kept,
/// so a correction can be taken back without hunting for what it used to say.
/// </para>
/// </remarks>
public static class VocabularyEndpoints
{
    private static readonly string[] Kinds = ["event", "object", "entity", "verb", "discriminator", "qualifier"];

    public static WebApplication MapVocabularyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/vocabulary");

        // What is still unnamed, most frequent first — the order in which naming pays off.
        group.MapGet(
            "/gaps",
            async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
                Results.Ok(
                    await Query.RunAsync(
                        factory,
                        """
                        SELECT kind, type_name AS technischer_typ, occurrences AS beobachtet
                        FROM analytics.naming_gap
                        LIMIT 500
                        """,
                        ct
                    )
                )
        );

        // Everything that has a name, with where the name came from. The list a person works in.
        group.MapGet(
            "/labels",
            async (IDbContextFactory<AppDbContext> factory, string? kind, CancellationToken ct) =>
                Results.Ok(
                    await Query.RunAsync(
                        factory,
                        """
                        SELECT l.kind,
                               l.type_name      AS technischer_typ,
                               l.label_de       AS bezeichnung,
                               l.hint_de        AS erklaerung,
                               l.source         AS herkunft,
                               l.file_label_de  AS bezeichnung_aus_datei,
                               l.updated_at     AS geaendert_am
                        FROM ocel.label l
                        WHERE (@kind = '' OR l.kind = @kind)
                        ORDER BY l.kind, l.label_de
                        """,
                        ct,
                        ("kind", kind ?? string.Empty)
                    )
                )
        );

        // Naming or correcting one type.
        group.MapPut(
            "/label",
            async (IDbContextFactory<AppDbContext> factory, LabelEdit edit, CancellationToken ct) =>
            {
                if (!Kinds.Contains(edit.Kind, StringComparer.Ordinal))
                    return Results.BadRequest(new { error = $"unbekannte Art: {edit.Kind}" });
                if (string.IsNullOrWhiteSpace(edit.TypeName))
                    return Results.BadRequest(new { error = "technischer Typ fehlt" });

                var label = edit.Label?.Trim() ?? string.Empty;
                if (label.Length == 0)
                    return Results.BadRequest(new { error = "Bezeichnung fehlt" });

                // A label that is the key is not a translation of it, and the whole point of the vocabulary is that no
                // screen shows a dotted identifier. Rejected here rather than rendered later.
                if (label.Contains('.') && label.Split('.').Length > 2)
                    return Results.BadRequest(new { error = "Das ist der technische Schlüssel, kein Name." });

                await using var db = await factory.CreateDbContextAsync(ct);
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await connection.OpenAsync(ct);

                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO ocel.label (kind, type_name, label_de, hint_de, source, updated_at)
                    VALUES (@kind, @type, @label, @hint, 'ui', now())
                    ON CONFLICT (kind, type_name) DO UPDATE
                        SET label_de = EXCLUDED.label_de,
                            hint_de = EXCLUDED.hint_de,
                            source = 'ui',
                            updated_at = now()
                    """,
                    connection
                );
                command.Parameters.AddWithValue("kind", edit.Kind);
                command.Parameters.AddWithValue("type", edit.TypeName.Trim());
                command.Parameters.AddWithValue("label", label);
                command.Parameters.AddWithValue(
                    "hint",
                    string.IsNullOrWhiteSpace(edit.Hint) ? DBNull.Value : edit.Hint.Trim()
                );
                await command.ExecuteNonQueryAsync(ct);

                return Results.Ok(
                    new
                    {
                        edit.Kind,
                        edit.TypeName,
                        label,
                    }
                );
            }
        );

        // Back to what the vocabulary says. Deletes the row when the file never had one, so the type shows up as a gap
        // again rather than keeping a name nobody can find the source of.
        group.MapPost(
            "/label/reset",
            async (IDbContextFactory<AppDbContext> factory, LabelKey key, CancellationToken ct) =>
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                await connection.OpenAsync(ct);

                await using var command = new NpgsqlCommand(
                    """
                    WITH restored AS (
                        UPDATE ocel.label
                           SET label_de = file_label_de,
                               hint_de = file_hint_de,
                               source = 'file',
                               updated_at = now()
                         WHERE kind = @kind AND type_name = @type AND file_label_de IS NOT NULL
                        RETURNING 1
                    )
                    DELETE FROM ocel.label
                     WHERE kind = @kind AND type_name = @type AND NOT EXISTS (SELECT 1 FROM restored)
                    """,
                    connection
                );
                command.Parameters.AddWithValue("kind", key.Kind);
                command.Parameters.AddWithValue("type", key.TypeName);
                await command.ExecuteNonQueryAsync(ct);

                return Results.Ok(new { key.Kind, key.TypeName });
            }
        );

        // The whole vocabulary as the file it came from, so what was typed here can go back into the deployment instead
        // of living only in this database.
        group.MapGet(
            "/export",
            async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
            {
                var rows = await Query.RunAsync(
                    factory,
                    "SELECT kind, type_name, label_de, coalesce(hint_de, '') AS hint_de FROM ocel.label ORDER BY kind, type_name",
                    ct
                );

                var lines = new List<string>(rows.Count + 4)
                {
                    "# labels.tsv — kind<TAB>type_name<TAB>label_de<TAB>hint_de",
                    "#",
                    "# Exported from the running installation. Rows named in the tool are in here too, which is the point:",
                    "# they belong in the deployment, not only in one database.",
                };
                lines.AddRange(
                    rows.Select(row =>
                        string.Join('\t', row["kind"], row["type_name"], row["label_de"], row["hint_de"])
                    )
                );

                return Results.Text(string.Join('\n', lines) + '\n', "text/tab-separated-values");
            }
        );

        return app;
    }

    /// <param name="Kind">event, object, entity, verb, discriminator or qualifier.</param>
    /// <param name="TypeName">The technical key being named.</param>
    /// <param name="Label">What a person should read.</param>
    /// <param name="Hint">One sentence of explanation, or nothing.</param>
    public sealed record LabelEdit(string Kind, string TypeName, string? Label, string? Hint);

    /// <param name="Kind">event, object, entity, verb, discriminator or qualifier.</param>
    /// <param name="TypeName">The technical key.</param>
    public sealed record LabelKey(string Kind, string TypeName);
}

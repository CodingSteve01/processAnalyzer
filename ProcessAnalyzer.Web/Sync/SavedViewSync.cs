using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Sync;

/// <summary>
/// Pulls the views people saved for themselves — which screen, which columns, which filters.
/// </summary>
/// <remarks>
/// The closest thing to a job description this system holds. The log says who did what; it does not say who somebody
/// IS, and a department is not a role: two dispatchers in the same department differ in exactly the way that matters.
/// What somebody configured for themselves says it, is dated, and cost nobody an interview.
/// <para>
/// Master data, read whole and replaced, like the directory: a view somebody deleted has to disappear rather than
/// linger as a role they no longer hold.
/// </para>
/// <para>
/// <b>Names only, never values.</b> A filter value is free text a person typed — a customer name, a licence plate, a
/// vendor. The property name is a schema identifier and answers the role question on its own, so the value is dropped
/// on this side of the wire rather than stored and forgotten about.
/// </para>
/// </remarks>
public sealed class SavedViewSync
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<SavedViewSync> _logger;

    public SavedViewSync(
        IDbContextFactory<AppDbContext> factory,
        ProcessAnalyzerOptions options,
        ILogger<SavedViewSync> logger
    )
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task<SavedViewResult> SyncAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SourceConnectionString))
            return new SavedViewResult(0, 0, 0, "no source configured");

        var views = await ReadAsync(ct);
        await WriteAsync(views, ct);

        var filters = views.Sum(view => view.Filters.Count);
        var columns = views.Sum(view => view.Columns.Count);
        _logger.LogInformation(
            "Saved views synced: {Views} views, {Filters} filter properties, {Columns} columns",
            views.Count,
            filters,
            columns
        );

        return new SavedViewResult(views.Count, filters, columns, null);
    }

    private async Task<List<SavedView>> ReadAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_options.SourceConnectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        // Only views that belong to a person. A global view is a template somebody else set up and says nothing about
        // who works how — counting it as a role would give every user the same signature.
        command.CommandText = """
            SELECT v.Id, v.UserId, v.Path, v.Name, v.IsDefaultView, v.CreationDate, v.ModificationDate, v.Data
            FROM dbo.ApplicationViews v
            WHERE v.UserId IS NOT NULL
              AND v.Path IS NOT NULL
              AND (v.IsGlobal IS NULL OR v.IsGlobal = 0)
            """;
        command.CommandTimeout = 300;

        var views = new List<SavedView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var data = await reader.IsDBNullAsync(7, ct) ? null : reader.GetString(7);
            var (filters, columns) = SavedViewPayload.Decompose(data);

            views.Add(
                new SavedView(
                    reader.GetInt64(0),
                    ActorKey(reader.GetString(1)),
                    reader.GetString(2),
                    await reader.IsDBNullAsync(3, ct) ? null : reader.GetString(3),
                    !await reader.IsDBNullAsync(4, ct) && reader.GetBoolean(4),
                    await reader.IsDBNullAsync(5, ct) ? null : reader.GetDateTime(5),
                    await reader.IsDBNullAsync(6, ct) ? null : reader.GetDateTime(6),
                    filters,
                    columns
                )
            );
        }

        return views;
    }

    private async Task WriteAsync(List<SavedView> views, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Replaced wholesale inside one transaction, for the same reason as the directory: a deleted view has to be
        // gone, and a half-written set would show somebody a role they gave up months ago.
        await using (var truncate = connection.CreateCommand())
        {
            truncate.CommandText = "TRUNCATE dim.saved_view CASCADE";
            await truncate.ExecuteNonQueryAsync(ct);
        }

        foreach (var view in views)
        {
            long id;
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO dim.saved_view (source_id, actor_key, path, name, is_default, created_at, changed_at)
                    VALUES (@source, @actor, @path, @name, @isDefault, @created, @changed)
                    RETURNING id
                    """;
                insert.Parameters.AddWithValue("source", view.SourceId);
                insert.Parameters.AddWithValue("actor", view.ActorKey);
                insert.Parameters.AddWithValue("path", view.Path);
                insert.Parameters.AddWithValue("name", (object?)view.Name ?? DBNull.Value);
                insert.Parameters.AddWithValue("isDefault", view.IsDefault);
                insert.Parameters.AddWithValue(
                    "created",
                    view.CreatedAt is null ? DBNull.Value : DateTime.SpecifyKind(view.CreatedAt.Value, DateTimeKind.Utc)
                );
                insert.Parameters.AddWithValue(
                    "changed",
                    view.ChangedAt is null ? DBNull.Value : DateTime.SpecifyKind(view.ChangedAt.Value, DateTimeKind.Utc)
                );
                id = (long)(await insert.ExecuteScalarAsync(ct))!;
            }

            foreach (var property in view.Filters)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO dim.saved_view_filter (view_id, property) VALUES (@view, @property) "
                    + "ON CONFLICT DO NOTHING";
                insert.Parameters.AddWithValue("view", id);
                insert.Parameters.AddWithValue("property", property);
                await insert.ExecuteNonQueryAsync(ct);
            }

            for (var index = 0; index < view.Columns.Count; index++)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO dim.saved_view_column (view_id, property, ord) VALUES (@view, @property, @ord) "
                    + "ON CONFLICT DO NOTHING";
                insert.Parameters.AddWithValue("view", id);
                insert.Parameters.AddWithValue("property", view.Columns[index]);
                insert.Parameters.AddWithValue("ord", index);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The same pseudonym the directory and the projection compute. All three must agree or the role profile joins to
    /// nothing and shows an empty screen rather than failing.
    /// </summary>
    private string ActorKey(string sourceId)
    {
        var key = string.IsNullOrWhiteSpace(_options.ActorHashKey)
            ? "local-development-actor-key"
            : _options.ActorHashKey;

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(sourceId));
        return "a:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private sealed record SavedView(
        long SourceId,
        string ActorKey,
        string Path,
        string? Name,
        bool IsDefault,
        DateTime? CreatedAt,
        DateTime? ChangedAt,
        List<string> Filters,
        List<string> Columns
    );
}

/// <param name="Views">Saved views taken over.</param>
/// <param name="Filters">Filter properties across them.</param>
/// <param name="Columns">Columns across them.</param>
/// <param name="Skipped">Why nothing was done, when nothing was.</param>
public sealed record SavedViewResult(int Views, int Filters, int Columns, string? Skipped);

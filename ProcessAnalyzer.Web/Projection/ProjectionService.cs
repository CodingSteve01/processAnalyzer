using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Projection;

/// <summary>
/// Derives the object-centric model from the mirror.
/// <para>
/// The work is one SQL function, called in batches. The rules are joins and CASE expressions, and keeping them in
/// SQL means the thing that runs is the thing a reviewer reads — a row-by-row projector in C# would be slower and
/// would put the rule two translations away from the data it describes.
/// </para>
/// <para>
/// <b>Re-projection is free and deliberate.</b> Bumping <see cref="ProjectorVersion"/> makes every mirrored event
/// pending again, so a changed rule is applied to the whole history without going near the source system. That is
/// the reason the mirror exists as its own layer.
/// </para>
/// </summary>
public sealed class ProjectionService
{
    /// <summary>
    /// Bump this whenever a projection rule changes. Rows carry the version they were projected with, so a bump
    /// re-projects everything on the next run instead of leaving old rows derived by old rules.
    /// </summary>
    public const int ProjectorVersion = 1;

    private const int BatchSize = 5000;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<ProjectionService> _logger;

    public ProjectionService(
        IDbContextFactory<AppDbContext> factory,
        ProcessAnalyzerOptions options,
        ILogger<ProjectionService> logger
    )
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Projects everything pending and refreshes the analytics views. Returns how many events were projected.
    /// </summary>
    public async Task<int> ProjectPendingAsync(CancellationToken ct)
    {
        // Two projections at once would each re-read the other's pending set and duplicate the work.
        if (!await Gate.WaitAsync(0, ct))
            return 0;

        try
        {
            var total = 0;
            while (true)
            {
                var projected = await ProjectBatchAsync(ct);
                if (projected == 0)
                    break;

                total += projected;
            }

            if (total > 0)
            {
                await RefreshViewsAsync(ct);
                _logger.LogInformation("Projected {Count} events and refreshed the analytics views", total);
            }

            return total;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Discards the derived model and rebuilds it from the mirror. The mirror is never touched, so this costs
    /// nothing but local time — which is the whole point of keeping the raw journal as its own layer.
    /// </summary>
    public async Task<int> RebuildAsync(CancellationToken ct)
    {
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE ocel.e2o, ocel.event, ocel.object, ocel.type_registry", ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE journal.event SET projection_version = 0", ct);
        }

        return await ProjectPendingAsync(ct);
    }

    private async Task<int> ProjectBatchAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ocel.project_pending(@key, @version, @batch)";
        command.CommandTimeout = 300;
        command.Parameters.AddWithValue("key", ActorHashKey);
        command.Parameters.AddWithValue("version", ProjectorVersion);
        command.Parameters.AddWithValue("batch", BatchSize);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private async Task RefreshViewsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(600);

        // Not CONCURRENTLY: that needs the view to be populated already and takes a heavier lock path. This store
        // has a single reader-facing instance and a refresh of a few seconds, so the simple form is honest.
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.object_timeline", ct);
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.object_lifecycle", ct);
    }

    /// <summary>
    /// The key that turns a performer id into a stable pseudonym. Rotating it re-pseudonymizes everything and
    /// breaks handover history continuity, so an unset key falls back to a fixed local value rather than a random
    /// one — a random key per restart would silently split one person into many.
    /// </summary>
    private string ActorHashKey =>
        string.IsNullOrWhiteSpace(_options.ActorHashKey) ? "local-development-actor-key" : _options.ActorHashKey;
}

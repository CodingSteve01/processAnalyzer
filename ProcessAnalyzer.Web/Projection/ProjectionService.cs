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

    /// <summary>When the views were last rebuilt. Static, because the ceiling is per process, not per scope.</summary>
    private static DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

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
    public async Task<int> ProjectPendingAsync(CancellationToken ct) => await ProjectPendingAsync(false, ct);

    /// <param name="force">Refresh the views even when the interval has not elapsed. A reader waiting for an answer
    /// outranks the ceiling that exists to protect the pull loop.</param>
    public async Task<int> ProjectPendingAsync(bool force, CancellationToken ct)
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
                // Rebuilding all four views costs the whole log, not the new part of it, and the pull runs every minute.
                // At the current volume that is a second; at ten times it is ten, and the loop would spend its life
                // rebuilding. So it is bounded per unit of time: at most once per RefreshIntervalSeconds, and always
                // when a reader asks through /api/projection/run.
                //
                // This is a ceiling, not a fix. The fix is to maintain the timeline and the lifecycle per touched case
                // instead of rebuilding them, and that is the prerequisite for mirroring years rather than days.
                var since = DateTimeOffset.UtcNow - _lastRefresh;
                if (force || since >= TimeSpan.FromSeconds(_options.RefreshIntervalSeconds))
                {
                    await RefreshViewsAsync(ct);
                    _lastRefresh = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Projected {Count} events and refreshed the analytics views", total);
                }
                else
                {
                    _logger.LogDebug(
                        "Projected {Count} events; the views were refreshed {Age:0}s ago and stay as they are",
                        total,
                        since.TotalSeconds
                    );
                }
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
        // Order matters: the lifecycle measures durations in the clock of its process and decides whether a case is
        // finished from that process's end steps, and both of those are derived from the timeline. Refreshing the
        // lifecycle first would date it against the log it was built from.
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.object_timeline", ct);
        // Who the actors are, before anything asks whether a person was involved. The lifecycle reads it through
        // analytics.is_person, so a stale identity would date every automation figure by one run.
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW dim.actor_identity", ct);
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.process_clock", ct);
        await db.Database.ExecuteSqlRawAsync("REFRESH MATERIALIZED VIEW analytics.derived_end_activity", ct);
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

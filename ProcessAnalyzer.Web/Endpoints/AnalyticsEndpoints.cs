using ProcessAnalyzer.Web.Analytics;
using ProcessAnalyzer.Web.Export;
using ProcessAnalyzer.Web.Projection;
using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Web.Endpoints;

/// <summary>
/// The analytical surface. One endpoint per question, each scoped to a single object type.
/// </summary>
public static class AnalyticsEndpoints
{
    public static WebApplication MapAnalyticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/inventory", (AnalyticsRepository repo, CancellationToken token) => repo.InventoryAsync(token));

        group.MapPost(
            "/projection/rebuild",
            async (ProjectionService projection, IHostApplicationLifetime lifetime) =>
            {
                // Detached from the request: a rebuild walks the whole mirror, and a closed browser tab must not
                // abort it half-way and leave the derived model partly rebuilt.
                _ = Task.Run(() => projection.RebuildAsync(lifetime.ApplicationStopping));
                return Results.Accepted(value: new { accepted = true });
            }
        );

        group.MapPost(
            "/projection/run",
            async (ProjectionService projection, CancellationToken token) =>
                Results.Ok(new { projected = await projection.ProjectPendingAsync(token) })
        );

        group.MapPost(
            "/export/ocel",
            async (OcelSqliteExporter exporter, CancellationToken token) =>
            {
                var path = Path.Combine(ArtifactPath.Directory, "log.sqlite");
                return Results.Ok(await exporter.ExportAsync(path, token));
            }
        );

        // Discovery: what exists at all. None of these take an object type — finding out which processes there are
        // is the question that comes before choosing one.
        group.MapGet("/discovery/processes", (DiscoveryRepository repo, CancellationToken t) => repo.ProcessesAsync(t));
        group.MapGet("/discovery/decisions", (DiscoveryRepository repo, CancellationToken t) => repo.DecisionsAsync(t));
        group.MapGet(
            "/discovery/collaboration",
            (DiscoveryRepository repo, CancellationToken t) => repo.CollaborationAsync(t)
        );
        group.MapGet("/discovery/calendar", (DiscoveryRepository repo, CancellationToken t) => repo.CalendarAsync(t));
        group.MapGet("/discovery/roles", (DiscoveryRepository repo, CancellationToken t) => repo.RolesAsync(t));
        group.MapGet(
            "/discovery/who-does-what",
            (DiscoveryRepository repo, CancellationToken t) => repo.WhoDoesWhatAsync(t)
        );
        group.MapGet("/discovery/handovers", (DiscoveryRepository repo, CancellationToken t) => repo.HandoversAsync(t));
        group.MapGet(
            "/discovery/role-handovers",
            (DiscoveryRepository repo, CancellationToken t) => repo.RoleHandoverMatrixAsync(t)
        );
        group.MapGet("/discovery/coverage", (DiscoveryRepository repo, CancellationToken t) => repo.CoverageAsync(t));

        group.MapPost(
            "/directory/sync",
            async (DirectorySync directory, CancellationToken token) => Results.Ok(await directory.SyncAsync(token))
        );

        // The single case. Scoped like everything else, because a case belongs to exactly one process.
        group.MapGet(
            "/cases",
            async (
                CaseRepository repo,
                string? objectType,
                string? lastActivity,
                string? search,
                string? from,
                string? until,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(objectType)
                    ? Results.BadRequest(new { error = "objectType is required" })
                    : Results.Ok(
                        new
                        {
                            objectType,
                            rows = await repo.ListAsync(
                                objectType,
                                Period.FromQuery(from, until),
                                lastActivity,
                                search,
                                t
                            ),
                        }
                    )
        );

        group.MapGet(
            "/case/{objectId}",
            async (CaseRepository repo, string objectId, CancellationToken t) =>
                Results.Ok(new { objectId, rows = await repo.TimelineAsync(objectId, t) })
        );

        group.MapGet(
            "/trend",
            async (CaseRepository repo, string? objectType, string? from, string? until, CancellationToken t) =>
                string.IsNullOrWhiteSpace(objectType)
                    ? Results.BadRequest(new { error = "objectType is required" })
                    : Results.Ok(
                        new { objectType, rows = await repo.TrendAsync(objectType, Period.FromQuery(from, until), t) }
                    )
        );

        MapScoped(group, "/activities", (repo, type, period, token) => repo.ActivitiesAsync(type, period, token));
        MapScoped(group, "/throughput", (repo, type, period, token) => repo.ThroughputAsync(type, period, token));
        MapScoped(group, "/transitions", (repo, type, period, token) => repo.TransitionsAsync(type, period, token));
        MapScoped(group, "/rework", (repo, type, period, token) => repo.ReworkAsync(type, period, token));
        MapScoped(
            group,
            "/negative-outcomes",
            (repo, type, period, token) => repo.NegativeOutcomesAsync(type, period, token)
        );
        MapScoped(group, "/variants", (repo, type, period, token) => repo.VariantsAsync(type, period, token));
        MapScoped(group, "/automation", (repo, type, period, token) => repo.AutomationAsync(type, period, token));
        MapScoped(
            group,
            "/automation-candidates",
            (repo, type, period, token) => repo.AutomationCandidatesAsync(type, period, token)
        );
        MapScoped(group, "/handovers", (repo, type, period, token) => repo.HandoversAsync(type, period, token));
        MapScoped(group, "/endpoints", (repo, type, period, token) => repo.EndpointsAsync(type, period, token));

        return app;
    }

    /// <summary>
    /// Registers an endpoint that refuses to answer without an object type.
    /// </summary>
    /// <remarks>
    /// Not a convenience default: flattening an object-centric log across types double-counts every event that
    /// touches several objects and makes unrelated events look sequential. An answer computed that way is wrong,
    /// not approximate, so the missing parameter is a 400 rather than an "all types" fallback.
    /// </remarks>
    private static void MapScoped(
        RouteGroupBuilder group,
        string route,
        Func<AnalyticsRepository, string, Period, CancellationToken, Task<List<Dictionary<string, object?>>>> query
    ) =>
        group.MapGet(
            route,
            async (
                AnalyticsRepository repo,
                string? objectType,
                string? from,
                string? until,
                CancellationToken token
            ) =>
            {
                if (string.IsNullOrWhiteSpace(objectType))
                    return Results.BadRequest(
                        new
                        {
                            error = "objectType is required — an object-centric log must not be aggregated across types",
                        }
                    );

                var period = Period.FromQuery(from, until);

                return Results.Ok(
                    new
                    {
                        objectType,
                        // Echoed back so the reader sees the window that was actually applied, not the one they meant.
                        from = period.From,
                        until = period.Until,
                        rows = await query(repo, objectType, period, token),
                    }
                );
            }
        );
}

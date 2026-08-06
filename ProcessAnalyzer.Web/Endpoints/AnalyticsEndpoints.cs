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

        // The groups a question can be narrowed to. Only groups that actually did something: a directory has hundreds
        // of groups, most of which never appear in the log, and a select box of empty options is a worse answer than
        // none.
        group.MapGet("/groups", (AnalyticsRepository repo, CancellationToken token) => repo.ActorGroupsAsync(token));

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
                Results.Ok(new { projected = await projection.ProjectPendingAsync(force: true, token) })
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
        group.MapGet(
            "/discovery/processes",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.ProcessesAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet(
            "/discovery/decisions",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.DecisionsAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet(
            "/discovery/collaboration",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.CollaborationAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet("/discovery/calendar", (DiscoveryRepository repo, CancellationToken t) => repo.CalendarAsync(t));
        group.MapGet(
            "/discovery/roles",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.RolesAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet(
            "/discovery/who-does-what",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.WhoDoesWhatAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet(
            "/discovery/handovers",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.HandoversAsync(Scope.FromQuery(from, until, group), t)
        );
        group.MapGet(
            "/discovery/role-handovers",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.RoleHandoverMatrixAsync(Scope.FromQuery(from, until, group), t)
        );
        // The landscape: what this company does end to end, from the events that touch two kinds of object at once.
        group.MapGet(
            "/discovery/landscape",
            (DiscoveryRepository repo, string? from, string? until, string? group, CancellationToken t) =>
                repo.LandscapeAsync(Scope.FromQuery(from, until, group), t)
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
                string? withActivity,
                string? search,
                string? from,
                string? until,
                string? group,
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
                                Scope.FromQuery(from, until, group),
                                lastActivity,
                                withActivity,
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
            async (
                CaseRepository repo,
                string? objectType,
                string? from,
                string? until,
                string? group,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(objectType)
                    ? Results.BadRequest(new { error = "objectType is required" })
                    : Results.Ok(
                        new
                        {
                            objectType,
                            rows = await repo.TrendAsync(objectType, Scope.FromQuery(from, until, group), t),
                        }
                    )
        );

        MapScoped(group, "/activities", (repo, type, scope, token) => repo.ActivitiesAsync(type, scope, token));
        MapScoped(group, "/throughput", (repo, type, scope, token) => repo.ThroughputAsync(type, scope, token));
        MapScoped(group, "/transitions", (repo, type, scope, token) => repo.TransitionsAsync(type, scope, token));
        MapScoped(group, "/rework", (repo, type, scope, token) => repo.ReworkAsync(type, scope, token));
        MapScoped(
            group,
            "/negative-outcomes",
            (repo, type, scope, token) => repo.NegativeOutcomesAsync(type, scope, token)
        );
        MapScoped(group, "/variants", (repo, type, scope, token) => repo.VariantsAsync(type, scope, token));
        MapScoped(group, "/automation", (repo, type, scope, token) => repo.AutomationAsync(type, scope, token));
        MapScoped(
            group,
            "/automation-candidates",
            (repo, type, scope, token) => repo.AutomationCandidatesAsync(type, scope, token)
        );
        // One person, at the level of the step. Not through MapScoped: there is no object type here, a person works
        // across processes.
        group.MapGet(
            "/actor",
            async (
                AnalyticsRepository repo,
                string? key,
                string? from,
                string? until,
                string? group,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(key)
                    ? Results.BadRequest(new { error = "key ist erforderlich" })
                    : Results.Ok(
                        new
                        {
                            key,
                            name = await repo.ActorNameAsync(key, t),
                            rows = await repo.ActorProfileAsync(key, Scope.FromQuery(from, until, group), t),
                        }
                    )
        );

        // Not through MapScoped: this one needs the step as well as the process.
        group.MapGet(
            "/activity-trend",
            async (
                AnalyticsRepository repo,
                string? objectType,
                string? activity,
                string? from,
                string? until,
                string? group,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(objectType) || string.IsNullOrWhiteSpace(activity)
                    ? Results.BadRequest(new { error = "objectType und activity sind erforderlich" })
                    : Results.Ok(
                        new
                        {
                            objectType,
                            activity,
                            rows = await repo.ActivityTrendAsync(
                                objectType,
                                activity,
                                Scope.FromQuery(from, until, group),
                                t
                            ),
                        }
                    )
        );

        MapScoped(group, "/drivers", (repo, type, scope, token) => repo.DriversAsync(type, scope, token));
        MapScoped(group, "/handovers", (repo, type, scope, token) => repo.HandoversAsync(type, scope, token));
        MapScoped(group, "/endpoints", (repo, type, scope, token) => repo.EndpointsAsync(type, scope, token));

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
        Func<AnalyticsRepository, string, Scope, CancellationToken, Task<List<Dictionary<string, object?>>>> query
    ) =>
        group.MapGet(
            route,
            async (
                AnalyticsRepository repo,
                string? objectType,
                string? from,
                string? until,
                string? group,
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

                var scope = Scope.FromQuery(from, until, group);

                return Results.Ok(
                    new
                    {
                        objectType,
                        // Echoed back so the reader sees the scope that was actually applied, not the one they meant.
                        from = scope.Period.From,
                        until = scope.Period.Until,
                        group = scope.Group,
                        rows = await query(repo, objectType, scope, token),
                    }
                );
            }
        );
}

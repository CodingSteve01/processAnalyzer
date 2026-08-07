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

        group.MapGet(
            "/inventory",
            (
                AnalyticsRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken token
            ) =>
                repo.InventoryAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    token
                )
        );

        // The groups a question can be narrowed to. Only groups that actually did something: a directory has hundreds
        // of groups, most of which never appear in the log, and a select box of empty options is a worse answer than
        // none.
        group.MapGet("/groups", (AnalyticsRepository repo, CancellationToken token) => repo.ActorGroupsAsync(token));

        // What a case IS, as opposed to what happened to it: its kind, its area, purchase or sale. The options for
        // the property filter, with their coverage — a classification that only a handful of cases carry is a gap to
        // report, not a filter to offer silently.
        group.MapGet("/properties", (AnalyticsRepository repo, CancellationToken token) => repo.PropertiesAsync(token));

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
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.ProcessesAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet(
            "/discovery/decisions",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.DecisionsAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet(
            "/discovery/collaboration",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.CollaborationAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet("/discovery/calendar", (DiscoveryRepository repo, CancellationToken t) => repo.CalendarAsync(t));
        group.MapGet(
            "/discovery/roles",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) => repo.RolesAsync(Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue), t)
        );
        group.MapGet(
            "/discovery/who-does-what",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.WhoDoesWhatAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet(
            "/discovery/handovers",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.HandoversAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet(
            "/discovery/role-handovers",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.RoleHandoverMatrixAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        // The landscape: what this company does end to end, from the events that touch two kinds of object at once.
        group.MapGet(
            "/discovery/landscape",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.LandscapeAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet("/discovery/coverage", (DiscoveryRepository repo, CancellationToken t) => repo.CoverageAsync(t));
        // Which process a step belongs to. Read once per screen so the combined pictures can be clicked into.
        group.MapGet("/discovery/step-home", (DiscoveryRepository repo, CancellationToken t) => repo.StepHomeAsync(t));
        // The release ladder: which stages exist, who holds them, and in which order they are actually climbed.
        group.MapGet(
            "/discovery/release-stages",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.ReleaseStagesAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );
        group.MapGet(
            "/discovery/release-chain",
            (
                DiscoveryRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                repo.ReleaseChainAsync(
                    Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                    t
                )
        );

        group.MapPost(
            "/directory/sync",
            async (DirectorySync directory, CancellationToken token) => Results.Ok(await directory.SyncAsync(token))
        );

        group.MapPost(
            "/saved-views/sync",
            async (SavedViewSync views, CancellationToken token) => Results.Ok(await views.SyncAsync(token))
        );

        // Who somebody IS, as opposed to what they did. Many people work in the same module and do completely different
        // things with it, so a figure grouped by module is an average over unrelated roles. These three read the views
        // people set up for themselves, which is the only place that distinction is already written down.
        group.MapGet("/roles/profiles", (RoleRepository repo, CancellationToken t) => repo.ProfilesAsync(t));
        group.MapGet("/roles/vocabulary", (RoleRepository repo, CancellationToken t) => repo.VocabularyAsync(t));
        group.MapGet("/roles/screens", (RoleRepository repo, CancellationToken t) => repo.ScreensAsync(t));

        // Which column layouts exist per screen and who shares one — the question a user-interface rebuild has to
        // answer before anybody draws a screen.
        group.MapGet("/roles/layouts", (RoleRepository repo, CancellationToken t) => repo.LayoutsAsync(t));
        group.MapGet("/roles/layout-sharing", (RoleRepository repo, CancellationToken t) => repo.LayoutSharingAsync(t));

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
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
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
                                Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                                lastActivity,
                                withActivity,
                                search,
                                t
                            ),
                        }
                    )
        );

        // The whole transaction around one case: its own steps plus those of everything it touches. An object-centric
        // log has no single case by design, and this is where a person needs one anyway.
        group.MapGet(
            "/case/{objectId}/chain",
            async (CaseRepository repo, string objectId, CancellationToken t) =>
                Results.Ok(new { objectId, rows = await repo.ChainAsync(objectId, t) })
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
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(objectType)
                    ? Results.BadRequest(new { error = "objectType is required" })
                    : Results.Ok(
                        new
                        {
                            objectType,
                            rows = await repo.TrendAsync(
                                objectType,
                                Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                                t
                            ),
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
        // The three screens that look at now rather than backwards. Everything else in this tool is a retrospective,
        // which is useful in a workshop and useless at ten in the morning.
        group.MapGet(
            "/queue",
            async (
                AnalyticsRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                Results.Ok(
                    new
                    {
                        rows = await repo.QueueAsync(
                            Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                            t
                        ),
                    }
                )
        );
        group.MapGet(
            "/anomalies",
            async (
                AnalyticsRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                Results.Ok(
                    new
                    {
                        rows = await repo.AnomaliesAsync(
                            Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                            t
                        ),
                    }
                )
        );
        group.MapGet(
            "/four-eyes",
            async (
                AnalyticsRepository repo,
                string? from,
                string? until,
                string? group,
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                Results.Ok(
                    new
                    {
                        rows = await repo.FourEyesAsync(
                            Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                            t
                        ),
                    }
                )
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
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
                CancellationToken t
            ) =>
                string.IsNullOrWhiteSpace(key)
                    ? Results.BadRequest(new { error = "key ist erforderlich" })
                    : Results.Ok(
                        new
                        {
                            key,
                            name = await repo.ActorNameAsync(key, t),
                            rows = await repo.ActorProfileAsync(
                                key,
                                Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
                                t
                            ),
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
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
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
                                Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue),
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
                string? hasStep,
                string? withoutStep,
                string? property,
                string? propertyValue,
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

                var scope = Scope.FromQuery(from, until, group, hasStep, withoutStep, property, propertyValue);

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

using ProcessAnalyzer.Web.Options;
using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Web.Endpoints;

public static class SyncEndpoints
{
    // Manual triggers are serialised here because the endpoint is where accidental parallelism comes from:
    // a double-clicked button, a second browser tab, a retrying monitoring probe. 0 = idle, 1 = running.
    private static int _manualRunActive;

    public static WebApplication MapSyncEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sync");

        group.MapGet(
            "/status",
            async (JournalMirror mirror, ProcessAnalyzerOptions opts, CancellationToken token) =>
            {
                var status = await mirror.GetStatusAsync(token);
                return Results.Ok(
                    new
                    {
                        // The page must be able to distinguish "nothing mirrored yet" from "no source at all";
                        // both look like zeros otherwise.
                        sourceConfigured = !string.IsNullOrWhiteSpace(opts.SourceConnectionString),
                        pullIntervalSeconds = opts.PullIntervalSeconds,
                        lagSeconds = opts.LagSeconds,
                        gapSweepDays = opts.GapSweepDays,
                        manualRunActive = Volatile.Read(ref _manualRunActive) == 1,
                        sync = status,
                    }
                );
            }
        );

        group.MapPost(
            "/run",
            (
                JournalPullService pull,
                ProcessAnalyzerOptions opts,
                IHostApplicationLifetime lifetime,
                ILoggerFactory loggerFactory,
                string? kind
            ) =>
            {
                if (string.IsNullOrWhiteSpace(opts.SourceConnectionString))
                    return Results.Conflict(new { message = "No source configured" });

                var sweep = string.Equals(kind, "sweep", StringComparison.OrdinalIgnoreCase);

                if (Interlocked.CompareExchange(ref _manualRunActive, 1, 0) != 0)
                    return Results.Conflict(new { message = "A pull is already running" });

                StartDetached(pull, lifetime, loggerFactory.CreateLogger(typeof(SyncEndpoints)), sweep);

                return Results.Accepted("/api/sync/status", new { accepted = true, kind = sweep ? "sweep" : "pull" });
            }
        );

        return app;
    }

    // Runs on ApplicationStopping, never on the request token. A pull that dies halfway because someone closed
    // the tab would leave written events behind an un-advanced watermark, or worse, an advanced watermark with
    // unwritten events: the exact loss this phase is meant to prove impossible.
    private static void StartDetached(
        JournalPullService pull,
        IHostApplicationLifetime lifetime,
        ILogger log,
        bool sweep
    )
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var token = lifetime.ApplicationStopping;
                var result = sweep ? await pull.SweepOnceAsync(token) : await pull.PullOnceAsync(token);
                log.LogInformation(
                    "Manual {Kind} finished: ran={Ran} events={Events} objects={Objects} "
                        + "heldBack={HeldBack} pages={Pages} gaps={Gaps} error={Error}",
                    sweep ? "sweep" : "pull",
                    result.Ran,
                    result.Events,
                    result.Objects,
                    result.HeldBack,
                    result.Pages,
                    result.GapsFound,
                    result.Error
                );
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("Manual {Kind} cancelled by shutdown.", sweep ? "sweep" : "pull");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Manual {Kind} failed.", sweep ? "sweep" : "pull");
            }
            finally
            {
                Volatile.Write(ref _manualRunActive, 0);
            }
        });
    }
}

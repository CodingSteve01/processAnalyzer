using System.Reflection;
using ProcessAnalyzer.Web.Options;
using ProcessAnalyzer.Web.Sync;

namespace ProcessAnalyzer.Web.Endpoints;

public static class HealthEndpoints
{
    private static readonly string AppVersion =
        Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "dev";

    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // Hand-rolled instead of AddHealthChecks: "the process answers" is worthless for a mirror.
        // The only question worth asking is whether events are still arriving, and that answer lives in
        // our own sync tables.
        app.MapGet(
            "/health",
            async (JournalMirror mirror, ProcessAnalyzerOptions opts, CancellationToken token) =>
            {
                var sourceConfigured = !string.IsNullOrWhiteSpace(opts.SourceConnectionString);
                var status = await mirror.GetStatusAsync(token);

                // Guard against a misconfigured interval of 0, which would make every pull instantly "too old".
                var interval = TimeSpan.FromSeconds(Math.Max(1, opts.PullIntervalSeconds));
                var maxAge = interval * 3;
                var age = status.LastSuccessfulRunAt is null
                    ? null
                    : (TimeSpan?)(DateTime.UtcNow - status.LastSuccessfulRunAt.Value);

                var reason = DetermineFailure(sourceConfigured, status.LastError, age, maxAge);

                var body = new
                {
                    status = reason is null ? "healthy" : "unhealthy",
                    version = AppVersion,
                    sourceConfigured,
                    reason,
                    maxPullAgeSeconds = (long)maxAge.TotalSeconds,
                    lastSuccessAgeSeconds = age is null ? (long?)null : (long)age.Value.TotalSeconds,
                    // Passed through verbatim, so /health and /api/sync/status can never tell different stories
                    // about watermark, mirrored maximum, held-back events, gaps and the last run.
                    sync = status,
                };

                return reason is null ? Results.Ok(body) : Results.Json(body, statusCode: 503);
            }
        );

        app.MapGet("/api/version", () => Results.Ok(new { version = AppVersion }));

        return app;
    }

    private static string? DetermineFailure(bool sourceConfigured, string? lastError, TimeSpan? age, TimeSpan maxAge)
    {
        // No source means nothing is being mirrored. Reporting that as healthy would hide a dead sidecar
        // behind a green check for as long as nobody looks at the counters.
        if (!sourceConfigured)
            return "source not configured";

        if (!string.IsNullOrWhiteSpace(lastError))
            return $"last run failed: {lastError}";

        if (age is null)
            return "no successful pull yet";

        if (age > maxAge)
            return $"last successful pull is {(long)age.Value.TotalSeconds}s old (limit {(long)maxAge.TotalSeconds}s)";

        return null;
    }
}

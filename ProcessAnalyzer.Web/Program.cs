using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Analytics;
using ProcessAnalyzer.Web.Auth;
using ProcessAnalyzer.Web.Data;
using ProcessAnalyzer.Web.Endpoints;
using ProcessAnalyzer.Web.Export;
using ProcessAnalyzer.Web.Options;
using ProcessAnalyzer.Web.Projection;
using ProcessAnalyzer.Web.Sync;
using ProcessAnalyzer.Web.Vocabulary;

// A side entrance so an operator can produce a password hash without a separate tool. The hash goes into the
// configuration; the password itself is stored nowhere.
//
// It refuses an empty or short password instead of hashing it. The first version did not, and a password piped in
// from a script that never reached stdin produced a valid-looking hash of the empty string — a dashboard that opens
// for anybody who submits nothing, with no sign that anything went wrong.
if (args is ["hash-password", ..])
{
    if (!Console.IsInputRedirected)
        Console.Write("Password: ");

    var entered = Console.ReadLine() ?? Environment.GetEnvironmentVariable("PA_NEW_PASSWORD") ?? string.Empty;
    if (entered.Length < 12)
    {
        await Console.Error.WriteLineAsync(
            $"Refusing to hash a password of {entered.Length} characters. Provide at least 12 on stdin, or set "
                + "PA_NEW_PASSWORD. An empty password here becomes a dashboard that opens for everybody."
        );
        return 2;
    }

    Console.WriteLine(DashboardAuth.Hash(entered));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// The container sets ASPNETCORE_URLS; without it a bare `dotnet run` would bind the SDK default (5000/5001)
// and every local script that talks to 5100 would silently hit nothing.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5100");
}

builder.Services.Configure<ProcessAnalyzerOptions>(
    builder.Configuration.GetSection(ProcessAnalyzerOptions.SectionName)
);

// Second, eager binding of the same section: the pull service and the endpoints take the bare instance so
// every component sees the values that were validated at startup. Nothing in phase 1 may change interval,
// batch size or lag while a pull is in flight — a mid-run change would move the watermark rule under our feet.
var options =
    builder.Configuration.GetSection(ProcessAnalyzerOptions.SectionName).Get<ProcessAnalyzerOptions>()
    ?? new ProcessAnalyzerOptions();
builder.Services.AddSingleton(options);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    throw new InvalidOperationException(
        "PostgreSQL connection string 'DefaultConnection' is required. "
            + "Set it in appsettings.json or via environment variable ConnectionStrings__DefaultConnection"
    );
}

builder.Services.AddDbContextFactory<AppDbContext>(cfg =>
    cfg.UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(120))
);

// The reader refuses to construct without a read-only connection string, which is right — but an unconfigured
// sidecar still has to start, or the "no source configured" state the dashboard and /health report could never
// actually be reached. The stand-in throws on every read instead of returning empty pages: an empty page looks
// exactly like "nothing new in the journal", and that is how an unconfigured mirror would report itself healthy.
if (string.IsNullOrWhiteSpace(options.SourceConnectionString))
    builder.Services.AddSingleton<IJournalSource, UnconfiguredJournalSource>();
else
    builder.Services.AddSingleton<IJournalSource, SqlJournalReader>();
builder.Services.AddSingleton<JournalMirror>();
builder.Services.AddSingleton<ProjectionService>();
builder.Services.AddSingleton<AnalyticsRepository>();
builder.Services.AddSingleton<DiscoveryRepository>();
builder.Services.AddSingleton<CaseRepository>();
builder.Services.AddSingleton<DirectorySync>();
builder.Services.AddSingleton<OcelSqliteExporter>();
builder.Services.AddSingleton<VocabularyLoader>();

// One instance, two roles: the timer loop and the manual trigger endpoint must share the same object,
// otherwise the endpoint would start a pull that the hosted service knows nothing about.
builder.Services.AddSingleton<JournalPullService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JournalPullService>());

builder.Services.AddDashboardAuth(options);

builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});

var app = builder.Build();

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

// Migrate, never EnsureCreated. From phase 2 on this store holds hand-curated projection rules that exist
// nowhere else; if the only way to pick up a schema change were `docker compose down -v`, those rules would
// be destroyed on every upgrade. Migrations keep the data and the schema evolving together.
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

// After the migrations: they create ocel.label, the loader only fills it. Configuration, so it is re-read on every
// start — a corrected label must take effect by restarting the container, not by editing a table by hand.
await app.Services.GetRequiredService<VocabularyLoader>().LoadAsync(app.Lifetime.ApplicationStopping);

if (!await EnsureSourceIsReadOnlyAsync(app, options, startupLog))
{
    return 1;
}

app.UseResponseCompression();

// Client navigated away / request timeout → EF cancels the Postgres query → 57014.
// Catch it here so Kestrel doesn't log it as an unhandled exception.
app.Use(
    async (ctx, next) =>
    {
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 499; // Client Closed Request
        }
    }
);

// BEFORE the static files, not after. Static files are served by their own middleware and never reach the
// endpoints, so a check placed behind them hands out index.html to anybody who asks — which is exactly what the
// first version did: /api answered 401 and / answered 200. The login page and its two assets are on the open list.
app.UseDashboardAuth(options);

app.UseDefaultFiles();
app.UseStaticFiles(
    new StaticFileOptions
    {
        // Revalidate every asset on every load, and never serve one from cache without asking.
        //
        // This used to be max-age=3600. The markup carries no version in its asset URLs, so for an hour after an
        // update a browser combined the new HTML with the previous JS — new buttons wired to code that did not
        // contain their handlers yet, doing nothing at all when clicked, with nothing in the console to explain it.
        // A conditional request per asset costs a 304 on a local network; a UI whose halves disagree costs an hour of
        // looking for a bug that is not in the code.
        OnPrepareResponse = ctx =>
        {
            var ext = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
            if (ext is ".js" or ".css" or ".html" or ".woff2" or ".woff" or ".svg")
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        },
    }
);

// The identity switch lives in the database because SQL functions read it, but the container's environment is what
// decides it — otherwise a value set once by hand would outlive the configuration that is supposed to govern it.
await using (var identityScope = app.Services.CreateAsyncScope())
{
    var factory = identityScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.ExecuteSqlRawAsync(
        "INSERT INTO analytics.setting (key, value) VALUES ('show_actor_identity', {0}) "
            + "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
        options.ShowActorIdentity ? "true" : "false"
    );
}

// An unset hash key falls back to a constant that is written in the source, so the pseudonyms it produces are
// reversible by anyone holding a list of user ids. That is fine for a demo run and not fine anywhere else, and the
// only thing worse than the weak default is a weak default nobody mentions.
if (string.IsNullOrWhiteSpace(options.ActorHashKey))
    app.Logger.LogWarning(
        "ProcessAnalyzer:ActorHashKey is not set — pseudonyms use the built-in development key and are reversible. "
            + "Generate one with 'openssl rand -base64 32' and keep it: changing it later re-pseudonymizes everyone."
    );

app.MapLoginEndpoints(options);

app.MapHealthEndpoints();
app.MapSyncEndpoints();
app.MapAnalyticsEndpoints();
app.MapMiningEndpoints();
app.MapVocabularyEndpoints();

app.Run();
return 0;

// The rule: this sidecar reads the operational database and never writes to it. Not one analytical query,
// not one "quick fix" UPDATE, not now and not in phase 3 when someone is tempted to enrich the source.
// A rule that only lives in a document is not a rule, so it is checked against the live login before the
// host accepts a single request. Refusing to start is the point: a write-capable login that nobody notices
// is exactly the failure mode this guard exists for.
static async Task<bool> EnsureSourceIsReadOnlyAsync(WebApplication app, ProcessAnalyzerOptions options, ILogger log)
{
    if (string.IsNullOrWhiteSpace(options.SourceConnectionString))
    {
        log.LogWarning(
            "No source configured — skipping the read-only login check. Nothing will be mirrored; "
                + "the UI reports the missing source instead of showing empty counters as if they were data."
        );
        return true;
    }

    var source = app.Services.GetRequiredService<IJournalSource>();
    bool writeCapable;
    try
    {
        writeCapable = await source.IsWriteCapableAsync(app.Lifetime.ApplicationStopping);
    }
    catch (Exception ex)
    {
        // A guard that could not run has not passed. Starting anyway would mean mirroring from a login whose
        // permissions we never verified.
        log.LogCritical(ex, "Could not verify that the source login is read-only. Refusing to start.");
        return false;
    }

    if (!writeCapable)
    {
        log.LogInformation("Source login verified as read-only.");
        return true;
    }

    if (!options.AllowWriteCapableLogin)
    {
        log.LogCritical(
            "The configured source login can write to the source database. Refusing to start. "
                + "Use a read-only login, or set ProcessAnalyzer:AllowWriteCapableLogin=true if you accept the risk."
        );
        return false;
    }

    log.LogWarning(
        "The configured source login can write to the source database. Starting anyway because "
            + "AllowWriteCapableLogin is set. Every query this process issues must stay read-only."
    );
    return true;
}

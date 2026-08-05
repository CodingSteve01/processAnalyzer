using System.Text.Json;

namespace ProcessAnalyzer.Web.Endpoints;

/// <summary>
/// Serves what the miner produced.
/// <para>
/// The application does not run pm4py and must not: it is AGPL-3.0, it pulls in a scientific stack, and a mining
/// run takes minutes. The two exchange files through a shared directory, so this endpoint only reports what is
/// there and how old it is — a stale model is a normal state and has to be visible as one, not hidden behind a
/// picture that looks current.
/// </para>
/// </summary>
public static class MiningEndpoints
{
    private static readonly string[] Models = ["ocdfg-frequency.svg", "ocdfg-performance.svg", "ocpn.svg"];

    public static WebApplication MapMiningEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/mining/status",
            (IWebHostEnvironment _) =>
            {
                var directory = ArtifactPath.Directory;
                var models = Models
                    .Select(name => new FileInfo(Path.Combine(directory, name)))
                    .Select(file => new
                    {
                        name = file.Name,
                        available = file.Exists,
                        ageMinutes = file.Exists
                            ? (int)(DateTime.UtcNow - file.LastWriteTimeUtc).TotalMinutes
                            : (int?)null,
                        url = $"/api/mining/model/{file.Name}",
                    })
                    .ToList();

                var statsFile = new FileInfo(Path.Combine(directory, "stats.json"));
                object? stats = null;
                if (statsFile.Exists)
                {
                    // Read through, not re-serialized: whatever the miner reported is what the page shows, so a new
                    // statistic appears without a change on this side.
                    using var stream = statsFile.OpenRead();
                    stats = JsonSerializer.Deserialize<JsonElement>(stream);
                }

                return Results.Ok(
                    new
                    {
                        models,
                        stats,
                        minedAt = statsFile.Exists ? statsFile.LastWriteTimeUtc : (DateTime?)null,
                        hint = "docker compose --profile mining run --rm miner",
                    }
                );
            }
        );

        app.MapGet(
            "/api/mining/model/{name}",
            (string name) =>
            {
                // Only the known file names. The artifact directory is shared with another container, and letting a
                // path fragment through would turn that into a file-read primitive.
                if (!Models.Contains(name, StringComparer.Ordinal))
                    return Results.NotFound();

                var path = Path.Combine(ArtifactPath.Directory, name);
                return File.Exists(path) ? Results.File(path, "image/svg+xml") : Results.NotFound();
            }
        );

        return app;
    }
}

/// <summary>Where the application and the miner exchange files.</summary>
internal static class ArtifactPath
{
    public static string Directory
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("PROCESSANALYZER_ARTIFACTS") ?? "/artifacts";
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }
}

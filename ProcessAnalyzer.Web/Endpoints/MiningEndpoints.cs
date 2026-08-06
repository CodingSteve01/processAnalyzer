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
    private static readonly string[] Overviews = ["ocdfg-frequency.svg", "ocdfg-performance.svg", "ocpn.svg"];

    /// <summary>
    /// What may be read out of the artifact directory. A pattern rather than a fixed list, because the miner writes
    /// one set of diagrams per process and their names come from the data — but still a pattern and not a path, since
    /// the directory is shared with another container and a path fragment would turn this into a file-read primitive.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProcessModel = new(
        @"^process-[a-z0-9-]{1,60}-(frequency|performance|main)\.svg$",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static bool IsModel(string name) =>
        Overviews.Contains(name, StringComparer.Ordinal) || ProcessModel.IsMatch(name);

    private static IEnumerable<string> KnownModels(string directory)
    {
        var found = Directory
            .EnumerateFiles(directory, "process-*.svg")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => ProcessModel.IsMatch(name))
            .OrderBy(name => name, StringComparer.Ordinal);

        return Overviews.Concat(found);
    }

    public static WebApplication MapMiningEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/mining/status",
            (IWebHostEnvironment _) =>
            {
                var directory = ArtifactPath.Directory;
                var models = KnownModels(directory)
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
                if (!IsModel(name))
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

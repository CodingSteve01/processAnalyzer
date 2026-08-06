using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// A case is finished when its last step ends the process, and only otherwise after silence. The three-day rule alone
/// meant that against a young mirror every case counted as in flight, and everything computed on finished cases
/// reported on an empty set.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806140000_CaseClosure")]
public partial class CaseClosure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("022-case-closure.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Back to silence alone. The view has to be rebuilt either way, because it loses a column going back.
        migrationBuilder.Sql(
            """
            DROP MATERIALIZED VIEW IF EXISTS analytics.object_lifecycle;

            CREATE MATERIALIZED VIEW analytics.object_lifecycle AS
            SELECT
                t.object_id,
                t.object_type,
                MIN(t.ts) AS first_ts,
                MAX(t.ts) AS last_ts,
                COUNT(*) AS n_events,
                analytics.biz_seconds(MIN(t.ts), MAX(t.ts)) AS biz_seconds,
                EXTRACT(EPOCH FROM (MAX(t.ts) - MIN(t.ts))) AS wall_seconds,
                bool_or(t.actor_kind = 'human') AS has_human,
                MAX(t.ts) > (SELECT MAX(ts) - interval '3 days' FROM ocel.event) AS is_open
            FROM analytics.object_timeline t
            GROUP BY 1, 2;

            CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
            CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);

            DROP FUNCTION IF EXISTS analytics.case_is_open(text, text, timestamptz);
            DROP VIEW IF EXISTS analytics.derived_end_activity;
            DROP TABLE IF EXISTS analytics.process_closure;
            """
        );

    private static string Read(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly
            .GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

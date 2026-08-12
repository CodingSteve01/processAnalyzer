using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// A kind belongs to the actor, not to the event, and it can be corrected in the tool.
/// </summary>
/// <remarks>
/// The log carries the kind per event, from the source's performer type: a channel, not an identity. Every person who
/// ever confirmed something from a tablet therefore arrived as two actors, dim.actor_role had two rows for them, and
/// joining it to the event log counted their work twice: once under their group, once under "Gerät". That is where "43 %
/// of all steps are done by the machine" came from, over a log holding 336 device events.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260806200000_ActorKind")]
public partial class ActorKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("026-actor-kind.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Back to one row per (actor, kind) and to "the channel was human" as the measure of manual work. The lifecycle
        // has to be rebuilt either way, because has_human changes its meaning going back.
        migrationBuilder.Sql(
            """
            DROP VIEW IF EXISTS dim.actor_role;

            CREATE VIEW dim.actor_role AS
            SELECT e.actor_key,
                   e.actor_kind,
                   CASE
                       WHEN e.actor_kind = 'job' THEN 'Automatischer Job'
                       WHEN e.actor_kind = 'service' THEN 'Systemdienst'
                       WHEN e.actor_kind = 'external' THEN 'Fremdsystem'
                       WHEN e.actor_kind = 'device' THEN 'Gerät'
                       ELSE coalesce(p.role, 'Ohne Gruppe')
                   END AS role
            FROM (SELECT DISTINCT actor_key, actor_kind FROM ocel.event WHERE actor_key IS NOT NULL) e
            LEFT JOIN dim.actor_primary_role p ON p.actor_key = e.actor_key;

            DROP MATERIALIZED VIEW IF EXISTS analytics.object_lifecycle;

            CREATE MATERIALIZED VIEW analytics.object_lifecycle AS
            WITH last_step AS (
                SELECT DISTINCT ON (object_id) object_id, event_type, ts
                FROM analytics.object_timeline
                ORDER BY object_id, seq DESC
            )
            SELECT
                t.object_id,
                t.object_type,
                MIN(t.ts) AS first_ts,
                MAX(t.ts) AS last_ts,
                COUNT(*) AS n_events,
                analytics.duration_seconds(t.object_type, MIN(t.ts), MAX(t.ts)) AS duration_seconds,
                analytics.biz_seconds(MIN(t.ts), MAX(t.ts)) AS biz_seconds,
                EXTRACT(EPOCH FROM (MAX(t.ts) - MIN(t.ts))) AS wall_seconds,
                bool_or(t.actor_kind = 'human') AS has_human,
                MAX(l.event_type) AS last_activity,
                analytics.case_is_open(t.object_type, MAX(l.event_type), MAX(t.ts)) AS is_open
            FROM analytics.object_timeline t
            JOIN last_step l ON l.object_id = t.object_id
            GROUP BY 1, 2;

            CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
            CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);
            CREATE INDEX ix_lifecycle_open ON analytics.object_lifecycle (object_type, is_open);

            DROP FUNCTION IF EXISTS analytics.is_person(text);
            DROP MATERIALIZED VIEW IF EXISTS dim.actor_identity;
            DROP TABLE IF EXISTS dim.actor_kind_override;
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

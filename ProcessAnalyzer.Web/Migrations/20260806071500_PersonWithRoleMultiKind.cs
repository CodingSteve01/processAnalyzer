using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Naming an actor survives that actor having acted through more than one channel: the same person as a device and
/// at a desk used to fail every screen that names anyone.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806071500_PersonWithRoleMultiKind")]
public partial class PersonWithRoleMultiKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("019-person-with-role-multi-kind.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Back to the single-kind assumption from 008-machines-as-roles.sql.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION analytics.person_with_role(p_actor_key text)
            RETURNS text LANGUAGE sql STABLE AS $$
                SELECT CASE
                    WHEN p_actor_key IS NULL THEN NULL
                    WHEN (SELECT r.actor_kind FROM dim.actor_role r WHERE r.actor_key = p_actor_key) <> 'human'
                        THEN (SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key)
                    WHEN analytics.show_identity() THEN
                        coalesce((SELECT a.display_name FROM dim.actor a WHERE a.actor_key = p_actor_key), p_actor_key)
                        || coalesce(' (' || (SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key) || ')', '')
                    ELSE coalesce((SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key), p_actor_key)
                END;
            $$;
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

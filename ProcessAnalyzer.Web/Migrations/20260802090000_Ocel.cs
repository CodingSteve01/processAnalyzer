using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The object-centric model, the projection function and the analytics spine.
/// <para>
/// Hand-authored rather than scaffolded, because none of it is a model: functions, materialized views and seeded
/// rule tables have no EF representation, and expressing them as one would only produce a snapshot that lies about
/// what the database contains.
/// </para>
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260802090000_Ocel")]
public partial class Ocel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(Read("002-ocel.sql"));
        migrationBuilder.Sql(Read("003-projection.sql"));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dropping the schemas is the whole rollback: everything in them is derived from journal.*, which is
        // untouched here. A re-projection rebuilds it without going near the source system.
        migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS analytics.object_lifecycle");
        migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS analytics.object_timeline");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS analytics CASCADE");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS ocel CASCADE");
    }

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

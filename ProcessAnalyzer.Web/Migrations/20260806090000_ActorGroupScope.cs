using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Scoping every question to one group of people, so a part of the organisation can be read on its own instead of
/// disappearing under the volume of whichever group produces the most events.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806090000_ActorGroupScope")]
public partial class ActorGroupScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("020-actor-group-scope.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS analytics.event_in_group(text, text);
            DROP FUNCTION IF EXISTS analytics.case_touched_by_group(text, text);
            DROP INDEX IF EXISTS ocel.e2o_object_event_idx;
            DROP INDEX IF EXISTS ocel.e2o_event_object_idx;
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

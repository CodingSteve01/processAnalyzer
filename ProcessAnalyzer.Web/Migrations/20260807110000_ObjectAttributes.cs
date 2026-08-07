using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// What a case IS, next to what happened to it.
/// </summary>
/// <remarks>
/// Everything here described events so far and nothing described the object, so every document kind the source knows was
/// the same process and a posted purchase invoice could not be told apart from a posted sales credit memo. The mirror now
/// carries the classifications the source states on its object references,
/// and the analysis can filter and group by them.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260807110000_ObjectAttributes")]
public partial class ObjectAttributes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("028-object-attributes.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Back to a scope without the property, and the four-argument predicates as their only shape.
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS analytics.event_in_scope(text, text, text, text, text, text);
            DROP FUNCTION IF EXISTS analytics.case_in_scope(text, text, text, text, text, text);
            DROP FUNCTION IF EXISTS analytics.case_has_property(text, text, text);
            DROP VIEW IF EXISTS analytics.property_coverage;
            DROP VIEW IF EXISTS analytics.case_property;
            DROP FUNCTION IF EXISTS ocel.project_object_attributes();
            DROP TABLE IF EXISTS ocel.object_attribute;
            DROP INDEX IF EXISTS journal.ix_journal_eo_classified;
            ALTER TABLE journal.event_object DROP COLUMN IF EXISTS attributes;
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

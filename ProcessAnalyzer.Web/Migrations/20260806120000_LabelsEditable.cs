using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// Naming from inside the tool: labels typed here survive the next startup, and what is still unnamed is a list
/// somebody can work through instead of a warning sign on a screen.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806120000_LabelsEditable")]
public partial class LabelsEditable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("021-labels-editable.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP VIEW IF EXISTS analytics.naming_gap;
            ALTER TABLE ocel.label DROP CONSTRAINT IF EXISTS label_source_known;
            ALTER TABLE ocel.label
                DROP COLUMN IF EXISTS source,
                DROP COLUMN IF EXISTS file_label_de,
                DROP COLUMN IF EXISTS file_hint_de,
                DROP COLUMN IF EXISTS updated_at;
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

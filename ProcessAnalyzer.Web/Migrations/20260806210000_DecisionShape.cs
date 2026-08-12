using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// A submission is not an approval, and an approval cannot precede the submission it belongs to.
/// </summary>
/// <remarks>
/// The decision screen read the first human step of a case as the submission. In 170 of 675 document cases that step is
/// itself a release, the document arrives from a scan or a job and the first person to touch it releases it, so the
/// screen reported the relationship upside down: the manager who approves appeared as the person who submitted.
/// </remarks>
[DbContext(typeof(AppDbContext))]
[Migration("20260806210000_DecisionShape")]
public partial class DecisionShape : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Read("027-decision-shape.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS analytics.is_decision(text);");

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

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcessAnalyzer.Web.Data;

#nullable disable

namespace ProcessAnalyzer.Web.Migrations;

/// <summary>
/// The scope of a question gains two steps: cases that went through one, and cases that never did. The second half is
/// what turns a figure into a comparison.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260806180000_ScopeSteps")]
public partial class ScopeSteps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("025-scope-steps.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS analytics.event_in_scope(text, text, text, text);
            DROP FUNCTION IF EXISTS analytics.case_in_scope(text, text, text, text);
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
